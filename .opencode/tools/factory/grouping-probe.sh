#!/usr/bin/env bash
# DEPRECATED (issue #123): --group-by-license flag + LicenseGrouper deleted — no grouping surface remains. Kept one issue cycle per lifecycle; delete next cycle if still unused.
# grouping-probe.sh — Grouping probe for issue #74 (promoted v0.1.0, 2 uses ≥ bar:
#   scratch prototype 1st use + promote-verify 2nd use).
# Model: template-probe.sh run_scan/check shape + canonical R1 block.
# Offline-safe: never passes --strict; synth phantom fixtures (printf/bash-loop
#   generated package.json under $WORKDIR) + committed npm fixture only.
#   Phantom names (olaf-group-pkg-*) 404 online / transport-error offline —
#   both resolve Unknown, so Unknown-shape asserts are deterministic either way.
#   G1 50-synth collapse: ungrouped txt carries 50 "^  SPDX:" blocks, grouped
#      txt/md/html collapse to ONE group block + "50 packages under 1 licenses".
#      NOTE: the single block is "## Unknown (50 packages)" at the CLI level —
#      the CLI cannot mint MIT licenses offline (npm resolution is
#      registry-backed), so MIT-keyed collapse stays unit-pinned
#      (GroupByLicenseTests.Should_GroupFiftyMitIntoSingleBlock_*); the probe
#      pins the deterministic property (N blocks -> 1 block, bullets intact).
#   G2 mixed ordering: synth express@4.18.2 + typescript@5.3.3 + phantom;
#      structural python check (headers Ordinal-asc, Unknown LAST) ALWAYS runs
#      (offline single-Unknown-group passes trivially); exact Apache<MIT<
#      Unknown positional pins fire only when all three headers present, else
#      SKIP (offline-degraded). Full 4-way MIT/Apache/GPL+Unknown order is
#      unit-pinned (Should_OrderGroupsOrdinalAsc_WithUnknownLast).
#   G3 Unknown: single phantom -> "## Unknown (1 packages)" + per-package
#      ": {reason}" bullet (reason text differs online/offline — assert
#      non-empty, not literal) + count match (Unknown: 1 == 1 reason bullet).
#   G4 default-identity: txt twice without flag diff-identical + no
#      "packages under" marker (default branch byte-identical shape); md/html
#      without flag marker-free; json with/without flag diff-identical +
#      dep_count==3 both; spdx-json with/without flag identical modulo the two
#      emission-nondeterminism fields (documentNamespace Guid + created UtcNow,
#      sed-normalized before diff).
#   G5 counts-header: "X packages under Y licenses" with X==Total and Y==
#      "## "-header count (FIX3 3/1 + G1 50/1 cross-check).
#   G6 --help mentions --group-by-license.
#   R1 matrix regression: json/yaml/xml/html `direct` markers untouched
#      (grouping is display-only post-scan/pre-format; defaults hand-coded C#).
# Rules: repo-relative, idempotent (mktemp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail

# ---- Canonical root resolution (factory depth: ../../.. per _template.sh) ----
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
TIMEOUT_SECS=90
PROJECT_REL="src/Olaf.Cli"
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

Grouping probe (issue #74): G1 50-synth collapse (txt/md/html), G2 mixed
ordering (Unknown LAST), G3 Unknown reasons, G4 default-identity (ungrouped
shape + SBOM json/spdx with/without flag), G5 counts-header, G6 --help,
R1 format-matrix regression. Offline-safe: synth phantom fixtures +
committed npm fixture, never --strict.

Options:
  --timeout <n>   per-scan timeout in seconds (default: 90)
  --workdir <dir> work dir (default: mktemp -d under \${TMPDIR:-/tmp})
  --keep-temp     keep temp work dir for debugging (default: remove)
  --help          show this help and exit 0
  --version       print VERSION and exit 0

Exit codes: 0 all PASS, 1 assertion failure, 2 usage/environment error.

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 60 --keep-temp
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
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/grouping-probe-XXXXXX")"
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

# ---- Canonical dep extractors (from _template.sh 0.2.2; python3, no jq) ----
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

echo "== grouping-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

echo "-- synth fixtures (printf/bash-loop package.json, phantom names) --"
FIX50="$WORKDIR/fix50"; FIXMIX="$WORKDIR/fixmix"
FIX1="$WORKDIR/fix1"; FIX3="$WORKDIR/fix3"
mkdir -p "$FIX50" "$FIXMIX" "$FIX1" "$FIX3"
{
printf '{\n  "name": "olaf-grouping-50",\n  "version": "1.0.0",\n  "dependencies": {\n'
for i in $(seq -w 0 49); do
  comma=","; [[ "$i" == "49" ]] && comma=""
  printf '    "olaf-group-pkg-%s": "1.0.0"%s\n' "$i" "$comma"
done
printf '  }\n}\n'
} > "$FIX50/package.json"
printf '{\n  "name": "olaf-grouping-mix",\n  "version": "1.0.0",\n  "dependencies": {\n    "express": "4.18.2",\n    "typescript": "5.3.3",\n    "olaf-grouping-phantom-xyz": "9.9.9"\n  }\n}\n' > "$FIXMIX/package.json"
printf '{\n  "name": "olaf-grouping-one",\n  "version": "1.0.0",\n  "dependencies": {\n    "olaf-phantom-xyz": "9.9.9"\n  }\n}\n' > "$FIX1/package.json"
printf '{\n  "name": "olaf-grouping-three",\n  "version": "1.0.0",\n  "dependencies": {\n    "olaf-g4-a": "9.9.9",\n    "olaf-g4-b": "9.9.9",\n    "olaf-g4-c": "9.9.9"\n  }\n}\n' > "$FIX3/package.json"
for d in "$FIX50" "$FIXMIX" "$FIX1" "$FIX3"; do
  if ! python3 -c "import json;json.load(open('$d/package.json'))" 2>/dev/null; then
    fail_msg "synth fixture invalid JSON: $d/package.json"
  fi
done
pass "synth fixtures: 4 package.json valid (50/3/1/3 deps)"

echo "-- G1: 50-synth collapse (ungrouped N blocks -> grouped 1 block) --"
run_scan "g1-plain" "$FIX50" "txt"
[[ "$RC" -eq 0 ]] && pass "G1 plain exit 0" || fail_msg "G1 plain exit $RC (want 0)"
if grep -q "Total: 50, Resolved: 0, Unknown: 50" "$WORKDIR/g1-plain.stdout" 2>/dev/null; then
  pass "G1 plain: Total 50 / Unknown 50 (phantoms deterministic)"
else
  fail_msg "G1 plain: want 'Total: 50, Resolved: 0, Unknown: 50'"
fi
n_spdx=$(grep -c "^  SPDX:" "$WORKDIR/g1-plain.stdout" || true)
[[ "$n_spdx" == "50" ]] && pass "G1 plain: 50 SPDX blocks" || fail_msg "G1 plain: $n_spdx SPDX blocks (want 50)"
if grep -q "packages under" "$WORKDIR/g1-plain.stdout" 2>/dev/null; then
  fail_msg "G1 plain: grouping marker leaks into default output"
else
  pass "G1 plain: no grouping marker (default shape)"
fi
run_scan "g1-txt" "$FIX50" "txt" --group-by-license
run_scan "g1-md" "$FIX50" "md" --group-by-license
run_scan "g1-html" "$FIX50" "html" --group-by-license
for t in g1-txt g1-md g1-html; do
  [[ "$(head -c 400 "$WORKDIR/$t.stdout" 2>/dev/null | wc -c)" -gt 0 ]] || { fail_msg "G1 $t: empty stdout"; continue; }
done
[[ "$RC" -eq 0 ]] && pass "G1 grouped scans exit 0" || fail_msg "G1 grouped exit $RC (want 0)"
if grep -q "50 packages under 1 licenses" "$WORKDIR/g1-txt.stdout" 2>/dev/null; then
  pass "G1 txt: '50 packages under 1 licenses'"
else
  fail_msg "G1 txt: counts-header missing"
fi
n_head=$(grep -c "^## " "$WORKDIR/g1-txt.stdout" || true)
[[ "$n_head" == "1" ]] && pass "G1 txt: exactly 1 group block" || fail_msg "G1 txt: $n_head group blocks (want 1)"
if grep -q "^## Unknown (50 packages)$" "$WORKDIR/g1-txt.stdout" 2>/dev/null; then
  pass "G1 txt: single '## Unknown (50 packages)' block"
else
  fail_msg "G1 txt: single Unknown block header missing"
fi
n_bul=$(grep -c "^- olaf-group-pkg-" "$WORKDIR/g1-txt.stdout" || true)
[[ "$n_bul" == "50" ]] && pass "G1 txt: 50 bullets intact" || fail_msg "G1 txt: $n_bul bullets (want 50)"
if grep -q "^## Unknown (50 packages)$" "$WORKDIR/g1-md.stdout" 2>/dev/null; then
  pass "G1 md: same single Unknown block"
else
  fail_msg "G1 md: single Unknown block header missing"
fi
if grep -q "| npm |" "$WORKDIR/g1-md.stdout" 2>/dev/null; then
  fail_msg "G1 md: ungrouped table leaks into grouped output"
else
  pass "G1 md: no ungrouped table (grouped shape)"
fi
n_mdbul=$(grep -c "^- olaf-group-pkg-" "$WORKDIR/g1-md.stdout" || true)
[[ "$n_mdbul" == "50" ]] && pass "G1 md: 50 bullets intact" || fail_msg "G1 md: $n_mdbul bullets (want 50)"
if grep -q "<h2>Unknown (50 packages)</h2>" "$WORKDIR/g1-html.stdout" 2>/dev/null; then
  pass "G1 html: same single Unknown block"
else
  fail_msg "G1 html: single Unknown block header missing"
fi
n_li=$(grep -o "<li>" "$WORKDIR/g1-html.stdout" 2>/dev/null | wc -l | tr -d ' ')
[[ "$n_li" == "50" ]] && pass "G1 html: 50 <li> intact" || fail_msg "G1 html: $n_li <li> (want 50)"
if grep -qi "<table" "$WORKDIR/g1-html.stdout" 2>/dev/null; then
  fail_msg "G1 html: ungrouped table leaks into grouped output"
else
  pass "G1 html: no ungrouped table (grouped shape)"
fi

echo "-- G2: mixed-license ordering (Ordinal asc, Unknown LAST) --"
run_scan "g2-grouped" "$FIXMIX" "txt" --group-by-license
[[ "$RC" -eq 0 ]] && pass "G2 scan exit 0" || fail_msg "G2 scan exit $RC (want 0)"
set +e
python3 - "$WORKDIR/g2-grouped.stdout" <<'PY' 2>/dev/null
import re, sys
text = open(sys.argv[1]).read()
heads = re.findall(r'^## (.+) \((\d+) packages\)', text, re.M)
keys = [h[0] for h in heads]
if not keys:
    sys.exit("no group headers")
if 'Unknown' in keys and keys[-1] != 'Unknown':
    sys.exit("Unknown not last: " + "|".join(keys))
rest = [k for k in keys if k != 'Unknown']
if rest != sorted(rest):
    sys.exit("not ordinal-asc: " + "|".join(keys))
print("order ok: " + " | ".join(keys))
PY
G2RC=$?
set -e
if [[ "$G2RC" -eq 0 ]]; then
  pass "G2 structural: headers Ordinal-asc with Unknown last"
else
  fail_msg "G2 structural: group order wrong (see python error above)"
fi
if grep -q "^## Apache-2.0 " "$WORKDIR/g2-grouped.stdout" 2>/dev/null \
  && grep -q "^## MIT " "$WORKDIR/g2-grouped.stdout" 2>/dev/null \
  && grep -q "^## Unknown " "$WORKDIR/g2-grouped.stdout" 2>/dev/null; then
  a=$(grep -n "^## Apache-2.0 " "$WORKDIR/g2-grouped.stdout" | head -n 1 | cut -d: -f1)
  m=$(grep -n "^## MIT " "$WORKDIR/g2-grouped.stdout" | head -n 1 | cut -d: -f1)
  u=$(grep -n "^## Unknown " "$WORKDIR/g2-grouped.stdout" | head -n 1 | cut -d: -f1)
  if [[ "$a" -lt "$m" ]] && [[ "$m" -lt "$u" ]]; then
    pass "G2 exact: Apache-2.0 ($a) < MIT ($m) < Unknown ($u)"
  else
    fail_msg "G2 exact: order Apache($a)/MIT($m)/Unknown($u) wrong"
  fi
else
  echo "SKIP: G2 exact Apache/MIT/Unknown pins (live licenses degraded offline; 4-way order unit-pinned)"
fi

echo "-- G3: Unknown header + per-package Reason lines + count match --"
run_scan "g3-grouped" "$FIX1" "txt" --group-by-license
[[ "$RC" -eq 0 ]] && pass "G3 scan exit 0" || fail_msg "G3 scan exit $RC (want 0)"
if grep -q "^## Unknown (1 packages)$" "$WORKDIR/g3-grouped.stdout" 2>/dev/null; then
  pass "G3: '## Unknown (1 packages)' header"
else
  fail_msg "G3: Unknown header missing"
fi
if grep -qE "^- olaf-phantom-xyz@9\.9\.9 \(npm\): .+" "$WORKDIR/g3-grouped.stdout" 2>/dev/null; then
  pass "G3: phantom bullet carries non-empty Reason"
else
  fail_msg "G3: phantom bullet lacks ': {reason}'"
fi
n_reason=$(grep -cE "^- olaf-phantom-xyz@9\.9\.9 \(npm\): .+" "$WORKDIR/g3-grouped.stdout" || true)
if grep -q "Unknown: 1" "$WORKDIR/g3-grouped.stdout" 2>/dev/null && [[ "$n_reason" == "1" ]]; then
  pass "G3: count match (Unknown: 1 == 1 reason bullet)"
else
  fail_msg "G3: count mismatch (Unknown-total vs $n_reason reason bullets)"
fi

echo "-- G4: default-identity (ungrouped shape + SBOM with/without flag) --"
run_scan "g4-txt-a" "$FIX3" "txt"
run_scan "g4-txt-b" "$FIX3" "txt"
if [[ "$RC" -eq 0 ]] && diff -q "$WORKDIR/g4-txt-a.stdout" "$WORKDIR/g4-txt-b.stdout" >/dev/null; then
  pass "G4 txt: no-flag runs byte-identical (idempotent)"
else
  fail_msg "G4 txt: no-flag runs differ (RC=$RC)"
fi
if grep -q "packages under" "$WORKDIR/g4-txt-a.stdout" 2>/dev/null; then
  fail_msg "G4 txt: grouping marker leaks into default output"
else
  pass "G4 txt: default branch marker-free"
fi
run_scan "g4-md" "$FIX3" "md"
run_scan "g4-html" "$FIX3" "html"
if grep -q "packages under" "$WORKDIR/g4-md.stdout" "$WORKDIR/g4-html.stdout" 2>/dev/null; then
  fail_msg "G4 md/html: grouping marker leaks into default output"
else
  pass "G4 md/html: default branches marker-free"
fi
run_scan "g4-json-plain" "$FIX3" "json"
run_scan "g4-json-flag" "$FIX3" "json" --group-by-license
if [[ "$RC" -eq 0 ]] && diff -q "$WORKDIR/g4-json-plain.stdout" "$WORKDIR/g4-json-flag.stdout" >/dev/null; then
  pass "G4 json: identical with/without flag (ignored for SBOM)"
else
  fail_msg "G4 json: with/without flag differ (RC=$RC)"
fi
c_plain=$(dep_count "$WORKDIR/g4-json-plain.stdout")
c_flag=$(dep_count "$WORKDIR/g4-json-flag.stdout")
if [[ "$c_plain" == "3" ]] && [[ "$c_flag" == "3" ]]; then
  pass "G4 json: dep_count 3 both runs"
else
  fail_msg "G4 json: dep_count plain=$c_plain flag=$c_flag (want 3/3)"
fi
v_pin=$(dep_field "$WORKDIR/g4-json-flag.stdout" "olaf-g4-a" "version")
[[ "$v_pin" == "9.9.9" ]] && pass "G4 json: dep_field phantom version 9.9.9" || fail_msg "G4 json: dep_field got '$v_pin' (want 9.9.9)"
run_scan "g4-spdx-plain" "$FIX3" "spdx-json"
run_scan "g4-spdx-flag" "$FIX3" "spdx-json" --group-by-license
if [[ "$RC" -eq 0 ]]; then
  sed -E -e 's/"documentNamespace":[ ]*"[^"]*"/"documentNamespace":"NS"/' -e 's/"created":[ ]*"[^"]*"/"created":"TS"/' "$WORKDIR/g4-spdx-plain.stdout" > "$WORKDIR/g4-spdx-plain.norm"
  sed -E -e 's/"documentNamespace":[ ]*"[^"]*"/"documentNamespace":"NS"/' -e 's/"created":[ ]*"[^"]*"/"created":"TS"/' "$WORKDIR/g4-spdx-flag.stdout" > "$WORKDIR/g4-spdx-flag.norm"
  if diff -q "$WORKDIR/g4-spdx-plain.norm" "$WORKDIR/g4-spdx-flag.norm" >/dev/null; then
    pass "G4 spdx-json: identical with/without flag (modulo namespace/created nonce)"
  else
    fail_msg "G4 spdx-json: with/without flag differ beyond nonce fields"
  fi
else
  fail_msg "G4 spdx-json scan exit $RC (want 0)"
fi

echo "-- G5: counts-header (X packages under Y licenses) --"
run_scan "g5-grouped" "$FIX3" "txt" --group-by-license
[[ "$RC" -eq 0 ]] && pass "G5 scan exit 0" || fail_msg "G5 scan exit $RC (want 0)"
hdr=$(grep -oE "^[0-9]+ packages under [0-9]+ licenses$" "$WORKDIR/g5-grouped.stdout" 2>/dev/null | head -n 1 || true)
if [[ "$hdr" == "3 packages under 1 licenses" ]]; then
  pass "G5: header '$hdr'"
else
  fail_msg "G5: header got '$hdr' (want '3 packages under 1 licenses')"
fi
y_count=$(grep -c "^## " "$WORKDIR/g5-grouped.stdout" || true)
if grep -q "Total: 3, Resolved: 0, Unknown: 3" "$WORKDIR/g5-grouped.stdout" 2>/dev/null && [[ "$y_count" == "1" ]]; then
  pass "G5: X==Total(3), Y==group-headers(1)"
else
  fail_msg "G5: X/Y mismatch (headers=$y_count)"
fi
if grep -q "50 packages under 1 licenses" "$WORKDIR/g1-txt.stdout" 2>/dev/null; then
  pass "G5 cross-check: G1 50/1 header consistent"
else
  fail_msg "G5 cross-check: G1 50/1 header missing"
fi

echo "-- G6: --help mentions flag --"
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --help >"$WORKDIR/g6-help.stdout" 2>"$WORKDIR/g6-help.stderr"
G6RC=$?
set -e
if [[ "$G6RC" -eq 0 ]] && grep -q -- "--group-by-license" "$WORKDIR/g6-help.stdout"; then
  pass "G6: --help mentions --group-by-license"
else
  fail_msg "G6: --help lacks flag (RC=$G6RC)"
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
  echo "GROUPING-PROBE OK: G1-G6 + R1 all pass."
  exit 0
else
  echo "GROUPING-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
