#!/usr/bin/env bash
# npm-range-probe.sh -- npm semver-range probe for issue #166.
# Asserts the #166 fix: raw ranges (^, ~, >=, *, latest, npm:alias) must NEVER
# be interpolated into a registry URL (pre-fix: 405 -> registry-error Unknown).
# Range path is packument GET /{name} -> max-satisfying exact -> version-doc
# lookup; Dependency keeps the DECLARED range, the concrete version rides in
# the source URL / reason (fidelity contract).
# Arms (offline-safe unless marked LIVE):
#   S1 helper-file + API anchors (IsExactVersion/TrySplitAlias/IsSupportedRange/
#      TryResolve/TryParsePackument); S2 synth packument fixture (versions list
#      + dist-tags.latest, stubbed, never live-registry totals); S3 range-vector
#      vocab anchors (^, ~, hyphen, ||, prerelease); S4 suite-stub evidence
#      (14 Facts, mocked versions/dist-tags keys, CallCount pins).
#   R1-R5 mocked-suite vectors via dotnet test --filter (stubbed HttpMessageHandler,
#      zero live HTTP): caret x3, star/empty x2, latest+alias x3, fallback x2,
#      hyphen/tilde/union/prerelease x4 (= 14 planned-new).
#   U1 source: no raw range token on registry URL lines; U2 suite pins
#      no-%5E per request URL (AssertNoEncodedRangeArtifacts).
#   N1 source: version-doc 404/405 paths return Unknown, throw allowlist holds
#      (ctor ArgumentNullException + cancellation rethrow only); N2 LIVE phantom
#      range (packument 404 -> not-found Unknown, exit 0, no throw).
#   C1 LIVE CLI smoke DLL-direct on tests/Olaf.Tests/Fixtures/npm (has ^, ~,
#      >= ranges): exit 0, declared ranges kept, sourceUrls carry exact /v/X.Y.Z.
#   R1 canonical format-matrix regression (json/yaml/xml/html).
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
Usage: npm-range-probe.sh [options]

npm semver-range probe (issue #166): ranges resolve via packument to
max-satisfying exact; raw ranges never hit registry URLs (no 405).

Options:
  --repo-root <dir>   Repo root under test (default: git top-level or
                      script-relative fallback; must contain olaf.slnx)
  --timeout <secs>    Per-scan timeout in seconds (default: 180)
  --workdir <dir>     Work dir (default: mktemp under ${TMPDIR:-/tmp})
  --keep-temp         Keep temp work dir for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Examples:
  npm-range-probe.sh
  npm-range-probe.sh --repo-root ../olaf-166 --timeout 120 --keep-temp

Exit codes: 0 PASS, 1 FAIL (probe assertion failed), 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "npm-range-probe.sh $VERSION"; exit 0 ;;
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

# ---- Canonical pass/fail (from _template.sh) ----
fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

# ---- Canonical temp-dir with KEEP_TEMP (from _template.sh) ----
if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/npm-range-XXXXXX")"
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
# reflect genuine resolution, never cross-tree disk-cache entries (proven in
# prototype: pre-fix main read post-fix range records from the shared cache
# and false-PASSed C1). XDG_CACHE_HOME wins on ALL OSes per DiskLicenseCache.
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

HELPER="$REPO_ROOT/src/Olaf.Resolvers/NpmSemverRange.cs"
RESOLVER="$REPO_ROOT/src/Olaf.Resolvers/NpmLicenseResolver.cs"
TESTS="$REPO_ROOT/tests/Olaf.Tests/Resolvers/NpmRangeResolverTests.cs"
CSPROJ="$REPO_ROOT/tests/Olaf.Tests/Olaf.Tests.csproj"
FIXTURE="$REPO_ROOT/tests/Olaf.Tests/Fixtures/npm"

echo "== npm-range-probe v$VERSION =="
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
if [[ ! -d "$FIXTURE" ]]; then fail_msg "range fixture missing: $FIXTURE"; else pass "range fixture present: tests/Olaf.Tests/Fixtures/npm"; fi

# run_scan for R1 + CLI smoke (writes $WORKDIR/$tag.stdout, sets RC).
run_scan() {
  local tag="$1" indir="$2" fmt="$3"; shift 3
  run_gate "$WORKDIR/$tag.stdout" -- timeout "$TIMEOUT_SECS" dotnet "$CLI_DLL" generate "$indir" --format "$fmt" "$@"
}

echo "-- S arms: static surface (offline-safe, stubbed) --"
# S1: helper file + API anchors.
if [[ -f "$HELPER" ]]; then
  MISSING=""
  for anchor in "IsExactVersion" "TrySplitAlias" "IsSupportedRange" "TryResolve" "TryParsePackument"; do
    if ! grep -q -- "$anchor" "$HELPER"; then MISSING="$MISSING $anchor"; fi
  done
  if [[ -z "$MISSING" ]]; then
    pass "S1 helper + 5 API anchors (IsExactVersion/TrySplitAlias/IsSupportedRange/TryResolve/TryParsePackument)"
  else
    fail_msg "S1 helper missing anchors:$MISSING"
  fi
else
  fail_msg "S1 helper missing: src/Olaf.Resolvers/NpmSemverRange.cs"
fi
# S2: synth packument fixture (versions list + dist-tags.latest), shape-gated.
cat > "$WORKDIR/packument.json" <<'JSON'
{"versions":{"1.0.0":{},"1.2.0":{},"1.2.3":{},"2.0.0-beta.1":{},"2.0.0":{}},"dist-tags":{"latest":"1.2.3"}}
JSON
set +e
python3 - "$WORKDIR/packument.json" <<'PY' >"$WORKDIR/s2.out" 2>&1
import json, sys
data = json.load(open(sys.argv[1]))
versions = data.get("versions", {})
tags = data.get("dist-tags", {})
assert isinstance(versions, dict) and len(versions) >= 2, "versions list too small"
latest = tags.get("latest", "")
assert isinstance(latest, str) and latest.count(".") == 2, "dist-tags.latest not exact"
print("synth packument: %d versions, latest=%s" % (len(versions), latest))
PY
S2RC=$?
set -e
if [[ "$S2RC" -eq 0 ]]; then
  pass "S2 synth packument stubbed/offline-safe ($(cat "$WORKDIR/s2.out"))"
else
  fail_msg "S2 synth packument shape bad ($(cat "$WORKDIR/s2.out"))"
fi
# S3: range-vector vocab anchors in the helper.
S3MISS=""
for vocab in '"^"' '"~"' "hyphen" '||' "rerelease"; do
  if ! grep -qi -- "$vocab" "$HELPER"; then S3MISS="$S3MISS $vocab"; fi
done
if [[ -z "$S3MISS" ]]; then
  pass "S3 range-vector vocab anchored (^, ~, hyphen, ||, prerelease)"
else
  fail_msg "S3 helper missing vocab:$S3MISS"
fi
# S4: suite-stub evidence (14 Facts, mocked keys, CallCount pins).
if [[ -f "$TESTS" ]]; then
  FACTS="$(grep -c -e '\[Fact\]' "$TESTS" || true)"
  if [[ "$FACTS" -eq 14 ]]; then pass "S4a suite carries 14 Facts (= plan-table 3+2+1+2+1+1+2+1+1)"; else fail_msg "S4a suite Fact count $FACTS (want 14)"; fi
  if grep -q 'PackumentJson' "$TESTS" && grep -q 'dist-tags' "$TESTS" && grep -q 'versions' "$TESTS"; then
    pass "S4b mocked packuments carry versions + dist-tags keys (stubbed, never live totals)"
  else
    fail_msg "S4b mocked packument keys missing (versions/dist-tags)"
  fi
  if grep -q 'CallCount' "$TESTS"; then
    pass "S4c HTTP-budget CallCount pins present (1 packument + 0/1 version-doc, offline 0)"
  else
    fail_msg "S4c CallCount pins missing"
  fi
else
  fail_msg "S4 suite missing: tests/Olaf.Tests/Resolvers/NpmRangeResolverTests.cs"
fi

echo "-- R arms: mocked range vectors (stubbed HTTP, zero live traffic) --"
run_vector() {
  local label="$1" expected="$2" filter="$3"
  local tag="rvec-$(echo "$label" | tr ' ' '-')"
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
run_vector "R-caret (^ incl ^0.x special-case)" 3 "FullyQualifiedName~Should_ResolveCaret"
run_vector "R-star (* + empty)" 2 "FullyQualifiedName~Should_ResolveStar|FullyQualifiedName~Should_ResolveEmpty"
run_vector "R-tagalias (latest + npm:alias x2)" 3 "FullyQualifiedName~Should_ResolveLatestDistTag|FullyQualifiedName~Should_ResolveNpmAlias"
run_vector "R-fallback (unparseable->latest + unsatisfiable->not-found)" 2 "FullyQualifiedName~Should_FallbackToLatest|FullyQualifiedName~Should_ReturnNotFound"
run_vector "R-misc (hyphen + tilde + union + prerelease)" 4 "FullyQualifiedName~Should_ResolveHyphen|FullyQualifiedName~Should_ResolveTilde|FullyQualifiedName~Should_ResolveUnion|FullyQualifiedName~Should_ExcludePrerelease"

echo "-- U arms: raw-range-never-in-URL (the #166 defect) --"
# U1 source: registry URL lines carry only lookupName/exactVersion inside EscapeDataString.
U1BAD="$(grep -n 'registry.npmjs.org/' "$RESOLVER" | grep -E 'dependency\.Version|rawSpec|\{spec|\{range|Spec\}|Range\}' || true)"
U1ESC="$(grep -c 'registry.npmjs.org/.*EscapeDataString' "$RESOLVER" || true)"
if [[ -z "$U1BAD" && "$U1ESC" -ge 2 ]]; then
  pass "U1 registry URL lines range-free (2 EscapeDataString lookups, no raw Version/rawSpec)"
else
  fail_msg "U1 raw-range token on registry URL line (esc=$U1ESC): $U1BAD"
fi
# U2 suite pins no-encoded-range per request URL.
if grep -q 'AssertNoEncodedRangeArtifacts' "$TESTS" && grep -q '%5E' "$TESTS"; then
  pass "U2 suite pins no-%5E/no-caret/no-space per request URL"
else
  fail_msg "U2 no-encoded-range pin missing from suite"
fi

echo "-- N arms: 404/405 regression (Unknown, no throw) --"
# N1 source: NotFound->not-found in both lookup paths; error paths return Unknown.
NF_NOTFOUND="$(grep -c 'not-found: npm package' "$RESOLVER" || true)"
NF_REGERR="$(grep -c 'registry-error: npm returned' "$RESOLVER" || true)"
THROWBAD="$(grep -n 'throw' "$RESOLVER" | grep -v -E 'ArgumentNullException|:[[:space:]]*throw;' || true)"
if [[ "$NF_NOTFOUND" -ge 2 && "$NF_REGERR" -ge 2 && -z "$THROWBAD" ]]; then
  pass "N1 version-doc 404/405 -> Unknown (not-found x$NF_NOTFOUND, registry-error x$NF_REGERR, throw allowlist holds)"
else
  fail_msg "N1 404/405 mapping off (notfound=$NF_NOTFOUND regerr=$NF_REGERR throwbad=$THROWBAD)"
fi
# N2 LIVE: phantom range -> packument 404 -> not-found Unknown, exit 0, no throw.
PHANTOM="$WORKDIR/phantom-range"
mkdir -p "$PHANTOM"
printf '{\n  "name": "olaf-range-phantom",\n  "version": "1.0.0",\n  "dependencies": {\n    "this-package-definitely-does-not-exist-olaf-xyz": "^9.9.9"\n  }\n}\n' > "$PHANTOM/package.json"
run_scan "n2-phantom" "$PHANTOM" "json"
if [[ "$RC" -ne 0 ]]; then
  fail_msg "N2 phantom-range scan exited $RC (want 0)"
elif ! grep -q '"status": "Unknown"' "$WORKDIR/n2-phantom.stdout" && ! grep -q '"Unknown"' "$WORKDIR/n2-phantom.stdout"; then
  fail_msg "N2 phantom-range stdout missing Unknown"
elif grep -qi 'Unhandled exception\|System\.NullReference\|Object reference' "$WORKDIR/n2-phantom.stdout"; then
  fail_msg "N2 phantom-range threw (exception text in output)"
else
  pass "N2 phantom-range ^9.9.9 -> Unknown, exit 0, no throw (packument 404)"
fi

echo "-- C1 LIVE CLI smoke: synth range-only fixture resolves to exact versions --"
# NOTE: the committed npm fixture dir carries lock files and the parser prefers
# lock > manifest (exact versions, range path untouched), so C1 synthesizes a
# lock-free package.json with bare ranges (plan Step 3: use it OR a synth dir).
RANGE_FIX="$WORKDIR/range-fixture"
mkdir -p "$RANGE_FIX"
printf '{\n  "name": "olaf-range-smoke",\n  "version": "1.0.0",\n  "dependencies": {\n    "express": "^4.18.2",\n    "lodash": "~4.17.21",\n    "react": ">=18.2.0"\n  }\n}\n' > "$RANGE_FIX/package.json"
run_scan "c1-range" "$RANGE_FIX" "json"
if [[ "$RC" -ne 0 ]]; then
  fail_msg "C1 range-fixture scan exited $RC (want 0)"
else
  set +e
  python3 - "$WORKDIR/c1-range.stdout" <<'PY' >"$WORKDIR/c1.out" 2>&1
import json, re, sys
data = json.load(open(sys.argv[1]))
items = data.get("licenses", [])
byname = {it.get("name", ""): it for it in items if isinstance(it, dict)}
exact = re.compile(r"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")
fails = []
def check(cond, msg):
    print(("PASS " if cond else "FAIL ") + msg)
    if not cond:
        fails.append(msg)
check(data.get("summary", {}).get("total", 0) == 3, "total==3 (synth range-only deps)")
for name, declared in [("express", "^4.18.2"), ("lodash", "~4.17.21"), ("react", ">=18.2.0")]:
    it = byname.get(name)
    check(it is not None, "%s present" % name)
    if it is None:
        continue
    check(it.get("version", "") == declared, "%s keeps declared %s" % (name, declared))
    src = it.get("sourceUrl", "") or ""
    m = re.search(r"/v/(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$", src)
    check(bool(m and exact.match(m.group(1))), "%s sourceUrl carries exact (%s)" % (name, src))
check(data.get("summary", {}).get("resolved", 0) >= 3, "resolved>=3")
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
# dep_ver cross-check (canonical extractor reuse): declared ranges verbatim.
if [[ "$(dep_ver "$WORKDIR/c1-range.stdout" "express")" == "^4.18.2" ]]; then
  pass "C1 dep_ver cross-check: express keeps ^4.18.2"
else
  fail_msg "C1 dep_ver cross-check: express version [$(dep_ver "$WORKDIR/c1-range.stdout" "express")] (want ^4.18.2)"
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
if [[ "$fail" -eq 0 ]]; then
  echo "NPM-RANGE-PROBE OK"
  exit 0
else
  echo "NPM-RANGE-PROBE FAIL" >&2
  exit 1
fi
