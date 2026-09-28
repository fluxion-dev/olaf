#!/usr/bin/env bash
# spdx-db-probe.sh — SPDX offline-DB probe for issue #77 (promoted v0.1.0, 2 uses ≥ bar:
# scratch prototype 1st use + promote-verify 2nd use).
# Embedded offline SPDX DB oracle: validates the checked-in DB asset, its
# loader, the maintainer refresh script + hash pin, Normalize round-trip, and
# fetcher chain-head wiring. Reuses license-text-probe 0.1.3 L3 shape +
# canonical air_gap_grep; does NOT fork spdx-probe/license-text-probe logic
# (full format-matrix regression stays in license-text-probe R1 — here only a
# scoped chain-head wiring check).
# B1 count+pin: JSON parses, count==35, exact 35-id v3.29.0 seed set, ids in
#   LC_ALL=C byte order, metadata-first key order (id,name,osi,fsf,deprecated,text).
# B2 idempotency+hash: jq validity gate + LC_ALL=C sort -c (both mirror the
#   script's stage gates) + python re-emit byte-identical with the script's
#   exact parameters (deterministic-rebuild equivalent, no network),
#   update script pins SPDX_VERSION=v3.29.0 + LC_ALL=C + jq -S + sha256sum + 5242880.
# B3 air-gap: canonical air_gap_grep on SpdxLicenseDb.cs (zero HttpClient) +
#   EmbeddedResource pin in csproj.
# B4 size gate: checked-in DB <5MB (5242880 bytes, D3).
# B5 Normalize round-trip: LicenseRef regex + case-insensitive or-later +
#   bare-gpl→null static pins; behavioral proof = filtered SpdxDbTests +
#   SpdxDbScriptTests + SpdxMapperNormalizeTests runs (throwing-handler
#   offline proofs live in the suite, not here).
# R1-scoped: fetcher chain-head wiring (SpdxLicenseDb.TryGetText before
#   SpdxLicenseTexts fallback, offline-guarded HTTP) — scoped equivalent, NOT
#   the full matrix (see license-text-probe R1).
# Rules: repo-relative, idempotent (mktemp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.0"
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
TESTPROJ_REL="tests/Olaf.Tests/Olaf.Tests.csproj"
DBJSON_REL="src/Olaf.Resolvers/Data/spdx-licenses.json"
DBCS_REL="src/Olaf.Resolvers/SpdxLicenseDb.cs"
MAPPER_REL="src/Olaf.Resolvers/SpdxMapper.cs"
FETCHER_REL="src/Olaf.Resolvers/LicenseTextFetcher.cs"
SCRIPT_REL="tools/update-spdx-db.sh"
HASH_REL="tools/spdx-db.sha256"
CSPROJ_REL="src/Olaf.Resolvers/Olaf.Resolvers.csproj"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

SPDX offline-DB probe (issue #77): count+pin (B1), idempotency+hash (B2),
air-gap (B3), size gate (B4), Normalize round-trip (B5), chain-head wiring
(R1-scoped). Offline-safe: static reads + filtered dotnet-test runs only;
never executes $SCRIPT_REL (MAINTAINER-NETWORK).

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

TESTPROJ="$ROOT/$TESTPROJ_REL"
DBJSON="$ROOT/$DBJSON_REL"
DBCS="$ROOT/$DBCS_REL"
MAPPER="$ROOT/$MAPPER_REL"
FETCHER="$ROOT/$FETCHER_REL"
SCRIPT="$ROOT/$SCRIPT_REL"
HASH="$ROOT/$HASH_REL"
CSPROJ="$ROOT/$CSPROJ_REL"
[[ -f "$TESTPROJ" ]] || { echo "Test project not found: $TESTPROJ" >&2; exit 2; }
[[ -f "$DBJSON" ]] || { echo "DB asset not found: $DBJSON" >&2; exit 2; }
[[ -f "$DBCS" ]] || { echo "DB loader not found: $DBCS" >&2; exit 2; }
[[ -f "$MAPPER" ]] || { echo "Mapper not found: $MAPPER" >&2; exit 2; }
[[ -f "$FETCHER" ]] || { echo "Fetcher not found: $FETCHER" >&2; exit 2; }
[[ -f "$SCRIPT" ]] || { echo "Update script not found: $SCRIPT" >&2; exit 2; }
[[ -f "$HASH" ]] || { echo "Hash pin not found: $HASH" >&2; exit 2; }
[[ -f "$CSPROJ" ]] || { echo "Resolvers csproj not found: $CSPROJ" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3 jq sha256sum; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/spdx-db-probe-XXXXXX")"
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
run_t() {
  # run_t <tag> <filter>; dotnet test -> <tag>.stdout
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
  # t_ok <tag> <label>; PASS iff rc 0 + Passed! in stdout
  local tag="$1" label="$2"
  if [[ "$RC" -eq 0 ]] && grep -q "Passed!" "$WORKDIR/$tag.stdout"; then
    pass "$label"
  else
    fail_msg "$label (rc=$RC)"
    tail -n 5 "$WORKDIR/$tag.stdout" 2>/dev/null || true
  fi
}

# ---- B1: count + pin (shape, order, key order) ----
echo "-- B1 count+pin --"
set +e
python3 - "$DBJSON" >"$WORKDIR/b1.stdout" 2>"$WORKDIR/b1.stderr" <<'PY'
import json, sys
EXPECT = ["0BSD","AAL","AGPL-1.0-only","AGPL-3.0-only","AGPL-3.0-or-later",
"Apache-1.1","Apache-2.0","Artistic-2.0","BSD-2-Clause","BSD-3-Clause",
"BSD-4-Clause","BSL-1.0","CC0-1.0","CDDL-1.0","EPL-1.0","EPL-2.0",
"GPL-1.0-only","GPL-2.0-only","GPL-2.0-or-later","GPL-3.0-only","GPL-3.0-or-later",
"ISC","LGPL-2.0-only","LGPL-2.1-only","LGPL-2.1-or-later","LGPL-3.0-only",
"LGPL-3.0-or-later","MIT","MIT-0","MPL-1.0","MPL-1.1","MPL-2.0",
"OFL-1.1","Unlicense","Zlib"]
KEYS = ["id","name","osi","fsf","deprecated","text"]
try:
    data = json.load(open(sys.argv[1]))
except Exception as e:
    print(f"JSON parse failed: {e}")
    sys.exit(1)
ids = [e["id"] for e in data]
ok = True
if len(data) != 35:
    print(f"count {len(data)} (want 35)"); ok = False
else:
    print("count 35")
if ids != EXPECT:
    print(f"id set/order mismatch: extra={sorted(set(ids)-set(EXPECT))} missing={sorted(set(EXPECT)-set(ids))}"); ok = False
else:
    print("id set pinned to v3.29.0 35-seed")
import locale
if ids != sorted(ids):
    print("ids not in LC_ALL=C byte order"); ok = False
else:
    print("ids in LC_ALL=C byte order")
for e in data:
    if list(e.keys()) != KEYS:
        print(f"key order wrong for {e.get('id')}: {list(e.keys())}"); ok = False; break
else:
    print("metadata-first key order")
sys.exit(0 if ok else 1)
PY
B1_RC=$?
set -e
cat "$WORKDIR/b1.stdout"
if [[ "$B1_RC" -eq 0 ]]; then pass "B1 DB: 35-id v3.29.0 seed set, sorted, metadata-first"; else fail_msg "B1 DB: count/pin/order wrong"; fi

# ---- B2: idempotency + hash ----
echo "-- B2 idempotency+hash --"
# B2 idempotency: mirrors tools/update-spdx-db.sh stage gates — (a) jq -S
# validity gate passes (script: `jq -S . "$OUT_JSON" > /dev/null`), (b)
# LC_ALL=C id-order check passes (script: `jq -r '.[].id' | LC_ALL=C sort -c`),
# (c) python re-emit with the script's exact parameters (metadata-first keys,
# id-byte sort, indent=2, ensure_ascii=False, trailing newline) is
# byte-identical = deterministic-rebuild equivalent without network.
if jq -S . "$DBJSON" > /dev/null 2>&1; then
  pass "B2 DB: jq validity gate passes (mirrors script jq -S check)"
else
  fail_msg "B2 DB: jq validity gate fails"
fi
if jq -r '.[].id' "$DBJSON" | LC_ALL=C sort -c 2>/dev/null; then
  pass "B2 DB: ids pass LC_ALL=C sort -c (mirrors script order check)"
else
  fail_msg "B2 DB: ids fail LC_ALL=C sort -c (rebuild order would diverge)"
fi
set +e
python3 - "$DBJSON" >"$WORKDIR/b2-reemit.json" 2>"$WORKDIR/b2.stderr" <<'PY'
import json, sys
KEYS = ["id","name","osi","fsf","deprecated","text"]
data = json.load(open(sys.argv[1]))
entries = [{k: e[k] for k in KEYS} for e in data]
entries.sort(key=lambda e: e["id"].encode("utf-8"))
sys.stdout.write(json.dumps(entries, indent=2, ensure_ascii=False) + "\n")
PY
B2_RC=$?
set -e
if [[ "$B2_RC" -eq 0 ]] && cmp -s "$WORKDIR/b2-reemit.json" "$DBJSON"; then
  pass "B2 DB: python re-emit byte-identical (deterministic rebuild equivalent)"
else
  fail_msg "B2 DB: python re-emit diverges (rebuild would not be byte-identical)"
fi
ACTUAL="$(sha256sum "$DBJSON" | awk '{print $1}')"
if grep -q "$ACTUAL" "$HASH"; then
  pass "B2 hash: recomputed sha256 matches $HASH_REL"
else
  fail_msg "B2 hash: recomputed $ACTUAL not pinned in $HASH_REL"
fi
if grep -q 'SPDX_VERSION=v3.29.0\|SPDX_VERSION="${SPDX_VERSION:-v3.29.0}"' "$SCRIPT" \
  && grep -q 'LC_ALL=C' "$SCRIPT" && grep -q 'jq -S' "$SCRIPT" \
  && grep -q 'sha256sum' "$SCRIPT" && grep -q '5242880' "$SCRIPT"; then
  pass "B2 script: pins v3.29.0 + LC_ALL=C + jq -S + sha256sum + 5242880 gate"
else
  fail_msg "B2 script: version/determinism/gate pins missing"
fi

# ---- B3: air-gap ----
echo "-- B3 air-gap --"
if air_gap_grep "$DBCS"; then
  pass "B3 loader: air-gap safe (SpdxLicenseDb zero HTTP)"
else
  fail_msg "B3 loader: HTTP surface in SpdxLicenseDb.cs (air-gap broken)"
fi
if grep -q 'EmbeddedResource Include="Data/spdx-licenses.json"' "$CSPROJ"; then
  pass "B3 csproj: EmbeddedResource Data/spdx-licenses.json pinned"
else
  fail_msg "B3 csproj: EmbeddedResource pin missing"
fi

# ---- B4: size gate ----
echo "-- B4 size gate --"
SIZE="$(wc -c < "$DBJSON")"
echo "db bytes: $SIZE (gate 5242880)"
if [[ "$SIZE" -lt 5242880 ]]; then
  pass "B4 size: $SIZE bytes < 5MB (D3 holds)"
else
  fail_msg "B4 size: $SIZE bytes >= 5MB (D3 tripped — ship seed variant + file follow-up issue)"
fi

# ---- B5: Normalize round-trip ----
echo "-- B5 Normalize round-trip --"
grep -q 'LicenseRef-\[A-Za-z0-9\]' "$MAPPER" \
  && pass "B5 mapper: LicenseRef passthrough regex pinned" \
  || fail_msg "B5 mapper: LicenseRef regex missing"
grep -q 'ToLowerInvariant()' "$MAPPER" \
  && pass "B5 mapper: case-insensitive normalize path present" \
  || fail_msg "B5 mapper: case-fold missing"
run_t "b5-db" "FullyQualifiedName~SpdxDbTests"
t_ok "b5-db" "B5 behavior: SpdxDbTests (lazy/case-insensitive/zero-HTTP/size)"
run_t "b5-script" "FullyQualifiedName~SpdxDbScriptTests"
t_ok "b5-script" "B5 behavior: SpdxDbScriptTests (script pins + hash match)"
run_t "b5-norm" "FullyQualifiedName~SpdxMapperNormalizeTests"
t_ok "b5-norm" "B5 behavior: SpdxMapperNormalizeTests (LicenseRef/or-later/bare pins)"

# ---- R1-scoped: chain-head wiring ----
echo "-- R1-scoped chain-head wiring --"
if grep -q 'SpdxLicenseDb.TryGetText' "$FETCHER" && grep -q 'SpdxLicenseTexts.TryGetText' "$FETCHER"; then
  set +e
  python3 - "$FETCHER" <<'PY' 2>/dev/null
import sys
src = open(sys.argv[1]).read()
a = src.index("SpdxLicenseDb.TryGetText")
b = src.index("SpdxLicenseTexts.TryGetText")
sys.exit(0 if a < b else 1)
PY
  CHAIN_RC=$?
  set -e
  if [[ "$CHAIN_RC" -eq 0 ]]; then
    pass "R1-scoped: fetcher DB-first chain (SpdxLicenseDb before SpdxLicenseTexts)"
  else
    fail_msg "R1-scoped: fetcher chain order wrong (DB must head the chain)"
  fi
else
  fail_msg "R1-scoped: fetcher DB/fallback anchors missing"
fi
grep -q '!offline' "$FETCHER" \
  && pass "R1-scoped: fetcher HTTP arms offline-guarded" \
  || fail_msg "R1-scoped: offline guard missing in fetcher"

echo "spdx-db-probe: $([[ "$fail" -eq 0 ]] && echo OK || echo "FAILURES ($fail)")"
exit "$fail"
