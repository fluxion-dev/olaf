#!/usr/bin/env bash
# spdx-probe.sh — SPDX JSON probe for issue #69 (promoted v0.1.0, 2 uses ≥ bar).
# Model: cyclonedx-probe.sh JSON B-structure; cdx_check swapped for spdx_check.
# Offline-safe: never passes --strict; resolver failures degrade to Unknown, exit 0
#   (B4 explicit pins SKIP when the live-registry value is degraded — generic
#   ref<->output consistency via spdx_check still runs).
#   B1 envelope: spdxVersion==SPDX-2.3 + dataLicense==CC0-1.0 + SPDXID==SPDXRef-DOCUMENT
#      + name==olaf-scan + documentNamespace UUID shape + creationInfo.created
#      ISO-8601 + creators Tool: olaf + packages/relationships arrays on npm fixture
#   B2 packages count == json scan count; names/versions match (npm fixture + go indirect synth)
#   B3 DESCRIBES+CONTAINS closure: every package gets BOTH from SPDXRef-DOCUMENT,
#      every endpoint resolves, documentDescribes == package IDs
#      (empty-scan self-DESCRIBES is N/A — CLI exits 2 pre-formatter on manifest-less input)
#   B4 licenses: single-token->passthrough, expression->passthrough, multi-word->NOASSERTION,
#      Unknown->NOASSERTION, licenseDeclared mirrors licenseConcluded (+ shared spdx-b4
#      synth end-to-end: font-awesome expr + python-dateutil "Dual License" + phantom Unknown)
#   B5 purl externalRefs (PACKAGE-MANAGER/purl/pkg:...@...) + SPDXID uniqueness (positional
#      SPDXRef-Package-N, no bom-ref charset issue by design)
#   B6 --direct-only subset on go synth + both-flags conflict exit 2
#   B7 unknown-format (toml) exit 2 + --help lists spdx-json
#   R1 format-matrix regression untouched (json/yaml/xml/html markers MATRIX OK)
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
TIMEOUT_SECS=60
PROJECT_REL="src/Olaf.Cli"
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
B4FX_REL="tests/Olaf.Tests/Fixtures/spdx-b4"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

SPDX JSON probe (issue #69): envelope (B1), count/names (B2),
DESCRIBES+CONTAINS closure (B3), licenses (B4), purl/SPDXID (B5),
--direct-only + both-flags (B6), unknown-format + --help (B7),
format-matrix regression (R1).

Options:
  --timeout <n>   per-scan timeout in seconds (default: 60)
  --workdir <dir> work dir (default: mktemp -d under \${TMPDIR:-/tmp})
  --keep-temp     keep temp work dir for debugging (default: remove)
  --help          show this help and exit 0
  --version       print VERSION and exit 0

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
FIXTURE="$ROOT/$FIXTURE_REL"
B4FX="$ROOT/$B4FX_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -d "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
[[ -d "$B4FX" ]] || { echo "B4 fixture not found: $B4FX" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/spdx-probe-XXXXXX")"
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

spdx_check() {
  # spdx_check <label> <spdx-json> <ref-json>; python cross-check B1-B5.
  local label="$1" spdx="$2" ref="$3"
  python3 - "$spdx" "$ref" "$label" <<'PY'
import datetime, json, re, sys
spdx_path, ref_path, label = sys.argv[1], sys.argv[2], sys.argv[3]
fails = []
def ok(msg): print(f"PASS: {label}: {msg}")
def bad(msg): print(f"FAIL: {label}: {msg}"); fails.append(msg)
try:
    spdx = json.load(open(spdx_path))
    ref = json.load(open(ref_path))
except Exception as e:
    bad(f"unparseable JSON ({e})"); sys.exit(1)
# B1 envelope (checked per-file; npm run is authoritative, go run must match too)
if spdx.get("spdxVersion") == "SPDX-2.3": ok("spdxVersion==SPDX-2.3")
else: bad(f"spdxVersion={spdx.get('spdxVersion')!r} want SPDX-2.3")
if spdx.get("dataLicense") == "CC0-1.0": ok("dataLicense==CC0-1.0")
else: bad(f"dataLicense={spdx.get('dataLicense')!r} want CC0-1.0")
if spdx.get("SPDXID") == "SPDXRef-DOCUMENT": ok("SPDXID==SPDXRef-DOCUMENT")
else: bad(f"SPDXID={spdx.get('SPDXID')!r} want SPDXRef-DOCUMENT")
if spdx.get("name") == "olaf-scan": ok("name==olaf-scan")
else: bad(f"name={spdx.get('name')!r} want olaf-scan")
if re.match(r"^https://olaf\.example/sbom/[0-9a-fA-F-]{36}$", str(spdx.get("documentNamespace", ""))):
    ok("documentNamespace UUID shape")
else: bad(f"documentNamespace={spdx.get('documentNamespace')!r} bad shape")
ci = spdx.get("creationInfo", {}) if isinstance(spdx.get("creationInfo"), dict) else {}
created = str(ci.get("created", ""))
try:
    datetime.datetime.fromisoformat(created)
    if re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", created): ok("creationInfo.created ISO-8601")
    else: bad(f"creationInfo.created={created!r} not ISO-8601 shape")
except Exception:
    bad(f"creationInfo.created={created!r} unparseable")
creators = ci.get("creators", [])
if isinstance(creators, list) and len(creators) == 1 and str(creators[0]).startswith("Tool: olaf "):
    ok("creationInfo.creators Tool: olaf")
else: bad(f"creationInfo.creators={creators!r} want single 'Tool: olaf ...'")
pkgs = spdx.get("packages", None)
rels = spdx.get("relationships", None)
if isinstance(pkgs, list): ok(f"packages array ({len(pkgs)})")
else: bad(f"packages={type(pkgs).__name__} want array"); pkgs = []
if isinstance(rels, list): ok(f"relationships array ({len(rels)})")
else: bad(f"relationships={type(rels).__name__} want array"); rels = []
items = ref.get("licenses", ref.get("dependencies", ref.get("resolved", [])))
# B2 count
if len(pkgs) == len(items): ok(f"packages count == scan count ({len(pkgs)})")
else: bad(f"packages {len(pkgs)} != scan {len(items)}")
# B1 counts comment mirrors ScanResult (total == packages; resolved/unknown from ref statuses)
total = len(items)
resolved = sum(1 for it in items if isinstance(it, dict) and str(it.get("status", "")) == "Resolved")
unknown = sum(1 for it in items if isinstance(it, dict) and str(it.get("status", "")) == "Unknown")
want_comment = f"olaf:total={total}/resolved={resolved}/unknown={unknown}"
if spdx.get("comment") == want_comment: ok(f"comment counts {want_comment}")
else: bad(f"comment={spdx.get('comment')!r} want {want_comment!r}")
# index ref by lower name -> (name, version, spdx)
refmap = {}
for it in items:
    if not isinstance(it, dict): continue
    dep = it.get("dependency", it)
    n = str(dep.get("name", it.get("name", "")))
    v = str(dep.get("version", it.get("version", "")))
    eff = it.get("spdxId", it.get("spdx", None))
    if eff is None: eff = dep.get("spdx", None)
    refmap[n.lower()] = (n, v, eff if eff else "Unknown")
pkgmap = {}
for p in pkgs:
    if isinstance(p, dict):
        pkgmap[str(p.get("name", "")).lower()] = p
# B5 SPDXID uniqueness + positional shape
ids = [str(p.get("SPDXID", "")) for p in pkgs if isinstance(p, dict)]
if len(ids) == len(set(ids)): ok(f"SPDXID unique ({len(ids)})")
else: bad("SPDXID duplicates found")
badshape = [i for i in ids if not re.match(r"^SPDXRef-Package-\d+$", i)]
if not badshape: ok("SPDXID positional SPDXRef-Package-N")
else: bad(f"SPDXID bad shape: {badshape[:3]}")
# B3 DESCRIBES+CONTAINS closure
idset = set(ids) | {"SPDXRef-DOCUMENT"}
by_related = {}
for r in rels:
    if isinstance(r, dict):
        by_related.setdefault(str(r.get("relatedSpdxElement", "")), []).append(str(r.get("relationshipType", "")))
for pid in ids:
    kinds = sorted(by_related.get(pid, []))
    if kinds == ["CONTAINS", "DESCRIBES"]: ok(f"closure {pid} DESCRIBES+CONTAINS")
    else: bad(f"{pid} relationships={kinds} want [CONTAINS, DESCRIBES]")
bad_ep = [str(r.get("relatedSpdxElement", "")) for r in rels
          if isinstance(r, dict) and str(r.get("relatedSpdxElement", "")) not in idset]
if not bad_ep: ok("referential closure: every endpoint resolves")
else: bad(f"dangling endpoints: {bad_ep[:3]}")
describes = spdx.get("documentDescribes", [])
if isinstance(describes, list) and sorted(describes) == sorted(ids): ok("documentDescribes == package IDs")
else: bad(f"documentDescribes={describes!r} want package IDs")
# License-shape helpers (mirror SpdxJsonFormatter ToConcludedLicense)
def is_single(value):
    if not value: return False
    return all(ch.isalnum() or ch in ".-+" for ch in value)
def is_expr(value):
    import re as _re
    toks = [t for t in _re.split(r"[ \t()]+", value) if t]
    if len(toks) < 3: return False
    has_op = False
    for t in toks:
        if t in ("AND", "OR", "WITH"):
            has_op = True
            continue
        if not is_single(t): return False
    return has_op
def concluded(eff):
    if eff is None or (isinstance(eff, str) and eff.strip() == ""): return "NOASSERTION"
    t = str(eff).strip()
    if t.lower() in ("unknown", "none", "noassertion"): return "NOASSERTION"
    if is_single(t) or is_expr(t): return t
    return "NOASSERTION"
# B2 names/versions + B4 licenses + B5 purl
for key, (n, v, eff) in refmap.items():
    p = pkgmap.get(key)
    if p is None:
        bad(f"package missing for '{n}'"); continue
    if str(p.get("versionInfo", "")) == v: ok(f"name/version {n}@{v}")
    else: bad(f"{n} versionInfo {p.get('versionInfo')!r} want {v!r}")
    want_lic = concluded(eff)
    if p.get("licenseConcluded") == want_lic: ok(f"licenseConcluded {n}={want_lic!r}")
    else: bad(f"{n} licenseConcluded={p.get('licenseConcluded')!r} want {want_lic!r}")
    if p.get("licenseDeclared") == p.get("licenseConcluded"): ok(f"licenseDeclared mirrors {n}")
    else: bad(f"{n} licenseDeclared={p.get('licenseDeclared')!r} != concluded {p.get('licenseConcluded')!r}")
    if p.get("licenseConcluded") in (None, "", "NONE") or "Unknown" in str(p.get("licenseConcluded", "")):
        bad(f"{n} license leak: {p.get('licenseConcluded')!r} (never NONE/empty/Unknown)")
    refs = p.get("externalRefs", None)
    try:
        r0 = refs[0]
        loc = str(r0.get("referenceLocator", ""))
        if (r0.get("referenceCategory") == "PACKAGE-MANAGER" and r0.get("referenceType") == "purl"
                and re.match(r"^pkg:[a-z0-9]+/.+@.+", loc)):
            ok(f"purl {n}={loc}")
        else: bad(f"{n} externalRefs bad: {refs!r}")
    except Exception:
        bad(f"{n} externalRefs missing/unshaped: {refs!r}")
sys.exit(1 if fails else 0)
PY
  local prc=$?
  if [[ "$prc" -ne 0 ]]; then fail=1; fi
}

echo "== spdx-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

# ---- B1/B2 npm fixture: json + spdx-json side by side ----
run_scan "npm-json" "$FIXTURE" "json"
[[ "$RC" -eq 0 ]] && pass "B1/B2 npm-json exit 0" || fail_msg "B1/B2 npm-json exit $RC"
run_scan "npm-spdx" "$FIXTURE" "spdx-json"
[[ "$RC" -eq 0 ]] && pass "B1 npm-spdx exit 0" || fail_msg "B1 npm-spdx exit $RC"
if [[ "$RC" -eq 0 ]]; then
  NJ="$(dep_count "$WORKDIR/npm-json.stdout")"
  echo "npm json scan count: $NJ"
  spdx_check "B1-B5/npm" "$WORKDIR/npm-spdx.stdout" "$WORKDIR/npm-json.stdout"
fi

# ---- B2/B3 go indirect synth (1 direct + 1 indirect; SPDX carries no scope — subset via B6) ----
FX="$WORKDIR/fx"
mkdir -p "$FX/go"
cat > "$FX/go/go.mod" <<'EOF'
module example.com/fx

go 1.21

require example.com/direct-go-a v1.0.0

require example.com/indirect-go-b v2.0.0 // indirect
EOF
run_scan "go-json" "$FX/go" "json"
[[ "$RC" -eq 0 ]] && pass "B2 go-json exit 0" || fail_msg "B2 go-json exit $RC"
run_scan "go-spdx" "$FX/go" "spdx-json"
[[ "$RC" -eq 0 ]] && pass "B2 go-spdx exit 0" || fail_msg "B2 go-spdx exit $RC"
if [[ -s "$WORKDIR/go-json.stdout" && -s "$WORKDIR/go-spdx.stdout" ]]; then
  GJ="$(dep_count "$WORKDIR/go-json.stdout")"
  if [[ "$GJ" == "2" ]]; then pass "B2 go scan count 2"; else fail_msg "B2 go scan count $GJ (want 2)"; fi
  spdx_check "B1-B5/go" "$WORKDIR/go-spdx.stdout" "$WORKDIR/go-json.stdout"
fi

# ---- B4 shared spdx-b4 synth (font-awesome expr + dateutil multi-word + phantom Unknown) ----
run_scan "b4-json" "$B4FX" "json"
[[ "$RC" -eq 0 ]] && pass "B4 b4-json exit 0" || fail_msg "B4 b4-json exit $RC"
run_scan "b4-spdx" "$B4FX" "spdx-json"
[[ "$RC" -eq 0 ]] && pass "B4 b4-spdx exit 0" || fail_msg "B4 b4-spdx exit $RC"
if [[ -s "$WORKDIR/b4-json.stdout" && -s "$WORKDIR/b4-spdx.stdout" ]]; then
  B4C="$(dep_count "$WORKDIR/b4-json.stdout")"
  if [[ "$B4C" == "3" ]]; then pass "B4 spdx-b4 scan count 3"; else fail_msg "B4 spdx-b4 scan count $B4C (want 3)"; fi
  spdx_check "B4/spdx-b4" "$WORKDIR/b4-spdx.stdout" "$WORKDIR/b4-json.stdout"
  # Explicit pins (guarded: SKIP when the live-registry value is degraded offline).
  FA_SPDX="$(dep_field "$WORKDIR/b4-json.stdout" "font-awesome" "spdx")"
  if [[ "$FA_SPDX" == "(OFL-1.1 AND MIT)" ]]; then
    if python3 - "$WORKDIR/b4-spdx.stdout" <<'PY' 2>/dev/null
import json, sys
pkgs = {p["name"]: p for p in json.load(open(sys.argv[1]))["packages"]}
assert pkgs["font-awesome"]["licenseConcluded"] == "(OFL-1.1 AND MIT)", pkgs["font-awesome"]
PY
    then pass "B4 expression end-to-end: font-awesome concluded='(OFL-1.1 AND MIT)'";
    else fail_msg "B4 expression end-to-end: font-awesome concluded wrong"; fi
  else
    echo "SKIP: B4 font-awesome expression pin (ref spdx='$FA_SPDX', live value degraded)"
  fi
  DU_SPDX="$(dep_field "$WORKDIR/b4-json.stdout" "python-dateutil" "spdx")"
  if [[ "$DU_SPDX" == "Dual License" ]]; then
    if python3 - "$WORKDIR/b4-spdx.stdout" <<'PY' 2>/dev/null
import json, sys
pkgs = {p["name"]: p for p in json.load(open(sys.argv[1]))["packages"]}
assert pkgs["python-dateutil"]["licenseConcluded"] == "NOASSERTION", pkgs["python-dateutil"]
PY
    then pass "B4 multi-word end-to-end: python-dateutil 'Dual License'->NOASSERTION";
    else fail_msg "B4 multi-word end-to-end: python-dateutil concluded wrong"; fi
  else
    echo "SKIP: B4 python-dateutil multi-word pin (ref spdx='$DU_SPDX', live value degraded)"
  fi
  PH_STATUS="$(dep_field "$WORKDIR/b4-json.stdout" "olaf-nonexistent-pkg-xyz" "status")"
  if [[ "$PH_STATUS" == "Unknown" ]]; then
    if python3 - "$WORKDIR/b4-spdx.stdout" <<'PY' 2>/dev/null
import json, sys
raw = open(sys.argv[1]).read()
pkgs = {p["name"]: p for p in json.loads(raw)["packages"]}
assert pkgs["olaf-nonexistent-pkg-xyz"]["licenseConcluded"] == "NOASSERTION", pkgs["olaf-nonexistent-pkg-xyz"]
assert "Unknown" not in raw and '"NONE"' not in raw, "license leak"
PY
    then pass "B4 Unknown end-to-end: phantom->NOASSERTION, no NONE/Unknown leak";
    else fail_msg "B4 Unknown end-to-end: phantom concluded/leak wrong"; fi
  else
    echo "SKIP: B4 phantom Unknown pin (ref status='$PH_STATUS', live value degraded)"
  fi
fi

# ---- B6 --direct-only subset + both-flags conflict ----
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format spdx-json --direct-only >"$WORKDIR/b6-direct.stdout" 2>"$WORKDIR/b6-direct.stderr"
b6rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format spdx-json --direct-only --include-transitive >"$WORKDIR/b6-both.stdout" 2>"$WORKDIR/b6-both.stderr"
b6both=$?
set -e
if [[ "$b6rc" -eq 0 ]]; then
  B6C="$(python3 - "$WORKDIR/b6-direct.stdout" <<'PY' 2>/dev/null
import json, sys
try: print(len(json.load(open(sys.argv[1])).get("packages", [])))
except Exception: print("?")
PY
)"
  if [[ "$B6C" == "1" ]]; then pass "B6 --direct-only: exit 0, packages 1 (subset)";
  else fail_msg "B6 --direct-only: exit 0 but packages $B6C (want 1)"; fi
  if python3 - "$WORKDIR/b6-direct.stdout" <<'PY' 2>/dev/null
import json, sys
pkgs = json.load(open(sys.argv[1])).get("packages", [])
names = [p.get("name") for p in pkgs]
assert "example.com/direct-go-a" in names, names
assert "example.com/indirect-go-b" not in names, names
PY
  then pass "B6 --direct-only: direct present, indirect absent";
  else fail_msg "B6 --direct-only: package subset content wrong"; fi
else
  fail_msg "B6 --direct-only exit $b6rc (want 0)"
fi
if [[ "$b6both" -eq 2 ]]; then pass "B6 both-flags conflict: exit 2";
else fail_msg "B6 both-flags conflict: exit $b6both (want 2)"; fi

# ---- B7 unknown-format + --help ----
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FIXTURE" --format toml >"$WORKDIR/b7.stdout" 2>"$WORKDIR/b7.stderr"
b7rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --help >"$WORKDIR/help.stdout" 2>"$WORKDIR/help.stderr"
helprc=$?
set -e
if [[ "$b7rc" -eq 2 ]]; then pass "B7 unknown-format toml: exit 2";
else fail_msg "B7 unknown-format toml: exit $b7rc (want 2)"; fi
if [[ "$helprc" -eq 0 ]] && grep -q "spdx-json" "$WORKDIR/help.stdout"; then
  pass "B7 --help lists spdx-json"
else
  fail_msg "B7 --help missing spdx-json (exit $helprc)"
fi

# ---- Canonical R1 format-matrix regression (from _template.sh 0.2.3; behavior identical) ----
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

# ---- R1 format-matrix regression untouched ----
r1_matrix_regression

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "SPDX-PROBE OK: B1-B7 + R1 all pass."
  exit 0
else
  echo "SPDX-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
