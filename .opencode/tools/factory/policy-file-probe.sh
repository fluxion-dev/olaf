#!/usr/bin/env bash
# DEPRECATED (issue #123): RulesLoader + .sbom-rules.yaml policy surface deleted — no policy-file surface remains. Kept one issue cycle per lifecycle; delete next cycle if still unused.
# policy-file-probe.sh — Policy-file probe for issue #75 (.sbom-rules.yaml).
# Promoted v0.1.0 (2 uses ≥ bar: scratch prototype 1st use + promote-verify
#   2nd use).
# Model: policy-gate-probe.sh phantom shape + grouping-probe.sh run_scan/R1 shape.
# Offline-safe: synth phantom fixture (definitely-missing npm package =>
#   Unknown offline AND online: 404 online / transport-error offline, both
#   Unknown with a pinned unresolved-prefix reason) + synth rules files via
#   printf (never committed fixtures, never edited inline) + committed npm
#   fixture for R1 only.
#   P1 file-only deny: deny [Unknown], zero flags -> exit 1 + phantom +
#      "-> Unknown" + "1 offender(s) found" + report-write-first (stdout JSON
#      total==1).
#   P2 flag-replaces-file both directions (M2 pins, token-presence REPLACE
#      per key, not UNION):
#      deny Unknown file + --deny "" -> 0 (emptied gate off);
#      allow Unknown file + --allow MIT -> 1 (rescue replaced);
#      allow MIT file + --allow Unknown -> 0 (rescued);
#      deny MIT file alone -> 1 (fallback gate) + --allow Unknown -> 0
#      (per-key independence, deny key stands).
#   P3 exception + expiry: bounded (expires 2099) -> 0 + Suppressed + reason;
#      perpetual (no expires) -> 0; expired (2000) -> 1 + "(exception expired:";
#      scoped wrong-license -> 1 WITHOUT expired marker.
#   P4 invalid schema -> exit 2 + file:line (+col for malformed) with zero
#      stdout/--out side effects: malformed indent, unknown top-level key,
#      wrong-type deny string; + explicit missing --rules path -> 2 +
#      "Rules file not found" (L4 flag behavior, not auto-discovery).
#   P5 excludeEcosystems: [npm] -> 0 + report total 0 + licenses 0 (pre-gate
#      AND pre-report) + --ecosystem npm intersect tail -> 0/0.
#   P6 failOnUnknown-only + strict-implies: file failOnUnknown:true, zero
#      flags -> 1 + offender + NO "--allow/--deny" warning text (file-driven
#      gates declare intent); file failOnUnknown:false + --strict -> 1 +
#      "Strict mode: unknown licenses found." (flag forces gate over file).
#   P7 auto-discovery: adjacent .sbom-rules.yaml (no --rules) -> 1; fallback
#      .olaf-rules.yaml alone -> 1; dual filenames -> primary wins (deny in
#      primary vs allow in secondary -> 1); explicit --rules overrides
#      adjacent (adjacent deny vs explicit allow -> 0); no walk-up (parent
#      rules ignored -> 0); --help mentions --rules.
#   R1 matrix regression: json/yaml/xml/html `direct` markers untouched
#      (policy is gate-only post-scan/pre-report; defaults hand-coded C#).
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
PHANTOM_PKG="this-package-definitely-does-not-exist-olaf-xyz"
PHANTOM_VER="9.9.9"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

Policy-file probe (issue #75): P1 file-only deny, P2 flag-replaces-file both
directions, P3 exception+expiry, P4 invalid schema + missing path, P5
excludeEcosystems, P6 failOnUnknown-only + strict-implies, P7 auto-discovery,
R1 format-matrix regression. Offline-safe: synth phantom fixture + synth
rules files + committed npm fixture (R1 only); --strict only in P6.

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
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/policy-file-probe-XXXXXX")"
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

mkphantom() {
  # mkphantom <dir>: synth phantom package.json (valid JSON guard; python3
  #   is a hard dep of this probe so the guard stays strict).
  # Canonical source promoted to _template.sh 0.2.7 (this probe is the
  #   origin; resolve/policy-gate/cli-ux back-ports carry guard-free variants
  #   with their historical root names — root "name" is cosmetic, scanner
  #   keys on dependencies only).
  # Frozen-date idiom for synth rules files (carrier stays phantom-only):
  #   expires 2099-01-01 = bounded-live, 2000-01-01 = expired, absent = perpetual.
  local d="$1"
  mkdir -p "$d"
  printf '{\n  "name": "olaf-policy-fixture",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$PHANTOM_PKG" "$PHANTOM_VER" > "$d/package.json"
  if ! python3 -c "import json;json.load(open('$d/package.json'))" 2>/dev/null; then
    fail_msg "synth fixture invalid JSON: $d/package.json"
  fi
}

echo "== policy-file-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

echo "-- P1: file-only deny (zero flags) --"
P1="$WORKDIR/p1"; mkphantom "$P1"
printf 'deny: [Unknown]\n' > "$P1/rules.yaml"
run_scan "p1-deny" "$P1" "json" --rules "$P1/rules.yaml"
[[ "$RC" -eq 1 ]] && pass "P1 exit 1" || fail_msg "P1 exit $RC (want 1)"
if grep -q -- "$PHANTOM_PKG" "$WORKDIR/p1-deny.stderr" 2>/dev/null; then
  pass "P1 stderr lists offender $PHANTOM_PKG"
else
  fail_msg "P1 stderr missing offender $PHANTOM_PKG"
fi
if grep -q -- "-> Unknown" "$WORKDIR/p1-deny.stderr" 2>/dev/null; then
  pass "P1 stderr carries '-> Unknown' rule"
else
  fail_msg "P1 stderr missing '-> Unknown' rule"
fi
if grep -q "1 offender(s) found" "$WORKDIR/p1-deny.stderr" 2>/dev/null; then
  pass "P1 stderr carries '1 offender(s) found'"
else
  fail_msg "P1 stderr missing offender count"
fi
set +e
python3 - "$WORKDIR/p1-deny.stdout" <<'PY' 2>/dev/null
import json, sys
doc = json.load(open(sys.argv[1]))
assert doc["summary"]["total"] == 1, doc["summary"]
PY
P1RC=$?
set -e
if [[ "$P1RC" -eq 0 ]]; then
  pass "P1 report-write-first: stdout JSON total==1 on exit 1"
else
  fail_msg "P1 report-write-first: stdout JSON total!=1"
fi

echo "-- P2: flag-replaces-file both directions (per key) --"
P2="$WORKDIR/p2"; mkphantom "$P2"
printf 'deny: [Unknown]\n' > "$P2/deny-unknown.yaml"
run_scan "p2-emptied" "$P2" "json" --rules "$P2/deny-unknown.yaml" --deny ""
[[ "$RC" -eq 0 ]] && pass "P2 deny-key file->flag: --deny '' empties gate -> 0" || fail_msg "P2 deny-key emptied exit $RC (want 0; UNION impl would keep 1)"
printf 'allow: [Unknown]\n' > "$P2/allow-unknown.yaml"
run_scan "p2-unrescued" "$P2" "json" --rules "$P2/allow-unknown.yaml" --allow "MIT"
[[ "$RC" -eq 1 ]] && pass "P2 allow-key file->flag: --allow MIT replaces rescue -> 1" || fail_msg "P2 allow-key unrescued exit $RC (want 1)"
if grep -q -- "$PHANTOM_PKG" "$WORKDIR/p2-unrescued.stderr" 2>/dev/null; then
  pass "P2 unrescued stderr lists offender"
else
  fail_msg "P2 unrescued stderr missing offender"
fi
printf 'allow: [MIT]\n' > "$P2/allow-mit.yaml"
run_scan "p2-rescued" "$P2" "json" --rules "$P2/allow-mit.yaml" --allow "Unknown"
[[ "$RC" -eq 0 ]] && pass "P2 allow-key flag->file: --allow Unknown replaces -> 0" || fail_msg "P2 allow-key rescued exit $RC (want 0)"
printf 'deny: [MIT]\n' > "$P2/deny-mit.yaml"
run_scan "p2-fallback" "$P2" "json" --rules "$P2/deny-mit.yaml"
[[ "$RC" -eq 1 ]] && pass "P2 per-key: file deny MIT alone -> 1 (fallback gate)" || fail_msg "P2 per-key fallback exit $RC (want 1)"
run_scan "p2-independent" "$P2" "json" --rules "$P2/deny-mit.yaml" --allow "Unknown"
[[ "$RC" -eq 0 ]] && pass "P2 per-key: + --allow Unknown rescues -> 0 (deny key stands)" || fail_msg "P2 per-key independent exit $RC (want 0)"

echo "-- P3: exception + expiry --"
P3="$WORKDIR/p3"; mkphantom "$P3"
printf 'exceptions:\n  - name: %s\n    license: Unknown\n    reason: auditor approved\n    expires: 2099-01-01\n' "$PHANTOM_PKG" > "$P3/bounded.yaml"
run_scan "p3-bounded" "$P3" "json" --rules "$P3/bounded.yaml"
[[ "$RC" -eq 0 ]] && pass "P3 bounded exception -> 0" || fail_msg "P3 bounded exit $RC (want 0)"
if grep -q "Suppressed" "$WORKDIR/p3-bounded.stderr" 2>/dev/null && grep -q -- "$PHANTOM_PKG" "$WORKDIR/p3-bounded.stderr" 2>/dev/null && grep -q "auditor approved" "$WORKDIR/p3-bounded.stderr" 2>/dev/null; then
  pass "P3 bounded: Suppressed + phantom + reason"
else
  fail_msg "P3 bounded: Suppressed/phantom/reason missing"
fi
printf 'exceptions:\n  - name: %s\n    license: Unknown\n    reason: forever deal\n' "$PHANTOM_PKG" > "$P3/perpetual.yaml"
run_scan "p3-perpetual" "$P3" "json" --rules "$P3/perpetual.yaml"
[[ "$RC" -eq 0 ]] && pass "P3 perpetual (no expires) -> 0" || fail_msg "P3 perpetual exit $RC (want 0)"
if grep -q "forever deal" "$WORKDIR/p3-perpetual.stderr" 2>/dev/null; then
  pass "P3 perpetual: reason 'forever deal'"
else
  fail_msg "P3 perpetual: reason missing"
fi
printf 'exceptions:\n  - name: %s\n    license: Unknown\n    reason: auditor approved\n    expires: 2000-01-01\n' "$PHANTOM_PKG" > "$P3/expired.yaml"
run_scan "p3-expired" "$P3" "json" --rules "$P3/expired.yaml"
[[ "$RC" -eq 1 ]] && pass "P3 expired -> 1" || fail_msg "P3 expired exit $RC (want 1)"
if grep -q "(exception expired:" "$WORKDIR/p3-expired.stderr" 2>/dev/null && grep -q "auditor approved" "$WORKDIR/p3-expired.stderr" 2>/dev/null; then
  pass "P3 expired: '(exception expired:' + reason"
else
  fail_msg "P3 expired: expired marker/reason missing"
fi
printf 'failOnUnknown: true\nexceptions:\n  - name: %s\n    license: MIT\n    reason: wrong license\n    expires: 2099-01-01\n' "$PHANTOM_PKG" > "$P3/scoped.yaml"
run_scan "p3-scoped" "$P3" "json" --rules "$P3/scoped.yaml"
[[ "$RC" -eq 1 ]] && pass "P3 scoped wrong-license -> 1 (gate, not expiry)" || fail_msg "P3 scoped exit $RC (want 1)"
if grep -q "(exception expired:" "$WORKDIR/p3-scoped.stderr" 2>/dev/null; then
  fail_msg "P3 scoped: unexpected expired marker (exception never matched)"
else
  pass "P3 scoped: no expired marker (gate fired)"
fi

echo "-- P4: invalid schema + missing path --"
P4="$WORKDIR/p4"; mkphantom "$P4"
printf 'deny:\n - MIT\n  - BADINDENT\n   : : :\n' > "$P4/bad.yaml"
OUT4="$P4/out.json"
rm -f "$OUT4"
run_scan "p4-malformed" "$P4" "json" --rules "$P4/bad.yaml" --out "$OUT4"
[[ "$RC" -eq 2 ]] && pass "P4 malformed -> 2" || fail_msg "P4 malformed exit $RC (want 2)"
if grep -q -- "$P4/bad.yaml" "$WORKDIR/p4-malformed.stderr" 2>/dev/null && grep -qE ":[0-9]+:[0-9]+" "$WORKDIR/p4-malformed.stderr" 2>/dev/null; then
  pass "P4 malformed: file:line:col in stderr"
else
  fail_msg "P4 malformed: file:line:col missing"
fi
if [[ -f "$OUT4" ]]; then
  fail_msg "P4 malformed: --out file written (schema errors exit BEFORE report)"
else
  pass "P4 malformed: no --out file (exit-before-write)"
fi
printf 'bogusKey: true\ndeny: [Unknown]\n' > "$P4/unknown-key.yaml"
run_scan "p4-unknownkey" "$P4" "json" --rules "$P4/unknown-key.yaml"
[[ "$RC" -eq 2 ]] && pass "P4 unknown-key -> 2" || fail_msg "P4 unknown-key exit $RC (want 2)"
if grep -q -- "$P4/unknown-key.yaml" "$WORKDIR/p4-unknownkey.stderr" 2>/dev/null && grep -qE ":[0-9]+:[0-9]+" "$WORKDIR/p4-unknownkey.stderr" 2>/dev/null && grep -q "unknown key 'bogusKey'" "$WORKDIR/p4-unknownkey.stderr" 2>/dev/null; then
  pass "P4 unknown-key: file:line + unknown key 'bogusKey'"
else
  fail_msg "P4 unknown-key: file:line/key pin missing"
fi
if [[ -s "$WORKDIR/p4-unknownkey.stdout" ]]; then
  fail_msg "P4 unknown-key: stdout not empty (schema errors write no report)"
else
  pass "P4 unknown-key: stdout empty (no partial report)"
fi
printf 'deny: "MIT"\n' > "$P4/wrongtype.yaml"
run_scan "p4-wrongtype" "$P4" "json" --rules "$P4/wrongtype.yaml"
[[ "$RC" -eq 2 ]] && pass "P4 wrong-type -> 2" || fail_msg "P4 wrong-type exit $RC (want 2)"
if grep -q "must be a list of strings" "$WORKDIR/p4-wrongtype.stderr" 2>/dev/null; then
  pass "P4 wrong-type: 'must be a list of strings'"
else
  fail_msg "P4 wrong-type: schema message missing"
fi
MISSING="$P4/does-not-exist-rules.yaml"
run_scan "p4-missing" "$P4" "json" --rules "$MISSING"
[[ "$RC" -eq 2 ]] && pass "P4 missing path -> 2" || fail_msg "P4 missing exit $RC (want 2)"
if grep -q "Rules file not found" "$WORKDIR/p4-missing.stderr" 2>/dev/null && grep -q -- "$MISSING" "$WORKDIR/p4-missing.stderr" 2>/dev/null; then
  pass "P4 missing: 'Rules file not found' + path"
else
  fail_msg "P4 missing: not-found message/path missing"
fi

echo "-- P5: excludeEcosystems --"
P5="$WORKDIR/p5"; mkphantom "$P5"
printf 'excludeEcosystems: [npm]\n' > "$P5/rules.yaml"
run_scan "p5-excluded" "$P5" "json" --rules "$P5/rules.yaml"
[[ "$RC" -eq 0 ]] && pass "P5 excluded -> 0" || fail_msg "P5 excluded exit $RC (want 0)"
set +e
python3 - "$WORKDIR/p5-excluded.stdout" <<'PY' 2>/dev/null
import json, sys
doc = json.load(open(sys.argv[1]))
assert doc["summary"]["total"] == 0, doc["summary"]
assert len(doc["licenses"]) == 0, len(doc["licenses"])
PY
P5RC=$?
set -e
if [[ "$P5RC" -eq 0 ]]; then
  pass "P5 report: total 0 + licenses 0 (pre-gate AND pre-report)"
else
  fail_msg "P5 report: total/licenses not 0"
fi
run_scan "p5-scoped" "$P5" "json" --rules "$P5/rules.yaml" --ecosystem "npm"
[[ "$RC" -eq 0 ]] && pass "P5 intersect: --ecosystem npm + exclude npm -> 0" || fail_msg "P5 intersect exit $RC (want 0)"
set +e
python3 - "$WORKDIR/p5-scoped.stdout" <<'PY' 2>/dev/null
import json, sys
doc = json.load(open(sys.argv[1]))
assert doc["summary"]["total"] == 0, doc["summary"]
PY
P5SRC=$?
set -e
if [[ "$P5SRC" -eq 0 ]]; then
  pass "P5 intersect report: total 0"
else
  fail_msg "P5 intersect report: total not 0"
fi

echo "-- P6: failOnUnknown-only + strict-implies --"
P6="$WORKDIR/p6"; mkphantom "$P6"
printf 'failOnUnknown: true\n' > "$P6/rules.yaml"
run_scan "p6-file" "$P6" "json" --rules "$P6/rules.yaml"
[[ "$RC" -eq 1 ]] && pass "P6 file failOnUnknown:true (zero flags) -> 1" || fail_msg "P6 file exit $RC (want 1)"
if grep -q -- "$PHANTOM_PKG" "$WORKDIR/p6-file.stderr" 2>/dev/null; then
  pass "P6 file: offender listed"
else
  fail_msg "P6 file: offender missing"
fi
if grep -q "Warning: --allow/--deny" "$WORKDIR/p6-file.stderr" 2>/dev/null; then
  fail_msg "P6 file: unexpected flag warning (file-driven gates declare intent)"
else
  pass "P6 file: no flag warning text"
fi
printf 'failOnUnknown: false\n' > "$P6/off.yaml"
run_scan "p6-strict" "$P6" "json" --rules "$P6/off.yaml" --strict
[[ "$RC" -eq 1 ]] && pass "P6 strict-implies: file false + --strict -> 1" || fail_msg "P6 strict exit $RC (want 1)"
if grep -q "Strict mode: unknown licenses found." "$WORKDIR/p6-strict.stderr" 2>/dev/null; then
  pass "P6 strict: default-gate message (no per-offender pin)"
else
  fail_msg "P6 strict: strict message missing"
fi

echo "-- P7: auto-discovery --"
P7A="$WORKDIR/p7a"; mkphantom "$P7A"
printf 'deny: [Unknown]\n' > "$P7A/.sbom-rules.yaml"
run_scan "p7-primary" "$P7A" "json"
[[ "$RC" -eq 1 ]] && pass "P7 primary: adjacent .sbom-rules.yaml (no --rules) -> 1" || fail_msg "P7 primary exit $RC (want 1)"
if grep -q -- "$PHANTOM_PKG" "$WORKDIR/p7-primary.stderr" 2>/dev/null; then
  pass "P7 primary: offender listed"
else
  fail_msg "P7 primary: offender missing"
fi
P7B="$WORKDIR/p7b"; mkphantom "$P7B"
printf 'deny: [Unknown]\n' > "$P7B/.olaf-rules.yaml"
run_scan "p7-fallback" "$P7B" "json"
[[ "$RC" -eq 1 ]] && pass "P7 fallback: .olaf-rules.yaml alone -> 1" || fail_msg "P7 fallback exit $RC (want 1)"
P7C="$WORKDIR/p7c"; mkphantom "$P7C"
printf 'allow: [Unknown]\n' > "$P7C/.olaf-rules.yaml"
printf 'deny: [Unknown]\n' > "$P7C/.sbom-rules.yaml"
run_scan "p7-dual" "$P7C" "json"
[[ "$RC" -eq 1 ]] && pass "P7 dual: primary wins (deny vs allow -> 1)" || fail_msg "P7 dual exit $RC (want 1)"
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$P7C" --format json --verbose >"$WORKDIR/p7-dual-verbose.stdout" 2>"$WORKDIR/p7-dual-verbose.stderr"
P7VRC=$?
set -e
if grep -q "Using '.sbom-rules.yaml'; ignoring '.olaf-rules.yaml'." "$WORKDIR/p7-dual-verbose.stderr" 2>/dev/null; then
  pass "P7 dual: --verbose shadow note"
else
  fail_msg "P7 dual: shadow note missing (RC=$P7VRC)"
fi
P7D="$WORKDIR/p7d"; mkphantom "$P7D"
printf 'deny: [Unknown]\n' > "$P7D/.sbom-rules.yaml"
printf 'allow: [Unknown]\n' > "$WORKDIR/explicit-allow.yaml"
run_scan "p7-explicit" "$P7D" "json" --rules "$WORKDIR/explicit-allow.yaml"
[[ "$RC" -eq 0 ]] && pass "P7 explicit: --rules overrides adjacent (deny vs allow -> 0)" || fail_msg "P7 explicit exit $RC (want 0)"
P7E_PARENT="$WORKDIR/p7e"; P7E="$P7E_PARENT/child"; mkphantom "$P7E"
printf 'deny: [Unknown]\n' > "$P7E_PARENT/.sbom-rules.yaml"
run_scan "p7-nowalkup" "$P7E" "json"
[[ "$RC" -eq 0 ]] && pass "P7 no-walk-up: parent rules ignored -> 0" || fail_msg "P7 no-walk-up exit $RC (want 0)"
P7F="$WORKDIR/p7f"; mkphantom "$P7F"
printf 'deny: [Unknown]\n' > "$P7F/.sbom-rules.yaml"
run_scan "p7-fileinput" "$P7F/package.json" "json"
[[ "$RC" -eq 1 ]] && pass "P7 file-input: rules adjacent to package.json fire -> 1" || fail_msg "P7 file-input exit $RC (want 1)"
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --help >"$WORKDIR/p7-help.stdout" 2>"$WORKDIR/p7-help.stderr"
P7HRC=$?
set -e
if [[ "$P7HRC" -eq 0 ]] && grep -q -- "--rules" "$WORKDIR/p7-help.stdout" 2>/dev/null; then
  pass "P7 help: --help mentions --rules"
else
  fail_msg "P7 help: --rules missing from --help (RC=$P7HRC)"
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
  echo "POLICY-FILE-PROBE OK: P1-P7 + R1 all pass."
  exit 0
else
  echo "POLICY-FILE-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
