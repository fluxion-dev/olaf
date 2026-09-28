#!/usr/bin/env bash
# DEPRECATED (issue #123): --direct-only/--include-transitive flags deleted — no transitive-filter surface remains. Kept one issue cycle per lifecycle; delete next cycle if still unused.
# transitive-surface-probe.sh — transitive surface probe for issue #66.
# Promoted tool (issue #66; prototype + Wave-3 smoke = 2 uses).
# Per-parser direct-vs-transitive matrix + formatter direct-field check +
# CLI --direct-only/--include-transitive filter matrix.
# Matrix (each fixture scanned `dotnet run --project src/Olaf.Cli -- --input <dir> --format json`, non-strict):
#   P1 npm manifest-only (package.json)              -> direct=true
#   P2 npm lock-only (package-lock.json packages/)   -> direct=false
#   P3 pip Pipfile.lock (default section)            -> direct=true
#   P4 pip requirements.txt                          -> direct=true (manifest-direct)
#   P5 pip poetry.lock ([[package]])                 -> direct=false
#   P6 go.mod (require + // indirect, no go.sum)     -> direct / indirect split
#   P7 cargo Cargo.toml [dependencies]               -> direct=true
#   P8 cargo Cargo.lock ([[package]], root excluded) -> direct=false, root absent
#   P9 nuget csproj PackageReference                 -> direct=true
#   P10 nuget packages.lock.json (Direct/Transitive)-> split on type field
#   P11 composer.json (others heuristic)             -> carries direct field
#   F1-F4 formatter direct-field check (json/yaml/xml/html markers)
#   C1-C4 CLI filter matrix on P6 go fixture:
#     default-all (2) / --include-transitive (2, same) /
#     --direct-only (1, transitive absent) / both flags (exit 2)
# Offline-safe: never passes --strict; resolver failures degrade to Unknown, exit 0.
# Rules: repo-relative, idempotent (mktemp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail

# ---- Canonical root resolution (factory depth: ../../..) ----
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

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

Transitive surface probe (issue #66): per-parser direct-vs-transitive matrix
(npm lock-vs-manifest, pip lock-vs-requirements + poetry transitive, go // indirect,
cargo manifest-vs-lock, nuget manifest-vs-lock, composer heuristic) + formatter
direct-field check (json/yaml/xml/html) + CLI filter matrix (default-all,
--include-transitive same-as-default, --direct-only subset, both-flags conflict exit 2).

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
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/transitive-surface-probe-XXXXXX")"
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
# dep_field <json> <name> <field>: print <field> for package <name>
# (case-insensitive; unwraps {"dependency":{...}} envelope; empty if absent;
# booleans print as true/false; strings lowercased only for field=="direct").
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

# dep_direct <json> <name>: print "true"/"false"/"" for package <name>.
dep_direct() {
  dep_field "$1" "$2" "direct"
}

# dep_count <json>: print license count.
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

RC=0
run_scan() {
  # run_scan <tag> <inputdir> [extra args...]; stdout-><tag>.stdout.
  local tag="$1" input="$2"; shift 2
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$input" --format json "$@" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}

expect_direct() {
  # expect_direct <tag> <json> <name> <want(true|false|absent)>
  local tag="$1" json="$2" name="$3" want="$4"
  local got
  got="$(dep_direct "$json" "$name")"
  if [[ "$want" == "absent" ]]; then
    if [[ -z "$got" ]]; then pass "$tag: $name absent as expected";
    else fail_msg "$tag: $name present (direct=$got), want absent"; fi
  elif [[ "$got" == "$want" ]]; then
    pass "$tag: $name direct=$got"
  else
    fail_msg "$tag: $name direct='$got' (want $want)"
  fi
}

echo "== transitive-surface-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

FX="$WORKDIR/fx"
mkdir -p "$FX"

# ---- P1 npm manifest-only ----
mkdir -p "$FX/npm-manifest"
cat > "$FX/npm-manifest/package.json" <<'EOF'
{"name": "fx", "version": "1.0.0", "dependencies": {"npm-direct-a": "1.0.0"}}
EOF
run_scan "p1" "$FX/npm-manifest"
[[ "$RC" -eq 0 ]] && pass "P1 npm-manifest exit 0" || fail_msg "P1 npm-manifest exit $RC"
expect_direct "P1" "$WORKDIR/p1.stdout" "npm-direct-a" "true"

# ---- P2 npm lock-only ----
mkdir -p "$FX/npm-lock"
cat > "$FX/npm-lock/package-lock.json" <<'EOF'
{"name": "fx", "lockfileVersion": 3, "packages": {"": {"version": "1.0.0"}, "node_modules/npm-trans-b": {"version": "2.0.0"}}}
EOF
run_scan "p2" "$FX/npm-lock"
[[ "$RC" -eq 0 ]] && pass "P2 npm-lock exit 0" || fail_msg "P2 npm-lock exit $RC"
expect_direct "P2" "$WORKDIR/p2.stdout" "npm-trans-b" "false"

# ---- P3 pip Pipfile.lock ----
mkdir -p "$FX/pip-lock"
cat > "$FX/pip-lock/Pipfile.lock" <<'EOF'
{"_meta": {"hash": {"sha256": "00"}, "pipfile-spec": 6, "requires": {}, "sources": []},
 "default": {"pip-direct-a": {"version": "==1.0.0"}}, "develop": {}}
EOF
run_scan "p3" "$FX/pip-lock"
[[ "$RC" -eq 0 ]] && pass "P3 pip-lock exit 0" || fail_msg "P3 pip-lock exit $RC"
expect_direct "P3" "$WORKDIR/p3.stdout" "pip-direct-a" "true"

# ---- P4 pip requirements.txt ----
mkdir -p "$FX/pip-req"
printf 'pip-direct-b==2.0.0\n' > "$FX/pip-req/requirements.txt"
run_scan "p4" "$FX/pip-req"
[[ "$RC" -eq 0 ]] && pass "P4 pip-req exit 0" || fail_msg "P4 pip-req exit $RC"
expect_direct "P4" "$WORKDIR/p4.stdout" "pip-direct-b" "true"

# ---- P5 pip poetry.lock (transitive) ----
mkdir -p "$FX/pip-poetry"
cat > "$FX/pip-poetry/poetry.lock" <<'EOF'
[[package]]
name = "pip-trans-c"
version = "3.0.0"
EOF
run_scan "p5" "$FX/pip-poetry"
[[ "$RC" -eq 0 ]] && pass "P5 pip-poetry exit 0" || fail_msg "P5 pip-poetry exit $RC"
expect_direct "P5" "$WORKDIR/p5.stdout" "pip-trans-c" "false"

# ---- P6 go.mod direct + // indirect ----
mkdir -p "$FX/go"
cat > "$FX/go/go.mod" <<'EOF'
module example.com/fx

go 1.21

require example.com/direct-go-a v1.0.0

require example.com/indirect-go-b v2.0.0 // indirect
EOF
run_scan "p6" "$FX/go"
[[ "$RC" -eq 0 ]] && pass "P6 go exit 0" || fail_msg "P6 go exit $RC"
expect_direct "P6" "$WORKDIR/p6.stdout" "example.com/direct-go-a" "true"
expect_direct "P6" "$WORKDIR/p6.stdout" "example.com/indirect-go-b" "false"

# ---- P7 cargo manifest ----
mkdir -p "$FX/cargo-manifest"
cat > "$FX/cargo-manifest/Cargo.toml" <<'EOF'
[package]
name = "fx"
version = "0.1.0"

[dependencies]
cargo-direct-a = "1.0"
EOF
run_scan "p7" "$FX/cargo-manifest"
[[ "$RC" -eq 0 ]] && pass "P7 cargo-manifest exit 0" || fail_msg "P7 cargo-manifest exit $RC"
expect_direct "P7" "$WORKDIR/p7.stdout" "cargo-direct-a" "true"

# ---- P8 cargo lock (root excluded, rest transitive) ----
mkdir -p "$FX/cargo-lock"
cat > "$FX/cargo-lock/Cargo.toml" <<'EOF'
[package]
name = "myroot"
version = "0.1.0"

[dependencies]
cargo-trans-b = "2.0"
EOF
cat > "$FX/cargo-lock/Cargo.lock" <<'EOF'
[[package]]
name = "myroot"
version = "0.1.0"

[[package]]
name = "cargo-trans-b"
version = "2.0.0"
EOF
run_scan "p8" "$FX/cargo-lock"
[[ "$RC" -eq 0 ]] && pass "P8 cargo-lock exit 0" || fail_msg "P8 cargo-lock exit $RC"
expect_direct "P8" "$WORKDIR/p8.stdout" "cargo-trans-b" "false"
expect_direct "P8" "$WORKDIR/p8.stdout" "myroot" "absent"

# ---- P9 nuget manifest ----
mkdir -p "$FX/nuget-manifest"
cat > "$FX/nuget-manifest/fx.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="NuGetDirectA" Version="1.0.0" />
  </ItemGroup>
</Project>
EOF
run_scan "p9" "$FX/nuget-manifest"
[[ "$RC" -eq 0 ]] && pass "P9 nuget-manifest exit 0" || fail_msg "P9 nuget-manifest exit $RC"
expect_direct "P9" "$WORKDIR/p9.stdout" "NuGetDirectA" "true"

# ---- P10 nuget lock (Direct vs Transitive type) ----
mkdir -p "$FX/nuget-lock"
cat > "$FX/nuget-lock/packages.lock.json" <<'EOF'
{"dependencies": {".NETCoreApp,Version=v8.0": {
  "NuGetDirectB": {"resolved": "1.0.0", "type": "Direct"},
  "NuGetTransC": {"resolved": "2.0.0", "type": "Transitive"}}}}
EOF
run_scan "p10" "$FX/nuget-lock"
[[ "$RC" -eq 0 ]] && pass "P10 nuget-lock exit 0" || fail_msg "P10 nuget-lock exit $RC"
expect_direct "P10" "$WORKDIR/p10.stdout" "NuGetDirectB" "true"
expect_direct "P10" "$WORKDIR/p10.stdout" "NuGetTransC" "false"

# ---- P11 composer heuristic (manifest carries direct) ----
mkdir -p "$FX/composer"
cat > "$FX/composer/composer.json" <<'EOF'
{"name": "fx/fx", "require": {"vendor/composer-direct-a": "1.0.0"}}
EOF
run_scan "p11" "$FX/composer"
[[ "$RC" -eq 0 ]] && pass "P11 composer exit 0" || fail_msg "P11 composer exit $RC"
if grep -q '"direct"' "$WORKDIR/p11.stdout"; then
  pass "P11 composer JSON carries direct field"
else
  fail_msg "P11 composer JSON missing direct field"
fi

# ---- F1-F4 formatter direct-field check (on P1 npm-manifest dir) ----
echo "-- formatter direct-field check --"
for f in json yaml xml html; do
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/npm-manifest" --format "$f" >"$WORKDIR/fmt.$f" 2>"$WORKDIR/fmt.$f.err"
  frc=$?
  set -e
  if [[ "$frc" -ne 0 ]]; then
    fail_msg "F/$f scan exited $frc"
    continue
  fi
  case "$f" in
    json) pat='"direct"' ;;
    yaml) pat='direct:' ;;
    xml) pat='<direct>' ;;
    html) pat='<th>Direct</th>' ;;
  esac
  if grep -qF -- "$pat" "$WORKDIR/fmt.$f"; then
    pass "F/$f carries $pat"
  else
    fail_msg "F/$f missing $pat"
  fi
done

# ---- C1-C4 CLI filter matrix (on P6 go fixture: 1 direct + 1 indirect) ----
echo "-- CLI filter matrix --"
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format json >"$WORKDIR/c1.stdout" 2>"$WORKDIR/c1.stderr"
c1rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format json --include-transitive >"$WORKDIR/c2.stdout" 2>"$WORKDIR/c2.stderr"
c2rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format json --direct-only >"$WORKDIR/c3.stdout" 2>"$WORKDIR/c3.stderr"
c3rc=$?
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$FX/go" --format json --direct-only --include-transitive >"$WORKDIR/c4.stdout" 2>"$WORKDIR/c4.stderr"
c4rc=$?
set -e

C1="$(dep_count "$WORKDIR/c1.stdout")"
C2="$(dep_count "$WORKDIR/c2.stdout")"
C3="$(dep_count "$WORKDIR/c3.stdout")"
if [[ "$c1rc" -eq 0 && "$C1" == "2" ]]; then pass "C1 default-all: exit 0, count 2";
else fail_msg "C1 default-all: exit $c1rc count $C1 (want 0/2)"; fi
if [[ "$c2rc" -eq 0 && "$C2" == "2" ]]; then pass "C2 --include-transitive: exit 0, count 2 (same as default)";
else fail_msg "C2 --include-transitive: exit $c2rc count $C2 (want 0/2)"; fi
if [[ "$c3rc" -eq 0 && "$C3" == "1" ]]; then pass "C3 --direct-only: exit 0, count 1 (subset)";
else fail_msg "C3 --direct-only: exit $c3rc count $C3 (want 0/1)"; fi
expect_direct "C3" "$WORKDIR/c3.stdout" "example.com/direct-go-a" "true"
expect_direct "C3" "$WORKDIR/c3.stdout" "example.com/indirect-go-b" "absent"
if [[ "$c4rc" -eq 2 ]]; then pass "C4 both-flags conflict: exit 2";
else fail_msg "C4 both-flags conflict: exit $c4rc (want 2)"; fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "TRANSITIVE-SURFACE-PROBE OK: parser matrix + formatter + CLI filter all pass."
  exit 0
else
  echo "TRANSITIVE-SURFACE-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
