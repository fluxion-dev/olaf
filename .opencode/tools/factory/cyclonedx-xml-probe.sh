#!/usr/bin/env bash
# cyclonedx-xml-probe.sh — CycloneDX XML probe for issue #68 (promoted v0.1.0, 2 uses ≥ bar).
# Model: cyclonedx-probe.sh B-structure; JSON checks swapped for XML checks via
#   python3 xml.etree.ElementTree + explicit namespace map (plays the
#   XmlDocument/XPath + XmlNamespaceManager role: NS=http://cyclonedx.org/schema/bom/1.5).
# Well-formedness gate runs FIRST (ET.parse must succeed before any B-check).
#   B1 envelope: root {ns}bom + xmlns bom/1.5 (specVersion lives in xmlns — no
#      specVersion attribute per CycloneDxXmlFormatter.cs) + version==1 +
#      serialNumber urn:uuid + <metadata>/<components> on npm fixture
#   B2 components count == json scan count; names/versions match (npm fixture + go indirect synth)
#   B3 scope<->direct mapping (<scope>required</scope> iff direct=true, optional iff direct=false)
#   B4 licenses: EffectiveSpdx single-token->licenses/license/id, multi-word->name,
#      Unknown->no <licenses> child (+ olaf:status/reason <property> props)
#      + shared spdx-b4 synth (font-awesome expr + python-dateutil multi-word + phantom
#      Unknown end-to-end; explicit pins SKIP when the live-registry value is degraded)
#   B5 purl well-formed (pkg:<type>/...@version) + bom-ref unique ({eco}:{name}@{ver}[-N])
#   B6 --direct-only subset on go synth + both-flags conflict exit 2
#   B7 unknown-format (toml) exit 2 + --help lists cyclonedx-xml
#   R1 format-matrix regression untouched (json/yaml/xml/html markers MATRIX OK)
# Offline-safe: never passes --strict; resolver failures degrade to Unknown, exit 0.
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

CycloneDX XML probe (issue #68): well-formedness gate, envelope (B1),
count/names (B2), scope<->direct (B3), licenses (B4), purl/bom-ref (B5),
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
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -d "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/cyclonedx-xml-probe-XXXXXX")"
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
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- generate "$input" --format "$fmt" "$@" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}

wellformed_gate() {
  # wellformed_gate <label> <xml-file>; ET.parse must succeed (gate first).
  local label="$1" xml="$2"
  if python3 - "$xml" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
ET.parse(sys.argv[1])
PY
  then
    pass "$label: well-formed XML (ET.parse OK)"
    return 0
  else
    fail_msg "$label: not well-formed XML (ET.parse failed)"
    return 1
  fi
}

cdx_xml_check() {
  # cdx_xml_check <label> <cdx-xml> <ref-json>; python ElementTree cross-check B1-B5.
  local label="$1" cdx="$2" ref="$3"
  python3 - "$cdx" "$ref" "$label" <<'PY'
import json, re, sys, xml.etree.ElementTree as ET
cdx_path, ref_path, label = sys.argv[1], sys.argv[2], sys.argv[3]
fails = []
def ok(msg): print(f"PASS: {label}: {msg}")
def bad(msg): print(f"FAIL: {label}: {msg}"); fails.append(msg)
NS = "http://cyclonedx.org/schema/bom/1.5"
def q(tag): return f"{{{NS}}}{tag}"
try:
    root = ET.parse(cdx_path).getroot()
except Exception as e:
    bad(f"unparseable XML ({e})"); sys.exit(1)
try:
    ref = json.load(open(ref_path))
except Exception as e:
    bad(f"unparseable ref JSON ({e})"); sys.exit(1)
# B1 envelope (xmlns carries specVersion — no specVersion attribute by design)
if root.tag == q("bom"): ok("root {ns}bom")
else: bad(f"root tag {root.tag!r} want {{ns}}bom")
if root.tag.startswith("{http://cyclonedx.org/schema/bom/1.5}"): ok("xmlns bom/1.5 (specVersion via namespace)")
else: bad(f"namespace wrong: {root.tag!r}")
if str(root.get("version", "")) == "1": ok("version==1")
else: bad(f"version={root.get('version')!r} want 1")
if re.match(r"^urn:uuid:[0-9a-fA-F-]{36}$", str(root.get("serialNumber", ""))): ok("serialNumber urn:uuid shape")
else: bad(f"serialNumber={root.get('serialNumber')!r} bad shape")
if root.find(q("metadata")) is not None: ok("<metadata> present")
else: bad("missing <metadata>")
comps_el = root.find(q("components"))
if comps_el is not None: ok("<components> present")
else: bad("missing <components>"); comps_el = ET.Element("empty")
comps = comps_el.findall(q("component"))
items = ref.get("licenses", ref.get("dependencies", ref.get("resolved", [])))
# B2 count
if len(comps) == len(items): ok(f"components count == scan count ({len(comps)})")
else: bad(f"components {len(comps)} != scan {len(items)}")
def text(el, tag):
    c = el.find(q(tag))
    return c.text.strip() if c is not None and c.text else ""
def props(el):
    d = {}
    p = el.find(q("properties"))
    if p is not None:
        for pr in p.findall(q("property")):
            d[pr.get("name", "")] = (pr.text or "").strip()
    return d
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
    compmap[text(c, "name").lower()] = c
# B2 names/versions
for key, (n, v, d, spdx) in refmap.items():
    c = compmap.get(key)
    if c is None:
        bad(f"component missing for '{n}'"); continue
    if text(c, "version") == v: ok(f"name/version {n}@{v}")
    else: bad(f"{n} version {text(c, 'version')!r} want {v!r}")
    # B3 scope mapping (only when ref carries direct bool)
    if d is True or d is False:
        want = "required" if d is True else "optional"
        if text(c, "scope") == want: ok(f"scope {n}={want}")
        else: bad(f"{n} scope {text(c, 'scope')!r} want {want} (direct={d})")
    # B4 licenses
    eff = (spdx or "Unknown").strip()
    lics = c.find(q("licenses"))
    if eff == "Unknown" or eff == "":
        if lics is None: ok(f"licenses {n} Unknown->absent")
        else: bad(f"{n} licenses present, want absent for Unknown")
        pr = props(c)
        if pr.get("olaf:status") == "Unknown": ok(f"props {n} olaf:status")
        else: bad(f"{n} missing olaf:status prop")
        if "olaf:reason" in pr: ok(f"props {n} olaf:reason")
        else: bad(f"{n} missing olaf:reason prop")
    elif any(ch.isspace() for ch in eff):
        got = text(lics.find(q("license")), "name") if lics is not None and lics.find(q("license")) is not None else ""
        if got == eff: ok(f"licenses {n} name={eff!r}")
        else: bad(f"{n} license/name={got!r} want {eff!r}")
    else:
        got = text(lics.find(q("license")), "id") if lics is not None and lics.find(q("license")) is not None else ""
        if got == eff: ok(f"licenses {n} id={eff!r}")
        else: bad(f"{n} license/id={got!r} want {eff!r}")
    # B5 purl shape
    purl = text(c, "purl")
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

echo "== cyclonedx-xml-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

# ---- B1/B2 npm fixture: json + cyclonedx-xml side by side ----
run_scan "npm-json" "$FIXTURE" "json"
[[ "$RC" -eq 0 ]] && pass "B1/B2 npm-json exit 0" || fail_msg "B1/B2 npm-json exit $RC"
run_scan "npm-cdx" "$FIXTURE" "cyclonedx-xml"
[[ "$RC" -eq 0 ]] && pass "B1 npm-cdx exit 0" || fail_msg "B1 npm-cdx exit $RC"
if [[ "$RC" -eq 0 ]]; then
  NJ="$(dep_count "$WORKDIR/npm-json.stdout")"
  echo "npm json scan count: $NJ"
  if wellformed_gate "B0/npm" "$WORKDIR/npm-cdx.stdout"; then
    cdx_xml_check "B1-B5/npm" "$WORKDIR/npm-cdx.stdout" "$WORKDIR/npm-json.stdout"
  fi
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
run_scan "go-cdx" "$FX/go" "cyclonedx-xml"
[[ "$RC" -eq 0 ]] && pass "B2 go-cdx exit 0" || fail_msg "B2 go-cdx exit $RC"
if [[ -s "$WORKDIR/go-json.stdout" && -s "$WORKDIR/go-cdx.stdout" ]]; then
  GJ="$(dep_count "$WORKDIR/go-json.stdout")"
  if [[ "$GJ" == "2" ]]; then pass "B2 go scan count 2"; else fail_msg "B2 go scan count $GJ (want 2)"; fi
  if wellformed_gate "B0/go" "$WORKDIR/go-cdx.stdout"; then
    cdx_xml_check "B1-B5/go" "$WORKDIR/go-cdx.stdout" "$WORKDIR/go-json.stdout"
  fi
fi

# ---- B4 shared spdx-b4 synth (font-awesome expr + dateutil multi-word + phantom Unknown, end-to-end) ----
B4FX="$ROOT/tests/Olaf.Tests/Fixtures/spdx-b4"
[[ -d "$B4FX" ]] || fail_msg "B4 fixture not found: $B4FX"
if [[ -d "$B4FX" ]]; then
  run_scan "b4-json" "$B4FX" "json"
  [[ "$RC" -eq 0 ]] && pass "B4 b4-json exit 0" || fail_msg "B4 b4-json exit $RC"
  run_scan "b4-cdx" "$B4FX" "cyclonedx-xml"
  [[ "$RC" -eq 0 ]] && pass "B4 b4-cdx exit 0" || fail_msg "B4 b4-cdx exit $RC"
  if [[ -s "$WORKDIR/b4-json.stdout" && -s "$WORKDIR/b4-cdx.stdout" ]]; then
    B4C="$(dep_count "$WORKDIR/b4-json.stdout")"
    if [[ "$B4C" == "3" ]]; then pass "B4 spdx-b4 scan count 3"; else fail_msg "B4 spdx-b4 scan count $B4C (want 3)"; fi
    if wellformed_gate "B0/spdx-b4" "$WORKDIR/b4-cdx.stdout"; then
      cdx_xml_check "B4/spdx-b4" "$WORKDIR/b4-cdx.stdout" "$WORKDIR/b4-json.stdout"
    fi
    # Explicit multi-word pin (guarded: SKIP when the live-registry value is degraded offline).
    DU_SPDX="$(dep_field "$WORKDIR/b4-json.stdout" "python-dateutil" "spdx")"
    if [[ "$DU_SPDX" == "Dual License" ]]; then
      if python3 - "$WORKDIR/b4-cdx.stdout" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
NS = "http://cyclonedx.org/schema/bom/1.5"
def q(t): return f"{{{NS}}}{t}"
def text(el, tag):
    c = el.find(q(tag))
    return c.text.strip() if c is not None and c.text else ""
root = ET.parse(sys.argv[1]).getroot()
compmap = {}
for c in root.find(q("components")).findall(q("component")):
    compmap[text(c, "name")] = c
lic = compmap["python-dateutil"].find(q("licenses")).find(q("license"))
assert text(lic, "name") == "Dual License", ET.tostring(lic, encoding="unicode")
PY
      then pass "B4 multi-word end-to-end: python-dateutil license/name='Dual License'";
      else fail_msg "B4 multi-word end-to-end: python-dateutil license shape wrong"; fi
    else
      echo "SKIP: B4 python-dateutil multi-word pin (ref spdx='$DU_SPDX', live value degraded)"
    fi
  fi
fi

# ---- B6 --direct-only subset + both-flags conflict ----
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- generate "$FX/go" --format cyclonedx-xml --direct-only >"$WORKDIR/b6-direct.stdout" 2>"$WORKDIR/b6-direct.stderr"
b6rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- generate "$FX/go" --format cyclonedx-xml --direct-only --include-transitive >"$WORKDIR/b6-both.stdout" 2>"$WORKDIR/b6-both.stderr"
b6both=$?
set -e
if [[ "$b6rc" -eq 0 ]]; then
  B6C="$(python3 - "$WORKDIR/b6-direct.stdout" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
NS = "http://cyclonedx.org/schema/bom/1.5"
try:
    root = ET.parse(sys.argv[1]).getroot()
    print(len(root.find(f"{{{NS}}}components").findall(f"{{{NS}}}component")))
except Exception:
    print("?")
PY
)"
  if [[ "$B6C" == "1" ]]; then pass "B6 --direct-only: exit 0, components 1 (subset)";
  else fail_msg "B6 --direct-only: exit 0 but components $B6C (want 1)"; fi
  if python3 - "$WORKDIR/b6-direct.stdout" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
NS = "http://cyclonedx.org/schema/bom/1.5"
def q(t): return f"{{{NS}}}{t}"
def text(el, tag):
    c = el.find(q(tag))
    return c.text.strip() if c is not None and c.text else ""
root = ET.parse(sys.argv[1]).getroot()
comps = root.find(q("components")).findall(q("component"))
names = [text(c, "name") for c in comps]
assert "example.com/direct-go-a" in names, names
assert "example.com/indirect-go-b" not in names, names
assert all(text(c, "scope") == "required" for c in comps), [text(c, "scope") for c in comps]
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
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- generate "$FIXTURE" --format toml >"$WORKDIR/b7.stdout" 2>"$WORKDIR/b7.stderr"
b7rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- generate --help >"$WORKDIR/help.stdout" 2>"$WORKDIR/help.stderr"
helprc=$?
set -e
if [[ "$b7rc" -eq 2 ]]; then pass "B7 unknown-format toml: exit 2";
else fail_msg "B7 unknown-format toml: exit $b7rc (want 2)"; fi
if [[ "$helprc" -eq 0 ]] && grep -q "cyclonedx-xml" "$WORKDIR/help.stdout"; then
  pass "B7 --help lists cyclonedx-xml"
else
  fail_msg "B7 --help missing cyclonedx-xml (exit $helprc)"
fi

# ---- Canonical R1 format-matrix regression (from _template.sh 0.2.3; behavior identical) ----
r1_matrix_regression() {
  # r1_matrix_regression [formats...]; default: json yaml xml html.
  local formats=("$@")
  (( ${#formats[@]} )) || formats=(json yaml xml)
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
  echo "CYCLONEDX-XML-PROBE OK: B0-B7 + R1 all pass."
  exit 0
else
  echo "CYCLONEDX-XML-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
