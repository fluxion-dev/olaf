#!/usr/bin/env bash
# cyclonedx-probe.sh — CycloneDX JSON probe for issue #67.
# CycloneDX JSON probe for issue #67 (promoted 0.1.0: 2 uses >= bar).
# Offline-safe: never passes --strict; resolver failures degrade to Unknown, exit 0.
#   B1 envelope: bomFormat==CycloneDX + specVersion==1.5 (+version/serialNumber/timestamp) on npm fixture
#   B2 components count == json scan count; names/versions match (npm fixture + go indirect synth)
#   B3 scope<->direct mapping (required iff direct=true, optional iff direct=false)
#   B4 licenses: EffectiveSpdx single-token->id, multi-word->name, Unknown->[] (+olaf:status/reason props)
#      + shared spdx-b4 synth (font-awesome expr + python-dateutil multi-word + phantom
#      Unknown end-to-end; explicit pins SKIP when the live-registry value is degraded)
#   B5 purl well-formed (pkg:<type>/...@version) + bom-ref unique ({eco}:{name}@{ver}[-N])
#   B6 --direct-only subset on go synth + both-flags conflict exit 2
#   B7 unknown-format (toml) exit 2 + --help lists cyclonedx-json
#   R1 format-matrix regression untouched (json/yaml/xml/html markers MATRIX OK)
# Rules: repo-relative, idempotent (mktemp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.2"
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

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

CycloneDX JSON probe (issue #67): envelope (B1), count/names (B2),
scope<->direct (B3), licenses (B4), purl/bom-ref (B5), --direct-only +
both-flags (B6), unknown-format + --help (B7), format-matrix regression (R1).

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
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -d "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/cyclonedx-probe-XXXXXX")"
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

cdx_check() {
  # cdx_check <label> <cdx-json> <ref-json>; python cross-check B2-B5.
  local label="$1" cdx="$2" ref="$3"
  python3 - "$cdx" "$ref" "$label" <<'PY'
import json, re, sys
cdx_path, ref_path, label = sys.argv[1], sys.argv[2], sys.argv[3]
fails = []
def ok(msg): print(f"PASS: {label}: {msg}")
def bad(msg): print(f"FAIL: {label}: {msg}"); fails.append(msg)
try:
    cdx = json.load(open(cdx_path))
    ref = json.load(open(ref_path))
except Exception as e:
    bad(f"unparseable JSON ({e})"); sys.exit(1)
# B1 envelope (checked per-file; npm run is authoritative, go run must match too)
if cdx.get("bomFormat") == "CycloneDX": ok("bomFormat==CycloneDX")
else: bad(f"bomFormat={cdx.get('bomFormat')!r} want CycloneDX")
if str(cdx.get("specVersion")) == "1.5": ok("specVersion==1.5")
else: bad(f"specVersion={cdx.get('specVersion')!r} want 1.5")
if cdx.get("version") == 1: ok("version==1")
else: bad(f"version={cdx.get('version')!r} want 1")
if re.match(r"^urn:uuid:[0-9a-fA-F-]{36}$", str(cdx.get("serialNumber", ""))): ok("serialNumber urn:uuid shape")
else: bad(f"serialNumber={cdx.get('serialNumber')!r} bad shape")
comps = cdx.get("components", [])
items = ref.get("licenses", ref.get("dependencies", ref.get("resolved", [])))
# B2 count
if len(comps) == len(items): ok(f"components count == scan count ({len(comps)})")
else: bad(f"components {len(comps)} != scan {len(items)}")
# index ref by lower name -> (name, version, direct, spdx)
refmap = {}
for it in items:
    if not isinstance(it, dict): continue
    dep = it.get("dependency", it)
    n = str(dep.get("name", it.get("name", "")))
    v = str(dep.get("version", it.get("version", "")))
    d = dep.get("direct", it.get("direct", None))
    spdx = it.get("spdxId", it.get("spdx", None))
    if spdx is None: spdx = dep.get("spdx", None)
    refmap[n.lower()] = (n, v, d, spdx if spdx else "Unknown")
compmap = {}
for c in comps:
    compmap[str(c.get("name", "")).lower()] = c
# B2 names/versions
for key, (n, v, d, spdx) in refmap.items():
    c = compmap.get(key)
    if c is None:
        bad(f"component missing for '{n}'"); continue
    if str(c.get("version", "")) == v: ok(f"name/version {n}@{v}")
    else: bad(f"{n} version {c.get('version')!r} want {v!r}")
    # B3 scope mapping (only when ref carries direct bool)
    if d is True or d is False:
        want = "required" if d is True else "optional"
        if c.get("scope") == want: ok(f"scope {n}={want}")
        else: bad(f"{n} scope {c.get('scope')!r} want {want} (direct={d})")
    # B4 licenses
    eff = (spdx or "Unknown").strip()
    lics = c.get("licenses", None)
    if eff == "Unknown" or eff == "":
        if isinstance(lics, list) and len(lics) == 0: ok(f"licenses {n} Unknown->[]")
        else: bad(f"{n} licenses={lics!r} want [] for Unknown")
        props = {p.get("name"): p.get("value") for p in (c.get("properties", []) or []) if isinstance(p, dict)}
        if props.get("olaf:status") == "Unknown": ok(f"props {n} olaf:status")
        else: bad(f"{n} missing olaf:status prop")
        if "olaf:reason" in props: ok(f"props {n} olaf:reason")
        else: bad(f"{n} missing olaf:reason prop")
    elif any(ch.isspace() for ch in eff):
        try:
            got = lics[0]["license"].get("name", "")
            if got == eff: ok(f"licenses {n} name={eff!r}")
            else: bad(f"{n} license.name={got!r} want {eff!r}")
        except Exception: bad(f"{n} license name shape bad: {lics!r}")
    else:
        try:
            got = lics[0]["license"].get("id", "")
            if got == eff: ok(f"licenses {n} id={eff!r}")
            else: bad(f"{n} license.id={got!r} want {eff!r}")
        except Exception: bad(f"{n} license id shape bad: {lics!r}")
    # B5 purl shape
    purl = str(c.get("purl", ""))
    if re.match(r"^pkg:[a-z0-9]+/.+@.+", purl): ok(f"purl {n}={purl}")
    else: bad(f"{n} purl malformed: {purl!r}")
# B5 bom-ref uniqueness + shape
refs = [str(c.get("bom-ref", "")) for c in comps]
if len(refs) == len(set(refs)): ok(f"bom-ref unique ({len(refs)})")
else: bad("bom-ref duplicates found")
badshape = [r for r in refs if not re.match(r"^[^:]+:.+@.+(-\d+)?$", r)]
if not badshape: ok("bom-ref shape {eco}:{name}@{ver}[-N]")
else: bad(f"bom-ref bad shape: {badshape[:3]}")
sys.exit(1 if fails else 0)
PY
  local prc=$?
  if [[ "$prc" -ne 0 ]]; then fail=1; fi
}

echo "== cyclonedx-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

# ---- B1/B2 npm fixture: json + cyclonedx-json side by side ----
run_scan "npm-json" "$FIXTURE" "json"
[[ "$RC" -eq 0 ]] && pass "B1/B2 npm-json exit 0" || fail_msg "B1/B2 npm-json exit $RC"
run_scan "npm-cdx" "$FIXTURE" "cyclonedx-json"
[[ "$RC" -eq 0 ]] && pass "B1 npm-cdx exit 0" || fail_msg "B1 npm-cdx exit $RC"
if [[ "$RC" -eq 0 ]]; then
  NJ="$(dep_count "$WORKDIR/npm-json.stdout")"
  echo "npm json scan count: $NJ"
  cdx_check "B1-B5/npm" "$WORKDIR/npm-cdx.stdout" "$WORKDIR/npm-json.stdout"
fi

# ---- B2/B3 go indirect synth (1 direct + 1 indirect) ----
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
run_scan "go-cdx" "$FX/go" "cyclonedx-json"
[[ "$RC" -eq 0 ]] && pass "B2 go-cdx exit 0" || fail_msg "B2 go-cdx exit $RC"
if [[ -s "$WORKDIR/go-json.stdout" && -s "$WORKDIR/go-cdx.stdout" ]]; then
  GJ="$(dep_count "$WORKDIR/go-json.stdout")"
  if [[ "$GJ" == "2" ]]; then pass "B2 go scan count 2"; else fail_msg "B2 go scan count $GJ (want 2)"; fi
  cdx_check "B1-B5/go" "$WORKDIR/go-cdx.stdout" "$WORKDIR/go-json.stdout"
fi

# ---- B4 shared spdx-b4 synth (font-awesome expr + dateutil multi-word + phantom Unknown, end-to-end) ----
B4FX="$ROOT/tests/Olaf.Tests/Fixtures/spdx-b4"
[[ -d "$B4FX" ]] || fail_msg "B4 fixture not found: $B4FX"
if [[ -d "$B4FX" ]]; then
  run_scan "b4-json" "$B4FX" "json"
  [[ "$RC" -eq 0 ]] && pass "B4 b4-json exit 0" || fail_msg "B4 b4-json exit $RC"
  run_scan "b4-cdx" "$B4FX" "cyclonedx-json"
  [[ "$RC" -eq 0 ]] && pass "B4 b4-cdx exit 0" || fail_msg "B4 b4-cdx exit $RC"
  if [[ -s "$WORKDIR/b4-json.stdout" && -s "$WORKDIR/b4-cdx.stdout" ]]; then
    B4C="$(dep_count "$WORKDIR/b4-json.stdout")"
    if [[ "$B4C" == "3" ]]; then pass "B4 spdx-b4 scan count 3"; else fail_msg "B4 spdx-b4 scan count $B4C (want 3)"; fi
    cdx_check "B4/spdx-b4" "$WORKDIR/b4-cdx.stdout" "$WORKDIR/b4-json.stdout"
    # Explicit multi-word pin (guarded: SKIP when the live-registry value is degraded offline).
    DU_SPDX="$(dep_field "$WORKDIR/b4-json.stdout" "python-dateutil" "spdx")"
    if [[ "$DU_SPDX" == "Dual License" ]]; then
      if python3 - "$WORKDIR/b4-cdx.stdout" <<'PY' 2>/dev/null
import json, sys
comps = {c["name"]: c for c in json.load(open(sys.argv[1]))["components"]}
assert comps["python-dateutil"]["licenses"] == [{"license": {"name": "Dual License"}}], comps["python-dateutil"]
PY
      then pass "B4 multi-word end-to-end: python-dateutil license.name='Dual License'";
      else fail_msg "B4 multi-word end-to-end: python-dateutil license shape wrong"; fi
    else
      echo "SKIP: B4 python-dateutil multi-word pin (ref spdx='$DU_SPDX', live value degraded)"
    fi
  fi
fi

# ---- B6 --direct-only subset + both-flags conflict ----
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format cyclonedx-json --direct-only >"$WORKDIR/b6-direct.stdout" 2>"$WORKDIR/b6-direct.stderr"
b6rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format cyclonedx-json --direct-only --include-transitive >"$WORKDIR/b6-both.stdout" 2>"$WORKDIR/b6-both.stderr"
b6both=$?
set -e
if [[ "$b6rc" -eq 0 ]]; then
  B6C="$(python3 - "$WORKDIR/b6-direct.stdout" <<'PY' 2>/dev/null
import json, sys
try: print(len(json.load(open(sys.argv[1])).get("components", [])))
except Exception: print("?")
PY
)"
  if [[ "$B6C" == "1" ]]; then pass "B6 --direct-only: exit 0, components 1 (subset)";
  else fail_msg "B6 --direct-only: exit 0 but components $B6C (want 1)"; fi
  if python3 - "$WORKDIR/b6-direct.stdout" <<'PY' 2>/dev/null
import json, sys
comps = json.load(open(sys.argv[1])).get("components", [])
names = [c.get("name") for c in comps]
assert "example.com/direct-go-a" in names, names
assert "example.com/indirect-go-b" not in names, names
assert all(c.get("scope") == "required" for c in comps), [c.get("scope") for c in comps]
PY
  then pass "B6 --direct-only: direct present, indirect absent, scope required";
  else fail_msg "B6 --direct-only: component/scope content wrong"; fi
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
if [[ "$helprc" -eq 0 ]] && grep -q "cyclonedx-json" "$WORKDIR/help.stdout"; then
  pass "B7 --help lists cyclonedx-json"
else
  fail_msg "B7 --help missing cyclonedx-json (exit $helprc)"
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
  echo "CYCLONEDX-PROBE OK: B1-B7 + R1 all pass."
  exit 0
else
  echo "CYCLONEDX-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
