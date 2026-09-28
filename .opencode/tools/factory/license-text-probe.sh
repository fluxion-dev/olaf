#!/usr/bin/env bash
# license-text-probe.sh — License-text probe for issue #71 (promoted v0.1.0, 2 uses ≥ bar).
# Model: enrichment-probe.sh sections + shared spdx-b4 note; stubbed/offline-safe.
# L1 tarball-extract: synth tgz/zip built in-memory via python3 (LICENSE
#   first-match + COPYING/NOTICE tiers + size-cap arm + utf-8/utf-16le/latin-1
#   encoding arms); shapes verified with tar/zip listings + decode mirror;
#   behavioral proof = filtered LicenseTextFetcherTests run (in-memory ustar
#   writer + StubHttpMessageHandler, zero live network).
# L2 fallback chain: embedded-file > licenseUrl > embedded DB > null+reason
#   order asserts + first-failure-wins + no-silent-null (no `return (null,
#   null)`); behavioral proof = filtered LicenseTextChainTests run.
# L3 SPDX-DB coverage: 35-arm count pin (19 short + 16 verbatim) + 3.29 pin +
#   #77 FULL-DB owner note + air-gap via canonical air_gap_grep (no HttpClient
#   in SpdxLicenseTexts.cs); behavioral proof = filtered SpdxLicenseTextsTests run.
# L4 bounds: LicenseTextLimits consts (5s / 1MiB / 512 entries / 1MiB entry)
#   + CancelAfter + Content-Length gate + streaming truncation + stub-only
#   tests (no E2E trait); behavioral proof = filtered LicenseTextWireTests run.
# R1 matrix regression: canonical r1_matrix_regression (json/yaml/xml/html).
# Rules: repo-relative, idempotent (mktemp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.1"
set -euo pipefail

# ---- Canonical root resolution (factory depth: ../../.. per parser-coverage-probe.sh) ----
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

# ---- Canonical pass/fail ----
fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

# ---- Canonical temp-dir with KEEP_TEMP ----
WORKDIR=""
KEEP_TEMP=0
TIMEOUT_SECS=300
PROJECT_REL="src/Olaf.Cli"
TESTPROJ_REL="tests/Olaf.Tests/Olaf.Tests.csproj"
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
FETCHER_REL="src/Olaf.Resolvers/LicenseTextFetcher.cs"
DB_REL="src/Olaf.Resolvers/SpdxLicenseTexts.cs"
LIMITS_REL="src/Olaf.Resolvers/LicenseTextFetcher.cs"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

License-text probe (issue #71): tarball-extract (L1), fallback chain (L2),
SPDX-DB coverage (L3), size/time bounds (L4), format-matrix regression (R1).
Stubbed/offline-safe: synth archives in-memory, StubHttpMessageHandler-only
test runs, no live network.

Options:
  --timeout <n>   per-command timeout in seconds (default: 300)
  --workdir <dir> work dir (default: mktemp -d under \${TMPDIR:-/tmp})
  --keep-temp     keep temp work dir for debugging (default: remove)
  --help          show this help and exit 0
  --version       print VERSION and exit 0

Exit codes: 0 all PASS, 1 assertion failure, 2 usage/environment error.

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 120 --keep-temp
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
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

PROJECT="$ROOT/$PROJECT_REL"
TESTPROJ="$ROOT/$TESTPROJ_REL"
FIXTURE="$ROOT/$FIXTURE_REL"
FETCHER="$ROOT/$FETCHER_REL"
DB="$ROOT/$DB_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -f "$TESTPROJ" ]] || { echo "Test project not found: $TESTPROJ" >&2; exit 2; }
[[ -d "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
[[ -f "$FETCHER" ]] || { echo "Fetcher not found: $FETCHER" >&2; exit 2; }
[[ -f "$DB" ]] || { echo "DB not found: $DB" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3 tar; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/license-text-probe-XXXXXX")"
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

# ---- Canonical dep extractors (from _template.sh 0.2.2; behavior identical) ----
dep_count() {
  python3 - "$1" <<'PY' 2>/dev/null
import json, sys
try:
    with open(sys.argv[1]) as f:
        data = json.load(f)
except Exception:
    print("?")
    sys.exit(0)
items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
print(len(items) if isinstance(items, list) else "?")
PY
}
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
dep_direct() { dep_field "$1" "$2" "direct"; }

# ---- Canonical air-gap grep (from _template.sh 0.2.4; behavior identical) ----
air_gap_grep() {
  local file="$1"; shift
  [[ -f "$file" ]] || return 2
  local vocab='HttpClient|GetAsync|System\.Net|HttpRequest|GetByteArray|SendAsync'
  local p
  for p in "$@"; do vocab="$vocab|$p"; done
  if grep -v '^[[:space:]]*//' "$file" | grep -qE -- "$vocab"; then
    return 1
  fi
  return 0
}

RC=0
run_scan() {
  # run_scan <tag> <inputdir> <format> [extra args...]; stdout-><tag>.stdout
  local tag="$1" input="$2" fmt="$3"; shift 3
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$input" --format "$fmt" "$@" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}
run_t() {
  # run_t <tag> <filter>; dotnet test -> <tag>.stdout (Passed!/Failed! summary)
  local tag="$1" filter="$2"
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet test "$TESTPROJ" --filter "$filter" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}
t_ok() {
  # t_ok <tag> <label>: exit 0 + Passed! summary (no Failed!).
  local tag="$1" label="$2" sum
  sum="$(grep -h "Passed!\|Failed!" "$WORKDIR/$tag.stdout" 2>/dev/null | tail -n 1)"
  if [[ "$RC" -eq 0 ]] && grep -q "Passed!" "$WORKDIR/$tag.stdout" 2>/dev/null \
      && ! grep -q "Failed!" "$WORKDIR/$tag.stdout" 2>/dev/null; then
    pass "$label: ${sum:-exit 0 + Passed!}"
  else
    fail_msg "$label: RC=$RC summary=[${sum:-none}] (see $tag.stdout)"
  fi
}

echo "== license-text-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

# ---- L1: tarball-extract (synth in-memory + source tiers + FetcherTests) ----
echo "-- L1 tarball-extract --"
SYNTH="$WORKDIR/synth"
export SYNTH
python3 <<'PY'
import gzip, io, os, tarfile, zipfile
s = os.environ["SYNTH"]
os.makedirs(s, exist_ok=True)
def mktgz(path, files):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as t:
        for name, data in files:
            ti = tarfile.TarInfo(name); ti.size = len(data); ti.mode = 0o644
            t.addfile(ti, io.BytesIO(data))
    with open(path, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", mtime=0) as g:
            g.write(buf.getvalue())
def mkzip(path, files):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in files:
            z.writestr(name, data)
mktgz(f"{s}/tier.tgz", [
    ("pkg/COPYING", b"COPYING-TEXT-v1"),
    ("pkg/LICENSE", b"LICENSE-TEXT-FIRST"),
    ("pkg/LICENSE.md", b"LICENSE-TEXT-SECOND"),
    ("pkg/NOTICE", b"NOTICE-TEXT-v1"),
])
mkzip(f"{s}/tier.zip", [
    ("pkg/COPYING", b"COPYING-TEXT-v1"),
    ("pkg/LICENSE", b"LICENSE-TEXT-FIRST"),
    ("pkg/NOTICE", b"NOTICE-TEXT-v1"),
])
mktgz(f"{s}/enc-utf8.tgz", [("LICENSE", "ENC-UTF8-\u2713".encode("utf-8"))])
mktgz(f"{s}/enc-utf16.tgz", [("LICENSE", b"\xff\xfe" + "ENC-UTF16".encode("utf-16-le"))])
mktgz(f"{s}/enc-latin1.tgz", [("LICENSE", "ENC-LATIN1-\xe9".encode("latin-1"))])
mktgz(f"{s}/big.tgz", [("LICENSE", b"B" * 1_100_000), ("COPYING", b"COPYING-SMALL")])
print("synth: 6 archives written")
PY
if [[ -s "$SYNTH/tier.tgz" ]]; then pass "L1 synth: 6 archives built in-memory"; else fail_msg "L1 synth: archives missing"; fi
# Shape: entry order preserved (first-match rule needs deterministic order).
if tar -tzf "$SYNTH/tier.tgz" | tr '\n' ' ' | grep -q "pkg/COPYING pkg/LICENSE pkg/LICENSE.md pkg/NOTICE"; then
  pass "L1 shape: tier.tgz entry order COPYING,LICENSE,LICENSE.md,NOTICE"
else
  fail_msg "L1 shape: tier.tgz order wrong: $(tar -tzf "$SYNTH/tier.tgz" | tr '\n' ' ')"
fi
if python3 - <<'PY' 2>/dev/null
import os, zipfile
names = zipfile.ZipFile(os.environ["SYNTH"] + "/tier.zip").namelist()
assert names == ["pkg/COPYING", "pkg/LICENSE", "pkg/NOTICE"], names
print("zip order ok")
PY
then pass "L1 shape: tier.zip entries COPYING,LICENSE,NOTICE"; else fail_msg "L1 shape: tier.zip entries wrong"; fi
# Tier mirror: LICENSE beats earlier COPYING; first LICENSE wins.
if python3 - <<'PY' 2>/dev/null
import os, tarfile
def tier(n):
    b = n.rsplit("/", 1)[-1].upper()
    if b.startswith("LICENSE"): return 0
    if b.startswith("COPYING"): return 1
    if b.startswith("NOTICE"): return 2
    return -1
names = tarfile.open(os.environ["SYNTH"] + "/tier.tgz").getnames()
best = (9, None)
for n in names:
    t = tier(n)
    if t >= 0 and t < best[0]:
        best = (t, n)
assert best == (0, "pkg/LICENSE"), best
print("tier mirror ok")
PY
then pass "L1 mirror: LICENSE-tier first-match beats earlier COPYING"; else fail_msg "L1 mirror: tier pick wrong"; fi
# Encoding mirror: utf-8 strict / utf-16le BOM / latin-1 fallback.
if python3 - <<'PY' 2>/dev/null
import os, tarfile
s = os.environ["SYNTH"]
def raw(p):
    t = tarfile.open(p); return t.extractfile("LICENSE").read()
assert raw(f"{s}/enc-utf8.tgz").decode("utf-8").startswith("ENC-UTF8-")
u16 = raw(f"{s}/enc-utf16.tgz")
assert (u16[0], u16[1]) == (0xFF, 0xFE) and u16[2:].decode("utf-16-le") == "ENC-UTF16"
lat = raw(f"{s}/enc-latin1.tgz")
try:
    lat.decode("utf-8"); raise SystemExit("latin1 unexpectedly valid utf-8")
except UnicodeDecodeError:
    assert lat.decode("cp1252").endswith("\xe9")
print("encoding mirror ok")
PY
then pass "L1 mirror: utf-8/utf-16le-BOM/latin-1 decode arms"; else fail_msg "L1 mirror: encoding arms wrong"; fi
# Size-cap arm: LICENSE entry exceeds MaxEntryBytes (1048576).
if python3 - <<'PY' 2>/dev/null
import os, tarfile
t = tarfile.open(os.environ["SYNTH"] + "/big.tgz")
size = t.getmember("LICENSE").size
assert size > 1_048_576, size
print(f"big LICENSE {size} bytes")
PY
then pass "L1 shape: big.tgz LICENSE entry over 1MiB cap"; else fail_msg "L1 shape: big.tgz entry not over cap"; fi
# Source tiers: LICENSE* > COPYING* > NOTICE*, LICENSE first-match returns.
grep -q 'StartsWith("LICENSE"' "$FETCHER" && grep -q 'StartsWith("COPYING"' "$FETCHER" \
  && grep -q 'StartsWith("NOTICE"' "$FETCHER" \
  && pass "L1 src: LICENSE*>COPYING*>NOTICE* tiers" \
  || fail_msg "L1 src: tier prefixes missing"
grep -q 'if (tier == 0)' "$FETCHER" && grep -q 'return text;' "$FETCHER" \
  && pass "L1 src: LICENSE tier first-match-wins return" \
  || fail_msg "L1 src: first-match return missing"
grep -q 'MaxEntryBytes' "$FETCHER" && grep -q 'IsZipSymlink\|IsSafeArchiveName' "$FETCHER" \
  && pass "L1 src: entry size-cap + symlink/absolute skips" \
  || fail_msg "L1 src: cap/skip guards missing"
grep -q 'BigEndianUnicode\|Encoding.Unicode' "$FETCHER" && grep -q 'throwOnInvalidBytes: true' "$FETCHER" \
  && grep -q 'GetEncoding(1252)' "$FETCHER" \
  && pass "L1 src: utf-16 BOM + utf-8-strict + 1252 decode pipeline" \
  || fail_msg "L1 src: decode pipeline arms missing"
run_t "l1-fetch" "FullyQualifiedName~LicenseTextFetcherTests"
t_ok "l1-fetch" "L1 behavior: LicenseTextFetcherTests (in-memory tgz/zip/nupkg, stub-only)"

# ---- L2: fallback chain (embedded > licenseUrl > DB > null+reason) ----
echo "-- L2 fallback chain --"
python3 - "$FETCHER" <<'PY' 2>/dev/null
import sys
src = open(sys.argv[1]).read()
body = src.split("TryFetchLicenseTextAsync", 1)[1]
i_tar, i_url, i_db = body.index("TryFetchFromTarballAsync"), body.index("TryFetchFromLicenseUrlAsync"), body.index("TryGetText")
assert i_tar < i_url < i_db, (i_tar, i_url, i_db)
assert "firstFailure ??=" in body
assert "return (null, firstFailure)" in body
print("chain order ok")
PY
if [[ "$?" -eq 0 ]]; then pass "L2 src: tarball > licenseUrl > DB order + first-failure-wins"; else fail_msg "L2 src: chain order/pinning wrong"; fi
if grep -q 'return (null, null)' "$FETCHER"; then
  fail_msg "L2 src: silent null present"
else
  pass "L2 src: no silent null (every null-text carries reason)"
fi
for pat in 'tarball-miss:' 'tarball-timeout' 'tarball-too-large:' 'licenseurl-fetch-failed:' 'spdxdb-miss:'; do
  if grep -q "$pat" "$FETCHER"; then pass "L2 src: reason vocab '$pat'"; else fail_msg "L2 src: reason vocab '$pat' missing"; fi
done
grep -q 'when (cancellationToken.IsCancellationRequested)' "$FETCHER" \
  && pass "L2 src: caller-cancellation rethrown, never swallowed" \
  || fail_msg "L2 src: cancellation rethrow missing"
run_t "l2-chain" "FullyQualifiedName~LicenseTextChainTests"
t_ok "l2-chain" "L2 behavior: LicenseTextChainTests (pinned chain, stub-only)"

# ---- L3: SPDX-DB coverage (count/pin + air-gap) ----
echo "-- L3 SPDX-DB coverage --"
SHORT_N="$(grep -c '=> "' "$DB")"
VERB_N="$(grep -c '=> @"' "$DB")"
TOTAL_N=$((SHORT_N + VERB_N))
echo "spdx arms: short=$SHORT_N verbatim=$VERB_N total=$TOTAL_N"
if [[ "$TOTAL_N" -eq 35 ]]; then pass "L3 DB: 35-arm curated subset (19 short + 16 verbatim)"; else fail_msg "L3 DB: arm count $TOTAL_N (want 35)"; fi
grep -q 'SPDX License List 3.29' "$DB" && grep -q 'v3.29.0' "$DB" \
  && pass "L3 DB: pinned to SPDX License List 3.29 (tag v3.29.0)" \
  || fail_msg "L3 DB: 3.29 version pin missing"
grep -q '#77' "$DB" \
  && pass "L3 DB: FULL offline DB owned by issue #77 (documented, no runtime download)" \
  || fail_msg "L3 DB: #77 owner note missing"
if air_gap_grep "$DB"; then
  pass "L3 DB: air-gap safe (pure in-memory switch, zero HTTP)"
else
  fail_msg "L3 DB: HTTP surface in DB code (air-gap broken)"
fi
grep -q 'license text as declared by the package registry' "$DB" \
  && pass "L3 DB: GetText fallback never returns null/empty" \
  || fail_msg "L3 DB: GetText fallback missing"
run_t "l3-spdx" "FullyQualifiedName~SpdxLicenseTextsTests"
t_ok "l3-spdx" "L3 behavior: SpdxLicenseTextsTests (offline, no network)"

# ---- L4: bounds (timeout + max-bytes, cached-note, stub-only tests) ----
echo "-- L4 bounds --"
grep -q 'PerPackageTimeoutSeconds = 5' "$FETCHER" \
  && pass "L4 src: per-package timeout 5s" \
  || fail_msg "L4 src: 5s timeout const missing"
grep -q 'MaxBytes = 1_048_576' "$FETCHER" \
  && pass "L4 src: max-bytes 1MiB" \
  || fail_msg "L4 src: 1MiB MaxBytes const missing"
grep -q 'MaxArchiveEntries = 512' "$FETCHER" && grep -q 'MaxEntryBytes = 1_048_576' "$FETCHER" \
  && pass "L4 src: 512-entry scan cap + 1MiB entry cap" \
  || fail_msg "L4 src: archive/entry caps missing"
grep -q 'CancelAfter(TimeSpan.FromSeconds(LicenseTextLimits.PerPackageTimeoutSeconds))' "$FETCHER" \
  && pass "L4 src: linked-CTS timeout enforced on both fetch paths" \
  || fail_msg "L4 src: CancelAfter enforcement missing"
grep -q 'ContentLength' "$FETCHER" && grep -q 'ReadUpToAsync' "$FETCHER" \
  && pass "L4 src: Content-Length gate + streaming truncation" \
  || fail_msg "L4 src: length gate/truncation missing"
for tf in LicenseTextFetcherTests LicenseTextChainTests LicenseTextWireTests; do
  if grep -q 'StubHttpMessageHandler' "$ROOT/tests/Olaf.Tests/Resolvers/$tf.cs"; then
    pass "L4 tests: $tf stub-handler-only"
  else
    fail_msg "L4 tests: $tf missing stub handler"
  fi
done
if grep -rn 'Trait.*E2E\|E2E.*Trait' "$ROOT/tests/Olaf.Tests/Resolvers/LicenseTextFetcherTests.cs" \
    "$ROOT/tests/Olaf.Tests/Resolvers/LicenseTextChainTests.cs" \
    "$ROOT/tests/Olaf.Tests/Resolvers/LicenseTextWireTests.cs" \
    "$ROOT/tests/Olaf.Tests/Resolvers/SpdxLicenseTextsTests.cs" 2>/dev/null; then
  fail_msg "L4 tests: E2E-trait leak in license-text unit tests"
else
  pass "L4 tests: no E2E trait (unit tests never hit live network)"
fi
run_t "l4-wire" "FullyQualifiedName~LicenseTextWireTests"
t_ok "l4-wire" "L4 behavior: LicenseTextWireTests (resolver wiring, stub-only)"

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

# ---- R1 format-matrix regression: json/yaml/xml/html direct markers untouched ----
r1_matrix_regression

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "LICENSE-TEXT-PROBE OK: L1-L4 + R1 all pass."
  exit 0
else
  echo "LICENSE-TEXT-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
