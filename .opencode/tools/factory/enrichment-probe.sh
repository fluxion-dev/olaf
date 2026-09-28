#!/usr/bin/env bash
# enrichment-probe.sh — Enrichment probe for issue #70 (scratch v0.1.0, 1st use).
# Model: spdx-probe.sh B-structure with enrich_check per format-kind.
# Offline-safe/live-tolerant: never passes --strict; resolver failures degrade
#   to Unknown with null enrichment, exit 0. Enriched-when-available asserts +
#   null-tolerance (absent keys / NOASSERTION) — never hard-fails on Unknown.
#   E1 supplier: json/yaml `supplier` non-empty-when-present (Unknown+present=FAIL);
#      spdx `supplier` == "Person: <ref>"-or-NOASSERTION; cdx `supplier.name`
#      object-when-present else absent.
#   E2 downloadLocation: http(s) URI shape or NOASSERTION + SourceUrl-never-copied
#      guard (download != sourceUrl whenever both present).
#   E3 hashes: cdx `hashes[]` {alg,content} / spdx `checksums[]`
#      {algorithm,checksumValue} / json `hashes[]` "algo:value" — or documented
#      absence (key omitted / NOASSERTION path); unparseable entries are
#      formatter-dropped, never emitted.
#   E4 purl unconditional + well-formed (B5 regex ^pkg:[a-z0-9]+/.+@.+): spdx
#      externalRefs + cdx purl asserted for EVERY entry incl. Unknown; json/yaml
#      purl well-formed-when-present, absent-tolerated (omit-null by design).
#   R1 matrix regression: json/yaml/xml/html `direct` markers untouched on npm
#   fixture (restored to 4-format on _template.sh 0.2.3 back-port — `direct`
#   spans all 9 matrix formats, so the earlier json/yaml narrowing had no
#   format-coverage justification).
# Fixtures: committed npm fixture + go indirect synth + shared spdx-b4
#   (font-awesome expr + python-dateutil multi-word + phantom Unknown;
#   explicit live pins SKIP when the live-registry value is degraded offline,
#   per fixture-hunt note).
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

Enrichment probe (issue #70): supplier (E1), downloadLocation (E2),
hashes (E3), purl unconditional + well-formed (E4),
format-matrix regression (R1). Live-tolerant: enriched-when-available +
null-tolerance, never hard-fails on Unknown.

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
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/enrichment-probe-XXXXXX")"
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

enrich_check() {
  # enrich_check <label> <out-file> <ref-json> <kind>; python cross-check E1-E4.
  # kind: json | spdx | cdx. ref-json is the format-json scan of the same input
  # (carries status/sourceUrl + omit-null enrichment keys as ground truth).
  local label="$1" out="$2" ref="$3" kind="$4"
  python3 - "$out" "$ref" "$label" "$kind" <<'PY'
import json, re, sys
out_path, ref_path, label, kind = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
fails = []
def ok(msg): print(f"PASS: {label}: {msg}")
def bad(msg): print(f"FAIL: {label}: {msg}"); fails.append(msg)
try:
    out = json.load(open(out_path))
    ref = json.load(open(ref_path))
except Exception as e:
    bad(f"unparseable JSON ({e})"); sys.exit(1)
PURL_RE = r"^pkg:[a-z0-9]+/.+@.+"
URI_RE = r"^https?://.+"
items = ref.get("licenses", ref.get("dependencies", ref.get("resolved", [])))
# index ref by lower name -> {status, sourceUrl, supplier, downloadUrl, hashes, purl}
refmap = {}
for it in items:
    if not isinstance(it, dict): continue
    dep = it.get("dependency", it)
    n = str(dep.get("name", it.get("name", "")))
    refmap[n.lower()] = {
        "name": n,
        "status": str(it.get("status", "")),
        "sourceUrl": it.get("sourceUrl", None),
        "supplier": it.get("supplier", None),
        "downloadUrl": it.get("downloadUrl", None),
        "hashes": it.get("hashes", None),
        "purl": it.get("purl", None),
    }
def check_hash_entries(entries, where):
    # entries: list of "algo:value"; both halves must be non-blank.
    if not isinstance(entries, list) or len(entries) == 0:
        bad(f"{where} hashes empty/non-list: {entries!r}"); return
    for e in entries:
        if not isinstance(e, str) or ":" not in e:
            bad(f"{where} hash unparseable (no colon): {e!r}"); continue
        algo, _, val = e.partition(":")
        if not algo.strip() or not val.strip():
            bad(f"{where} hash blank half: {e!r}")
    else:
        ok(f"{where} hashes[] {len(entries)} entries algo:value-shaped")
if kind == "json":
    entries = out.get("licenses", None)
    if not isinstance(entries, list):
        bad(f"licenses not a list"); sys.exit(1)
    outmap = {}
    for e in entries:
        if isinstance(e, dict):
            outmap[str(e.get("name", "")).lower()] = e
    if len(outmap) == len(refmap): ok(f"entry count == ref count ({len(outmap)})")
    else: bad(f"entry count {len(outmap)} != ref {len(refmap)}")
    enriched = sum(1 for e in outmap.values() if e.get("purl") is not None)
    ok(f"E4 purl present on {enriched}/{len(outmap)} (omit-null when unenriched)")
    for key, r in refmap.items():
        e = outmap.get(key)
        if e is None:
            bad(f"entry missing for '{r['name']}'"); continue
        # E4 purl well-formed-when-present
        p = e.get("purl", None)
        if p is None:
            ok(f"E4 {r['name']}: purl absent (null-tolerance, status={r['status']})")
        elif re.match(PURL_RE, str(p)): ok(f"E4 {r['name']}: purl well-formed {p}")
        else: bad(f"E4 {r['name']}: purl malformed: {p!r}")
        # E1 supplier: non-empty-when-present; Unknown+present is a leak
        s = e.get("supplier", None)
        if s is None:
            ok(f"E1 {r['name']}: supplier absent (null-tolerance, status={r['status']})")
        elif isinstance(s, str) and s.strip():
            if r["status"] == "Unknown": bad(f"E1 {r['name']}: Unknown carries supplier {s!r}")
            else: ok(f"E1 {r['name']}: supplier enriched {s!r}")
        else: bad(f"E1 {r['name']}: supplier empty/non-string: {s!r}")
        # E2 downloadUrl: URI shape + never SourceUrl
        d = e.get("downloadUrl", None)
        if d is None:
            ok(f"E2 {r['name']}: downloadUrl absent (null-tolerance, status={r['status']})")
        elif re.match(URI_RE, str(d)):
            if r["sourceUrl"] and str(d) == str(r["sourceUrl"]):
                bad(f"E2 {r['name']}: downloadUrl copies SourceUrl {d!r}")
            else: ok(f"E2 {r['name']}: downloadUrl URI {d}")
        else: bad(f"E2 {r['name']}: downloadUrl bad URI: {d!r}")
        # E3 hashes: shaped-when-present else documented absence
        h = e.get("hashes", None)
        if h is None:
            ok(f"E3 {r['name']}: hashes absent (documented absence, status={r['status']})")
        else: check_hash_entries(h, f"E3 {r['name']}")
elif kind == "spdx":
    pkgs = out.get("packages", None)
    if not isinstance(pkgs, list):
        bad("packages not a list"); sys.exit(1)
    pkgmap = {}
    for p in pkgs:
        if isinstance(p, dict):
            pkgmap[str(p.get("name", "")).lower()] = p
    if len(pkgmap) == len(refmap): ok(f"packages count == ref count ({len(pkgmap)})")
    else: bad(f"packages {len(pkgmap)} != ref {len(refmap)}")
    for key, r in refmap.items():
        p = pkgmap.get(key)
        if p is None:
            bad(f"package missing for '{r['name']}'"); continue
        # E1: enrichment-or-NOASSERTION, mirrors ref
        want_sup = f"Person: {r['supplier'].strip()}" if isinstance(r["supplier"], str) and r["supplier"].strip() else "NOASSERTION"
        if p.get("supplier") == want_sup: ok(f"E1 {r['name']}: supplier={want_sup!r}")
        else: bad(f"E1 {r['name']}: supplier={p.get('supplier')!r} want {want_sup!r}")
        if p.get("supplier") in (None, "", "NONE") or "Unknown" in str(p.get("supplier", "")):
            bad(f"E1 {r['name']}: supplier leak: {p.get('supplier')!r}")
        # E2: enrichment-or-NOASSERTION, never SourceUrl
        want_dl = r["downloadUrl"] if isinstance(r["downloadUrl"], str) and re.match(URI_RE, r["downloadUrl"]) else "NOASSERTION"
        if p.get("downloadLocation") == want_dl: ok(f"E2 {r['name']}: downloadLocation={want_dl!r}")
        else: bad(f"E2 {r['name']}: downloadLocation={p.get('downloadLocation')!r} want {want_dl!r}")
        if isinstance(p.get("downloadLocation"), str) and p.get("downloadLocation") != "NOASSERTION":
            if r["sourceUrl"] and p.get("downloadLocation") == r["sourceUrl"]:
                bad(f"E2 {r['name']}: downloadLocation copies SourceUrl")
        # E3: checksums[] when ref hashes present, else omitted
        cks = p.get("checksums", None)
        if isinstance(r["hashes"], list) and len(r["hashes"]) > 0:
            if isinstance(cks, list) and len(cks) > 0 and all(
                    isinstance(c, dict) and c.get("algorithm") and c.get("checksumValue") for c in cks):
                ok(f"E3 {r['name']}: checksums[] {len(cks)} entries")
            else: bad(f"E3 {r['name']}: checksums={cks!r} want non-empty algorithm/checksumValue entries")
        else:
            if cks is None: ok(f"E3 {r['name']}: checksums omitted (documented absence)")
            else: bad(f"E3 {r['name']}: checksums present without ref hashes: {cks!r}")
        # E4: purl unconditional + well-formed
        try:
            refs = p.get("externalRefs", None)
            loc = refs[0].get("referenceLocator", "")
            if (refs[0].get("referenceCategory") == "PACKAGE-MANAGER"
                    and refs[0].get("referenceType") == "purl"
                    and re.match(PURL_RE, str(loc))):
                ok(f"E4 {r['name']}: purl unconditional {loc}")
            else: bad(f"E4 {r['name']}: externalRefs bad: {refs!r}")
        except Exception:
            bad(f"E4 {r['name']}: externalRefs missing: {p.get('externalRefs')!r}")
elif kind == "cdx":
    comps = out.get("components", None)
    if not isinstance(comps, list):
        bad("components not a list"); sys.exit(1)
    compmap = {}
    for c in comps:
        if isinstance(c, dict):
            compmap[str(c.get("name", "")).lower()] = c
    if len(compmap) == len(refmap): ok(f"components count == ref count ({len(compmap)})")
    else: bad(f"components {len(compmap)} != ref {len(refmap)}")
    for key, r in refmap.items():
        c = compmap.get(key)
        if c is None:
            bad(f"component missing for '{r['name']}'"); continue
        # E1: supplier {name} object-when-present else absent
        s = c.get("supplier", None)
        if s is None:
            ok(f"E1 {r['name']}: supplier absent (null-tolerance, status={r['status']})")
        elif isinstance(s, dict) and isinstance(s.get("name"), str) and s["name"].strip():
            if r["status"] == "Unknown": bad(f"E1 {r['name']}: Unknown carries supplier {s!r}")
            else: ok(f"E1 {r['name']}: supplier enriched {s!r}")
        else: bad(f"E1 {r['name']}: supplier malformed: {s!r}")
        # E2: distribution externalReference URI + never SourceUrl
        ers = c.get("externalReferences", None)
        if ers is None:
            ok(f"E2 {r['name']}: externalReferences absent (null-tolerance, status={r['status']})")
        else:
            try:
                dist = [x for x in ers if isinstance(x, dict) and x.get("type") == "distribution"]
                url = dist[0].get("url", "") if dist else ""
                if re.match(URI_RE, str(url)):
                    if r["sourceUrl"] and str(url) == str(r["sourceUrl"]):
                        bad(f"E2 {r['name']}: distribution url copies SourceUrl")
                    else: ok(f"E2 {r['name']}: distribution url {url}")
                else: bad(f"E2 {r['name']}: distribution url bad: {ers!r}")
            except Exception:
                bad(f"E2 {r['name']}: externalReferences malformed: {ers!r}")
        # E3: hashes[] {alg,content} or documented absence
        hs = c.get("hashes", None)
        if hs is None:
            ok(f"E3 {r['name']}: hashes absent (documented absence, status={r['status']})")
        elif isinstance(hs, list) and len(hs) > 0 and all(
                isinstance(x, dict) and x.get("alg") and x.get("content") for x in hs):
            ok(f"E3 {r['name']}: hashes[] {len(hs)} entries")
        else: bad(f"E3 {r['name']}: hashes malformed: {hs!r}")
        # E4: purl unconditional + well-formed (every component incl. Unknown)
        p = str(c.get("purl", ""))
        if re.match(PURL_RE, p): ok(f"E4 {r['name']}: purl unconditional {p}")
        else: bad(f"E4 {r['name']}: purl malformed: {p!r}")
else:
    bad(f"unknown kind {kind!r}"); sys.exit(1)
sys.exit(1 if fails else 0)
PY
  local prc=$?
  if [[ "$prc" -ne 0 ]]; then fail=1; fi
}

echo "== enrichment-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

# ---- E1-E4 npm fixture: json (ref) + spdx-json + cyclonedx-json ----
run_scan "npm-json" "$FIXTURE" "json"
[[ "$RC" -eq 0 ]] && pass "npm-json exit 0" || fail_msg "npm-json exit $RC"
run_scan "npm-spdx" "$FIXTURE" "spdx-json"
[[ "$RC" -eq 0 ]] && pass "npm-spdx exit 0" || fail_msg "npm-spdx exit $RC"
run_scan "npm-cdx" "$FIXTURE" "cyclonedx-json"
[[ "$RC" -eq 0 ]] && pass "npm-cdx exit 0" || fail_msg "npm-cdx exit $RC"
if [[ -s "$WORKDIR/npm-json.stdout" ]]; then
  NJ="$(dep_count "$WORKDIR/npm-json.stdout")"
  echo "npm json scan count: $NJ"
  enrich_check "E1-E4/npm-json" "$WORKDIR/npm-json.stdout" "$WORKDIR/npm-json.stdout" "json"
  [[ -s "$WORKDIR/npm-spdx.stdout" ]] && enrich_check "E1-E4/npm-spdx" "$WORKDIR/npm-spdx.stdout" "$WORKDIR/npm-json.stdout" "spdx"
  [[ -s "$WORKDIR/npm-cdx.stdout" ]] && enrich_check "E1-E4/npm-cdx" "$WORKDIR/npm-cdx.stdout" "$WORKDIR/npm-json.stdout" "cdx"
fi

# ---- E1-E4 go indirect synth (fake modules -> Unknown, null-tolerance arm) ----
FX="$WORKDIR/fx"
mkdir -p "$FX/go"
cat > "$FX/go/go.mod" <<'EOF'
module example.com/fx

go 1.21

require example.com/direct-go-a v1.0.0

require example.com/indirect-go-b v2.0.0 // indirect
EOF
run_scan "go-json" "$FX/go" "json"
[[ "$RC" -eq 0 ]] && pass "go-json exit 0" || fail_msg "go-json exit $RC"
run_scan "go-spdx" "$FX/go" "spdx-json"
[[ "$RC" -eq 0 ]] && pass "go-spdx exit 0" || fail_msg "go-spdx exit $RC"
run_scan "go-cdx" "$FX/go" "cyclonedx-json"
[[ "$RC" -eq 0 ]] && pass "go-cdx exit 0" || fail_msg "go-cdx exit $RC"
if [[ -s "$WORKDIR/go-json.stdout" ]]; then
  GJ="$(dep_count "$WORKDIR/go-json.stdout")"
  if [[ "$GJ" == "2" ]]; then pass "go scan count 2"; else fail_msg "go scan count $GJ (want 2)"; fi
  enrich_check "E1-E4/go-json" "$WORKDIR/go-json.stdout" "$WORKDIR/go-json.stdout" "json"
  [[ -s "$WORKDIR/go-spdx.stdout" ]] && enrich_check "E1-E4/go-spdx" "$WORKDIR/go-spdx.stdout" "$WORKDIR/go-json.stdout" "spdx"
  [[ -s "$WORKDIR/go-cdx.stdout" ]] && enrich_check "E1-E4/go-cdx" "$WORKDIR/go-cdx.stdout" "$WORKDIR/go-json.stdout" "cdx"
fi

# ---- E1-E4 shared spdx-b4 (font-awesome + dateutil + phantom, end-to-end) ----
run_scan "b4-json" "$B4FX" "json"
[[ "$RC" -eq 0 ]] && pass "b4-json exit 0" || fail_msg "b4-json exit $RC"
run_scan "b4-spdx" "$B4FX" "spdx-json"
[[ "$RC" -eq 0 ]] && pass "b4-spdx exit 0" || fail_msg "b4-spdx exit $RC"
run_scan "b4-cdx" "$B4FX" "cyclonedx-json"
[[ "$RC" -eq 0 ]] && pass "b4-cdx exit 0" || fail_msg "b4-cdx exit $RC"
if [[ -s "$WORKDIR/b4-json.stdout" ]]; then
  B4C="$(dep_count "$WORKDIR/b4-json.stdout")"
  if [[ "$B4C" == "3" ]]; then pass "spdx-b4 scan count 3"; else fail_msg "spdx-b4 scan count $B4C (want 3)"; fi
  enrich_check "E1-E4/b4-json" "$WORKDIR/b4-json.stdout" "$WORKDIR/b4-json.stdout" "json"
  [[ -s "$WORKDIR/b4-spdx.stdout" ]] && enrich_check "E1-E4/b4-spdx" "$WORKDIR/b4-spdx.stdout" "$WORKDIR/b4-json.stdout" "spdx"
  [[ -s "$WORKDIR/b4-cdx.stdout" ]] && enrich_check "E1-E4/b4-cdx" "$WORKDIR/b4-cdx.stdout" "$WORKDIR/b4-json.stdout" "cdx"
  # Guarded explicit pins (SKIP when the live-registry value is degraded offline).
  PH_STATUS="$(dep_field "$WORKDIR/b4-json.stdout" "olaf-nonexistent-pkg-xyz" "status")"
  if [[ "$PH_STATUS" == "Unknown" ]]; then
    if python3 - "$WORKDIR/b4-spdx.stdout" "$WORKDIR/b4-cdx.stdout" "$WORKDIR/b4-json.stdout" <<'PY' 2>/dev/null
import json, sys
spdx, cdx, ref = (json.load(open(p)) for p in sys.argv[1:4])
pkgs = {p["name"]: p for p in spdx["packages"]}
comps = {c["name"]: c for c in cdx["components"]}
lic = {e["name"]: e for e in ref["licenses"]}
ph = pkgs["olaf-nonexistent-pkg-xyz"]
assert ph["supplier"] == "NOASSERTION", ph
assert ph["downloadLocation"] == "NOASSERTION", ph
assert "checksums" not in ph, ph
assert "Unknown" not in json.dumps(ph), ph
assert "supplier" not in comps["olaf-nonexistent-pkg-xyz"], comps["olaf-nonexistent-pkg-xyz"]
assert "hashes" not in comps["olaf-nonexistent-pkg-xyz"]
assert "externalReferences" not in comps["olaf-nonexistent-pkg-xyz"]
assert "supplier" not in lic["olaf-nonexistent-pkg-xyz"] and "downloadUrl" not in lic["olaf-nonexistent-pkg-xyz"]
PY
    then pass "phantom Unknown end-to-end: NOASSERTION/absent everywhere, no leak";
    else fail_msg "phantom Unknown end-to-end: NOASSERTION/absence wrong"; fi
  else
    echo "SKIP: phantom Unknown pin (ref status='$PH_STATUS', live value degraded)"
  fi
  FA_DL="$(python3 - "$WORKDIR/b4-json.stdout" <<'PY' 2>/dev/null
import json, sys
for e in json.load(open(sys.argv[1]))["licenses"]:
    if e.get("name") == "font-awesome":
        print(e.get("downloadUrl", ""))
PY
)"
  if [[ -n "$FA_DL" ]]; then
    if python3 - "$WORKDIR/b4-spdx.stdout" "$WORKDIR/b4-json.stdout" "$FA_DL" <<'PY' 2>/dev/null
import json, sys
spdx, ref, want = json.load(open(sys.argv[1])), json.load(open(sys.argv[2])), sys.argv[3]
pkgs = {p["name"]: p for p in spdx["packages"]}
lic = {e["name"]: e for e in ref["licenses"]}
assert pkgs["font-awesome"]["downloadLocation"] == want, pkgs["font-awesome"]
assert pkgs["font-awesome"]["downloadLocation"] != lic["font-awesome"].get("sourceUrl"), "SourceUrl copy!"
PY
    then pass "font-awesome live pin: spdx downloadLocation==ref downloadUrl, != SourceUrl";
    else fail_msg "font-awesome live pin: downloadLocation mismatch"; fi
  else
    echo "SKIP: font-awesome download pin (ref downloadUrl absent, live value degraded)"
  fi
fi

# ---- Canonical R1 format-matrix regression (from _template.sh 0.2.3) ----
# Restored to the 4-format default on back-port: `direct` spans all 9 matrix
# formats (see format-matrix-dump.sh), so the earlier json/yaml-only loop had
# no format-coverage justification. No narrowing comment needed.
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
  echo "ENRICHMENT-PROBE OK: E1-E4 + R1 all pass."
  exit 0
else
  echo "ENRICHMENT-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
