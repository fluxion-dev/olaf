#!/usr/bin/env bash
# disk-cache-probe.sh — Disk-cache probe for issue #78 (promoted v0.1.0, 2 uses
# ≥ bar: scratch prototype 1st use + promote-verify 2nd use).
# Persistent on-disk license-resolution cache (DiskLicenseCache + CacheKey).
# Offline-safe: every CLI run passes --offline (stubbed/seeds only, zero HTTP;
# zero-HTTP CallCount==0 is proven at unit level by CachedResolverTests R4).
# Arms: C1 round-trip, C2 TTL trio (30d/--cache-ttl-days/not-found 1d/
# never-cached), C3 corrupt warn-empty never-2, C4 atomic/concurrent,
# F1 flag+env matrix, F2 offline+disk, G1 size/time, D1 docs table (WARN-only:
# docs wave owns README), R1 matrix regression. Behavioral proof = filtered
# class runs (DiskLicenseCacheTests/CachedResolverTests/CacheCliTests).
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
warn_msg() { echo "WARN: $*"; }

# ---- Canonical temp-dir with KEEP_TEMP ----
WORKDIR=""
KEEP_TEMP=0
TIMEOUT_SECS=300
TESTPROJ_REL="tests/Olaf.Tests/Olaf.Tests.csproj"
CLI_REL="src/Olaf.Cli/Olaf.Cli.csproj"
DISK_REL="src/Olaf.Resolvers/DiskLicenseCache.cs"
KEY_REL="src/Olaf.Resolvers/CacheKey.cs"
RESOLVER_REL="src/Olaf.Resolvers/CachingLicenseResolver.cs"
PROGRAM_REL="src/Olaf.Cli/Program.cs"
SEED_PKG="olaf-seeded-pkg-78"
SEED_VER="1.2.3"
SEED_KEY="npm:olaf-seeded-pkg-78@1.2.3"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

Disk-cache probe (issue #78): round-trip (C1), TTL trio (C2), corrupt
tolerance (C3), atomic/concurrent writes (C4), flag+env matrix (F1),
offline+disk composition (F2), size/time bounds (G1), docs table (D1,
WARN-only), format-matrix regression (R1). Offline-safe: CLI runs always
pass --offline; seeds are synth; zero-HTTP proven by filtered unit runs.

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
CLIPROJ="$ROOT/$CLI_REL"
DISK="$ROOT/$DISK_REL"
CACHEKEY="$ROOT/$KEY_REL"
RESOLVER="$ROOT/$RESOLVER_REL"
PROGRAM="$ROOT/$PROGRAM_REL"
READMECS="$ROOT/README.md"
[[ -f "$TESTPROJ" ]] || { echo "Test project not found: $TESTPROJ" >&2; exit 2; }
[[ -f "$CLIPROJ" ]] || { echo "CLI project not found: $CLIPROJ" >&2; exit 2; }
[[ -f "$DISK" ]] || { echo "DiskLicenseCache not found: $DISK" >&2; exit 2; }
[[ -f "$CACHEKEY" ]] || { echo "CacheKey not found: $CACHEKEY" >&2; exit 2; }
[[ -f "$RESOLVER" ]] || { echo "Resolver not found: $RESOLVER" >&2; exit 2; }
[[ -f "$PROGRAM" ]] || { echo "Program not found: $PROGRAM" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/disk-cache-probe-XXXXXX")"
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

# ---- Canonical mkphantom fixture (from _template.sh 0.2.7; guard-free: probe parses JSON via python asserts) ----
PHANTOM_PKG="this-package-definitely-does-not-exist-olaf-xyz"
PHANTOM_VER="9.9.9"
mkphantom() {
  local d="$1"
  mkdir -p "$d"
  printf '{\n  "name": "olaf-phantom-fixture",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$PHANTOM_PKG" "$PHANTOM_VER" > "$d/package.json"
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

run_cli() {
  # run_cli <tag> [args...]; dotnet run --no-build -> <tag>.stdout/.stderr, RC.
  # Callers pass --offline explicitly (offline-safe: zero HTTP by construction).
  local tag="$1"; shift
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$CLIPROJ" --no-build --no-launch-profile -- "$@" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}

seed_cache() {
  # seed_cache <dir> [spdx] [status]; writes lowercase on-disk schema w/ fresh fetchedAt.
  local d="$1" spdx="${2:-MIT}" status="${3:-Resolved}"
  SEED_D="$d" SEED_SPDX="$spdx" SEED_STATUS="$status" SEED_KEY="$SEED_KEY" python3 - <<'PY' 2>/dev/null
import json, os, datetime
d, spdx, status, key = (os.environ["SEED_D"], os.environ["SEED_SPDX"],
                        os.environ["SEED_STATUS"], os.environ["SEED_KEY"])
stamp = datetime.datetime.now(datetime.timezone.utc).isoformat()
doc = {"entries": {key: {"spdx": spdx, "licenseText": None, "sourceUrl": None,
                         "status": status, "reason": "registry:npm",
                         "fetchedAt": stamp, "etag": None}}}
open(os.path.join(d, "cache.json"), "w").write(json.dumps(doc))
PY
}

mkseed_fixture() {
  # mkseed_fixture <dir>; synth npm fixture carrying the seeded dep.
  local d="$1"
  mkdir -p "$d"
  printf '{\n  "name": "olaf-cache-fixture-78",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$SEED_PKG" "$SEED_VER" > "$d/package.json"
}

json_summary() {
  # json_summary <stdout-file>; prints "total resolved unknown spdx".
  python3 - "$1" "$SEED_PKG" <<'PY' 2>/dev/null
import json, sys
doc = json.load(open(sys.argv[1]))
s = doc["summary"]
spdx = ""
for it in doc.get("licenses", []):
    if it.get("name") == sys.argv[2]:
        spdx = it.get("spdx") or ""
print(s["total"], s["resolved"], s["unknown"], spdx)
PY
}

# ---- Canonical R1 format-matrix regression (from _template.sh 0.2.3; behavior identical) ----
# Requires run_scan <tag> <inputdir> <format> [extra...] + FIXTURE + WORKDIR.
run_scan() {
  # run_scan <tag> <inputdir> <format> [extra args...]; stdout-><tag>.stdout
  local tag="$1" input="$2" fmt="$3"; shift 3
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$CLIPROJ" --no-build --no-launch-profile -- --input "$input" --format "$fmt" "$@" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}
r1_matrix_regression() {
  # r1_matrix_regression [formats...]; default: json yaml xml html.
  local formats=("$@")
  (( ${#formats[@]} )) || formats=(json yaml xml html)
  local R1FAIL=0 f pat
  echo "-- R1 format-matrix regression --"
  for f in "${formats[@]}"; do
    run_scan "r1-$f" "$FIXTURE" "$f" --offline
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

echo "== disk-cache-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$CLIPROJ" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

echo "-- B-suite: filtered class runs (behavioral proof, stubbed only) --"
run_t "b-disk" "FullyQualifiedName~DiskLicenseCacheTests"
t_ok "b-disk" "B-suite: DiskLicenseCacheTests U1-U5 (round-trip/TTL/corrupt/precedence/paths)"
run_t "b-cached" "FullyQualifiedName~CachedResolverTests"
t_ok "b-cached" "B-suite: CachedResolverTests R1-R4 (stored/1d/never-cached/offline-zero-HTTP)"
run_t "b-cli" "FullyQualifiedName~CacheCliTests"
t_ok "b-cli" "B-suite: CacheCliTests C1-C3 (reuse/bypass/refresh)"

echo "-- C1 round-trip (seeded offline second run, byte-identical MIT) --"
C1FIX="$WORKDIR/c1-fix"; mkseed_fixture "$C1FIX"
C1CACHE="$WORKDIR/c1-cache"; mkdir -p "$C1CACHE"; seed_cache "$C1CACHE"
C1_START="$(date +%s)"
run_cli "c1-first" --input "$C1FIX" --format json --offline --cache-dir "$C1CACHE"
C1_RC1=$RC
run_cli "c1-second" --input "$C1FIX" --format json --offline --cache-dir "$C1CACHE"
C1_RC2=$RC
C1_END="$(date +%s)"
C1_ELAPSED=$((C1_END - C1_START))
[[ "$C1_RC1" -eq 0 ]] && [[ "$C1_RC2" -eq 0 ]] \
  && pass "C1 runs: first+second exit 0 (offline, seeded)" \
  || fail_msg "C1 runs: exits $C1_RC1/$C1_RC2 (want 0/0)"
if cmp -s "$WORKDIR/c1-first.stdout" "$WORKDIR/c1-second.stdout"; then
  pass "C1 round-trip: second run byte-identical (disk hit, no re-resolve)"
else
  fail_msg "C1 round-trip: second run diverges (disk hit broken)"
fi
C1_SUM="$(json_summary "$WORKDIR/c1-first.stdout")"
echo "c1 summary: $C1_SUM (want '1 1 0 MIT')"
[[ "$C1_SUM" == "1 1 0 MIT" ]] \
  && pass "C1 summary: (1,1,0) + MIT served from disk" \
  || fail_msg "C1 summary: got '$C1_SUM' (want '1 1 0 MIT')"
run_cli "c1-generate" generate "$C1FIX" --format json --offline --cache-dir "$C1CACHE"
if [[ "$RC" -eq 0 ]] && cmp -s "$WORKDIR/c1-first.stdout" "$WORKDIR/c1-generate.stdout"; then
  pass "C1 generate parity: exit 0 + stdout identical to root"
else
  fail_msg "C1 generate parity: rc=$RC or stdout diverges"
fi

echo "-- C2 TTL trio (source pins + live ttl arms) --"
set +e
python3 - "$DISK" "$RESOLVER" "$PROGRAM" <<'PY' 2>/dev/null
import sys
disk, resolver, program = (open(p).read() for p in sys.argv[1:4])
fails = []
def need(src, needle, label):
    if needle not in src:
        fails.append(label)
# 30d default: resolver ctor default + Program default.
need(resolver, "TimeSpan.FromDays(30)", "C2: resolver default 30d missing")
need(program, "resolvedTtlDays = 30", "C2: Program default 30d missing")
# not-found 1d.
need(disk, "NotFoundTtl = TimeSpan.FromDays(1)", "C2: NotFoundTtl 1d missing")
# ttl selection scales ONLY Resolved.
need(disk, "? resolvedTtl", "C2: Resolved-only ttl selection missing")
need(disk, ": NotFoundTtl", "C2: not-found ttl arm missing")
# boundary: exactly-at-TTL counts as expired (>=).
need(disk, ">= ttl", "C2: >= boundary missing")
# never-cached reason prefixes.
for p in ['"timeout:"', '"transport-error:"', '"offline-cache-miss"', '"resolver-error:"']:
    need(disk, p, f"C2: never-cached prefix {p} missing")
# only Resolved/Unknown statuses cacheable.
need(disk, '"Resolved"', "C2: Resolved cacheable gate missing")
need(disk, '"Unknown"', "C2: Unknown cacheable gate missing")
# invalid ttl -> exit 2.
need(program, "cache-ttl-days", "C2: --cache-ttl-days wiring missing")
if fails:
    print("\n".join(fails))
    sys.exit(1)
print("C2 source pins ok (30d default / 1d not-found / Resolved-only scale / >= edge / 4 never-cached prefixes / exit-2 wiring)")
PY
C2_RC=$?
set -e
if [[ "$C2_RC" -eq 0 ]]; then pass "C2 source: TTL trio pins hold"; else fail_msg "C2 source: TTL pins wrong (see lines above)"; fi
run_cli "c2-ttl7" --input "$C1FIX" --format json --offline --cache-dir "$C1CACHE" --cache-ttl-days 7
if [[ "$RC" -eq 0 ]] && [[ "$(json_summary "$WORKDIR/c2-ttl7.stdout")" == "1 1 0 MIT" ]]; then
  pass "C2 live: --cache-ttl-days 7 accepted, hit preserved (1,1,0 MIT)"
else
  fail_msg "C2 live: --cache-ttl-days 7 rc=$RC summary='$(json_summary "$WORKDIR/c2-ttl7.stdout")'"
fi
run_cli "c2-badttl" --input "$C1FIX" --format json --offline --cache-dir "$C1CACHE" --cache-ttl-days bogus
[[ "$RC" -eq 2 ]] \
  && pass "C2 live: invalid --cache-ttl-days exits 2 (usage/config)" \
  || fail_msg "C2 live: invalid ttl exits $RC (want 2)"
grep -q "cache-ttl-days" "$WORKDIR/c2-badttl.stderr" \
  && pass "C2 live: invalid ttl names the flag on stderr" \
  || fail_msg "C2 live: invalid ttl stderr omits flag name"

echo "-- C3 corrupt tolerance (warn + empty, never exit 2) --"
C3FIX="$WORKDIR/c3-fix"; mkseed_fixture "$C3FIX"
C3CACHE="$WORKDIR/c3-cache"; mkdir -p "$C3CACHE"
printf 'this is not json {{{' > "$C3CACHE/cache.json"
run_cli "c3-corrupt" --input "$C3FIX" --format json --offline --cache-dir "$C3CACHE"
[[ "$RC" -eq 0 ]] \
  && pass "C3 live: corrupt cache exits 0 (never 2)" \
  || fail_msg "C3 live: corrupt cache exits $RC (want 0)"
grep -qi "warning.*corrupt.*license cache" "$WORKDIR/c3-corrupt.stderr" \
  && pass "C3 live: corrupt warns on stderr" \
  || fail_msg "C3 live: corrupt stderr missing warn"
C3_SUM="$(json_summary "$WORKDIR/c3-corrupt.stdout")"
[[ "$C3_SUM" == "1 0 1 " ]] \
  && pass "C3 live: corrupt starts empty (1,0,1 Unknown)" \
  || fail_msg "C3 live: corrupt summary '$C3_SUM' (want '1 0 1 ')"
set +e
python3 - "$DISK" <<'PY' 2>/dev/null
import sys
src = open(sys.argv[1]).read()
fails = []
if "starting empty" not in src:
    fails.append("C3: corrupt->empty-start pin missing")
if "entry-miss" not in src and "Malformed fetchedAt" not in src:
    fails.append("C3: malformed-fetchedAt entry-miss pin missing")
if "tolerate" not in src and "partial" not in src:
    fails.append("C3: partial-read tolerance pin missing")
if fails:
    print("\n".join(fails)); sys.exit(1)
print("C3 source pins ok (empty-start / entry-miss / partial tolerance)")
PY
C3_SRC_RC=$?
set -e
if [[ "$C3_SRC_RC" -eq 0 ]]; then pass "C3 source: resilience pins hold"; else fail_msg "C3 source: resilience pins wrong"; fi

echo "-- C4 atomic/concurrent writes --"
if grep -q 'catch (FileNotFoundException)\|catch (DirectoryNotFoundException)' "$DISK"; then
  fail_msg "C4 CS0160: File/DirectoryNotFound catch after IOException (covered by inheritance)"
else
  pass "C4 CS0160: no File/DirectoryNotFound catch (IOException covers)"
fi
set +e
python3 - "$DISK" <<'PY' 2>/dev/null
import sys
src = open(sys.argv[1]).read()
fails = []
for needle, label in [("SemaphoreSlim", "C4: SemaphoreSlim write gate"),
                      ('.tmp"', "C4: tmp-file arm"),
                      ("File.Move", "C4: rename arm"),
                      ("overwrite: true", "C4: atomic overwrite arm"),
                      ("allowlist:", "C4: catch allowlist comments")]:
    if needle not in src:
        fails.append(f"{label} missing ({needle})")
if fails:
    print("\n".join(fails)); sys.exit(1)
print("C4 source pins ok (gate + tmp+rename + allowlist)")
PY
C4_RC=$?
set -e
if [[ "$C4_RC" -eq 0 ]]; then pass "C4 source: atomic-write pins hold"; else fail_msg "C4 source: atomic-write pins wrong"; fi

echo "-- F1 flag+env matrix --"
for flag in --cache-dir --no-cache --refresh-cache --cache-ttl-days; do
  run_cli "f1-help-root" --help
  if grep -qF -- "$flag" "$WORKDIR/f1-help-root.stdout"; then
    pass "F1 root --help mentions $flag"
  else
    fail_msg "F1 root --help omits $flag"
  fi
done
for flag in --cache-dir --no-cache --refresh-cache --cache-ttl-days; do
  run_cli "f1-help-gen" generate --help
  if grep -qF -- "$flag" "$WORKDIR/f1-help-gen.stdout"; then
    pass "F1 generate --help mentions $flag"
  else
    fail_msg "F1 generate --help omits $flag"
  fi
done
set +e
python3 - "$DISK" "$PROGRAM" <<'PY' 2>/dev/null
import sys
disk, program = open(sys.argv[1]).read(), open(sys.argv[2]).read()
fails = []
# precedence: flag dir -> OLAF_CACHE_DIR -> XDG -> OS fallback (index order
# scoped to the ResolveFilePath body — the B3 header comment above the method
# mentions OLAF_CACHE_DIR first, so a whole-file index would mis-order).
try:
    body = disk.split("ResolveFilePath", 1)[1].split("GetEnvDir(string name)", 1)[0]
    a = body.index("flagDir")
    b = body.index("OLAF_CACHE_DIR")
    c = body.index("GetXdgDir")
    d = body.index("GetDefaultDirectory")
    if not (a < b < c < d):
        fails.append("F1: precedence order wrong (want flag > OLAF_CACHE_DIR > XDG > OS fallback)")
except (ValueError, IndexError) as e:
    fails.append(f"F1: precedence anchor missing ({e})")
# refresh wins over --no-cache.
if "noCache && !refreshCache" not in program:
    fails.append("F1: refresh-wins predicate missing (noCache && !refreshCache)")
# XDG wins on ALL OSes (pinned decision, not Linux-only).
if "when set and non-empty, wins on ALL OSes" not in disk and "wins on ALL OSes" not in disk:
    fails.append("F1: XDG-all-OSes decision comment missing")
if fails:
    print("\n".join(fails)); sys.exit(1)
print("F1 source pins ok (precedence order / refresh-wins / XDG-all-OSes)")
PY
F1_RC=$?
set -e
if [[ "$F1_RC" -eq 0 ]]; then pass "F1 source: precedence pins hold"; else fail_msg "F1 source: precedence pins wrong"; fi
F1CACHE="$WORKDIR/f1-cache"; mkdir -p "$F1CACHE"; seed_cache "$F1CACHE"
F1_BEFORE="$(sha256sum "$F1CACHE/cache.json" | awk '{print $1}')"
run_cli "f1-nocache" --input "$C1FIX" --format json --offline --cache-dir "$F1CACHE" --no-cache --strict
[[ "$RC" -eq 1 ]] \
  && pass "F1 live: --no-cache bypass trips --strict to exit 1 (seed ignored)" \
  || fail_msg "F1 live: --no-cache exits $RC (want 1)"
[[ "$(json_summary "$WORKDIR/f1-nocache.stdout")" == "1 0 1 " ]] \
  && pass "F1 live: --no-cache serves Unknown (1,0,1)" \
  || fail_msg "F1 live: --no-cache summary '$(json_summary "$WORKDIR/f1-nocache.stdout")' (want '1 0 1 ')"
F1_AFTER="$(sha256sum "$F1CACHE/cache.json" | awk '{print $1}')"
[[ "$F1_BEFORE" == "$F1_AFTER" ]] \
  && pass "F1 live: --no-cache leaves cache bytes untouched (no writes)" \
  || fail_msg "F1 live: --no-cache rewrote cache.json"
run_cli "f1-refresh" --input "$C1FIX" --format json --offline --cache-dir "$F1CACHE" --refresh-cache
if [[ "$RC" -eq 0 ]] && [[ "$(json_summary "$WORKDIR/f1-refresh.stdout")" == "1 0 1 " ]]; then
  pass "F1 live: --refresh-cache skips reads (1,0,1 Unknown, exit 0)"
else
  fail_msg "F1 live: --refresh-cache rc=$RC summary='$(json_summary "$WORKDIR/f1-refresh.stdout")'"
fi
run_cli "f1-generate-nocache" generate "$C1FIX" --format json --offline --cache-dir "$F1CACHE" --no-cache --strict
[[ "$RC" -eq 1 ]] \
  && pass "F1 live: generate inherits --no-cache (exit 1)" \
  || fail_msg "F1 live: generate --no-cache exits $RC (want 1)"
F1ENV="$WORKDIR/f1-env"; mkdir -p "$F1ENV"; seed_cache "$F1ENV"
set +e
OLAF_CACHE_DIR="$F1ENV" timeout "${TIMEOUT_SECS}s" dotnet run --project "$CLIPROJ" --no-build --no-launch-profile -- --input "$C1FIX" --format json --offline >"$WORKDIR/f1-env.stdout" 2>"$WORKDIR/f1-env.stderr"
F1_ENV_RC=$?
set -e
if [[ "$F1_ENV_RC" -eq 0 ]] && [[ "$(json_summary "$WORKDIR/f1-env.stdout")" == "1 1 0 MIT" ]]; then
  pass "F1 live: OLAF_CACHE_DIR serves seeded MIT without --cache-dir"
else
  fail_msg "F1 live: OLAF_CACHE_DIR rc=$F1_ENV_RC summary='$(json_summary "$WORKDIR/f1-env.stdout")'"
fi
F1EMPTY="$WORKDIR/f1-empty"; mkdir -p "$F1EMPTY"
set +e
OLAF_CACHE_DIR="$F1EMPTY" timeout "${TIMEOUT_SECS}s" dotnet run --project "$CLIPROJ" --no-build --no-launch-profile -- --input "$C1FIX" --format json --offline --cache-dir "$F1ENV" >"$WORKDIR/f1-flagwins.stdout" 2>"$WORKDIR/f1-flagwins.stderr"
F1_FLAG_RC=$?
set -e
if [[ "$F1_FLAG_RC" -eq 0 ]] && [[ "$(json_summary "$WORKDIR/f1-flagwins.stdout")" == "1 1 0 MIT" ]]; then
  pass "F1 live: --cache-dir wins over OLAF_CACHE_DIR (flag precedence)"
else
  fail_msg "F1 live: flag-wins rc=$F1_FLAG_RC summary='$(json_summary "$WORKDIR/f1-flagwins.stdout")'"
fi

echo "-- F2 offline+disk composition --"
set +e
python3 - "$PROGRAM" <<'PY' 2>/dev/null
import sys
src = open(sys.argv[1]).read()
anchor = "ResolveWithFallbackAsync"
try:
    body = src.split(anchor, 1)[1]
except (IndexError, ValueError):
    print("anchor split failed"); sys.exit(1)
a = body.index("!offline")
b = body.index("fallbackResolver.ResolveAsync")
if a > b:
    print("offline guard after fallback call"); sys.exit(1)
print("F2 callsite ok (offline guard precedes ClearlyDefined fallback)")
PY
F2_RC=$?
set -e
if [[ "$F2_RC" -eq 0 ]]; then pass "F2 callsite: offline skips ClearlyDefined fallback"; else fail_msg "F2 callsite: offline/fallback order wrong"; fi
if air_gap_grep "$DISK" && air_gap_grep "$CACHEKEY"; then
  pass "F2 air-gap: DiskLicenseCache + CacheKey zero HTTP"
else
  fail_msg "F2 air-gap: HTTP surface in cache layer"
fi

echo "-- G1 size/time bounds --"
grep -q 'MaxLicenseTextChars = 1024 \* 1024' "$DISK" \
  && pass "G1 size: 1MiB licenseText persist cap pinned (per #71)" \
  || fail_msg "G1 size: 1MiB cap pin missing"
CACHE_BYTES="$(wc -c < "$C1CACHE/cache.json")"
echo "seeded cache.json bytes: $CACHE_BYTES"
[[ "$CACHE_BYTES" -lt 5242880 ]] \
  && pass "G1 size: seeded cache.json $CACHE_BYTES bytes < 5MB" \
  || fail_msg "G1 size: seeded cache.json $CACHE_BYTES bytes >= 5MB"
echo "C1 two-run elapsed: ${C1_ELAPSED}s"
if [[ "$C1_ELAPSED" -le 120 ]]; then
  pass "G1 time: two offline cached runs in ${C1_ELAPSED}s (<= 120s)"
else
  warn_msg "G1 time: two runs took ${C1_ELAPSED}s (> 120s, advisory only)"
fi

echo "-- D1 docs table (WARN-only: docs wave owns README) --"
[[ -f "$READMECS" ]] || warn_msg "D1: README.md absent"
if [[ -f "$READMECS" ]]; then
  for needle in "OLAF_CACHE_DIR" "--cache-dir" "--no-cache" "--refresh-cache" "--cache-ttl-days"; do
    grep -qF -- "$needle" "$READMECS" \
      && pass "D1 docs: README mentions $needle" \
      || warn_msg "D1: README omits $needle (docs wave owns)"
  done
  grep -q "\.cache/olaf" "$READMECS" \
    && pass "D1 docs: README carries OS cache-location table" \
    || warn_msg "D1: README omits OS location table (docs wave owns)"
  grep -q "30" "$READMECS" \
    && pass "D1 docs: README mentions 30-day TTL" \
    || warn_msg "D1: README omits TTL rule (docs wave owns)"
  grep -qi "corrupt" "$READMECS" \
    && pass "D1 docs: README mentions corrupt-cache behavior" \
    || warn_msg "D1: README omits corrupt behavior (docs wave owns)"
fi

echo "-- R1 uses canonical r1_matrix_regression (offline phantom) --"
FIXTURE="$WORKDIR/r1-fix"; mkphantom "$FIXTURE"
r1_matrix_regression

echo "disk-cache-probe: $([[ "$fail" -eq 0 ]] && echo OK || echo "FAILURES ($fail)")"
exit "$fail"
