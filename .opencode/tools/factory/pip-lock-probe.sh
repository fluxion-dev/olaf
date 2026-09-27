#!/usr/bin/env bash
# pip-lock-probe.sh — Pipfile.lock probe for issue #6 (pip lock support).
# Builds a synthetic canonical Pipfile.lock and asserts:
#   (a) canonical lock (default requests ==2.31.0 w/ extras+markers+hashes,
#       shared-pkg collision default 1.0.0 vs develop 2.0.0, 1 git + 1 path +
#       1 file + 1 editable, develop pytest ==7.4.0) scanned via
#       `dotnet run --project src/Olaf.Cli -- --input <dir> --format json`
#       -> exit 0, stripped versions, VCS/local -> *, count 7, default-wins
#   (b) malformed JSON lock -> exit 0, count 0
#   (c) dir-preference: Pipfile + poetry.lock + uv.lock + requirements.txt
#       co-present with Pipfile.lock -> lock-only 7, rival-only pkgs absent
#   (d) IsTransitive=false static check on ParsePipfileLock
#       (CLI JSON carries no transitive field)
# Rules: repo-relative, idempotent (temp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail

TIMEOUT_SECS=60
PROJECT_REL="src/Olaf.Cli"
PARSER_REL="src/Olaf.Parsers/PipParser.cs"
REPO_ROOT=""
WORKDIR=""
KEEP_TEMP=0

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Pipfile.lock probe (issue #6): canonical lock + malformed + dir-preference + transitive check.

Checks:
  (a) canonical Pipfile.lock scan -> exit 0, count 7, versions stripped
      (requests 2.31.0, pytest 7.4.0, shared-pkg default-wins 1.0.0,
      git/path/file/editable -> *), extras/markers/hashes tolerated
  (b) malformed JSON lock        -> exit 0, count 0
  (c) lock + Pipfile/poetry/uv/requirements co-present -> lock-only 7,
      rival-only packages absent
  (d) ParsePipfileLock constructs Dependency(.., IsTransitive: false)
      and CLI JSON carries no transitive field

Options:
  --repo-root <dir>   Repo root (default: git top-level or CWD)
  --workdir <dir>     Work dir (default: mktemp -d)
  --timeout <secs>    Per-run timeout in seconds (default: 60)
  --keep-temp         Keep temp work dir for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 30 --keep-temp

Exit codes: 0 all PASS, 1 assertion failure, 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

if [[ -z "$REPO_ROOT" ]]; then
  if git rev-parse --show-toplevel >/dev/null 2>&1; then
    REPO_ROOT="$(git rev-parse --show-toplevel)"
  else
    REPO_ROOT="$(pwd)"
  fi
fi
PROJECT="$REPO_ROOT/$PROJECT_REL"
PARSER="$REPO_ROOT/$PARSER_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -f "$PARSER" ]] || { echo "Parser source not found: $PARSER" >&2; exit 2; }
[[ -f "$REPO_ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $REPO_ROOT" >&2; exit 2; }

for cmd in dotnet timeout python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d -t olaf-pip-lock-probe-XXXXXX)"
  MADE_TEMP=1
else
  MADE_TEMP=0
  mkdir -p "$WORKDIR"
fi
cleanup() {
  if [[ "$KEEP_TEMP" -eq 1 ]]; then
    echo "keeping work dir: $WORKDIR"
  elif [[ "$MADE_TEMP" -eq 1 ]]; then
    rm -rf "$WORKDIR"
  fi
}
trap cleanup EXIT

FAIL=0
pass() { echo "PASS [$1]: $2"; }
fail() { echo "FAIL [$1]: $2"; FAIL=1; }

# dep_ver <json> <name>: print version for package <name> (case-insensitive), empty if absent.
dep_ver() {
  python3 - "$1" "$2" <<'PY' 2>/dev/null
import json, sys
path, want = sys.argv[1], sys.argv[2].lower()
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
            print(str(dep.get("version", it.get("version", ""))))
            break
PY
}

# dep_count <json>: print license/dependency count.
dep_count() {
  python3 - "$1" <<'PY' 2>/dev/null
import json, sys
try:
    with open(path := sys.argv[1]) as f:
        data = json.load(f)
except Exception:
    print("?")
    sys.exit(0)
items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
print(len(items) if isinstance(items, list) else "?")
PY
}

run_scan() {
  # run_scan <tag> <inputdir>; stdout-><tag>.stdout, stderr-><tag>.stderr, sets RC.
  local tag="$1" input="$2"
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" -- --input "$input" --format json >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail "$tag" "timed out after ${TIMEOUT_SECS}s"
  fi
}

echo "== pip-lock-probe v$VERSION =="
echo "repo: $REPO_ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build" "dotnet build OK"

echo "-- (a) canonical Pipfile.lock -> exit 0, count 7, stripped, default-wins --"
LOCKDIR="$WORKDIR/canonical"
mkdir -p "$LOCKDIR"
cat > "$LOCKDIR/Pipfile.lock" <<'EOF'
{
    "_meta": {
        "hash": {"sha256": "0000000000000000000000000000000000000000000000000000000000000000"},
        "pipfile-spec": 6,
        "requires": {"python_version": "3.11"},
        "sources": [{"name": "pypi", "url": "https://pypi.org/simple", "verify_ssl": true}]
    },
    "default": {
        "requests": {
            "extras": ["security", "socks"],
            "markers": "python_version >= '3.8'",
            "version": "==2.31.0",
            "hashes": ["sha256:aaa", "sha256:bbb"]
        },
        "shared-pkg": {"version": "==1.0.0", "hashes": ["sha256:ccc"]},
        "git-pkg": {"git": "https://github.com/example/git-pkg.git", "ref": "abc123"},
        "path-pkg": {"path": "./vendor/path-pkg"},
        "file-pkg": {"file": "https://example.com/file-pkg-1.0.tar.gz"},
        "editable-pkg": {"editable": true, "path": "."}
    },
    "develop": {
        "pytest": {"version": "==7.4.0", "hashes": ["sha256:ddd"]},
        "shared-pkg": {"version": "==2.0.0", "hashes": ["sha256:eee"]}
    }
}
EOF

run_scan "canonical" "$LOCKDIR"
echo "canonical exit: $RC"
if [[ "$RC" -eq 0 ]]; then pass "a/exit" "exit 0"; else fail "a/exit" "exit $RC (want 0; stderr: $(tail -c 200 "$WORKDIR/canonical.stderr" | tr '\n' ' '))"; fi
COUNT="$(dep_count "$WORKDIR/canonical.stdout")"
if [[ "$COUNT" == "7" ]]; then pass "a/count" "count 7"; else fail "a/count" "count $COUNT (want 7)"; fi
V="$(dep_ver "$WORKDIR/canonical.stdout" "requests")"
if [[ "$V" == "2.31.0" ]]; then pass "a/requests" "requests==2.31.0 stripped (extras/markers/hashes tolerated)"; else fail "a/requests" "requests version '$V' (want 2.31.0)"; fi
V="$(dep_ver "$WORKDIR/canonical.stdout" "pytest")"
if [[ "$V" == "7.4.0" ]]; then pass "a/pytest" "develop pytest==7.4.0"; else fail "a/pytest" "pytest version '$V' (want 7.4.0)"; fi
V="$(dep_ver "$WORKDIR/canonical.stdout" "shared-pkg")"
if [[ "$V" == "1.0.0" ]]; then pass "a/default-wins" "shared-pkg==1.0.0 default wins over develop 2.0.0"; else fail "a/default-wins" "shared-pkg version '$V' (want 1.0.0)"; fi
for pkg in git-pkg path-pkg file-pkg editable-pkg; do
  V="$(dep_ver "$WORKDIR/canonical.stdout" "$pkg")"
  if [[ "$V" == "*" ]]; then pass "a/$pkg" "$pkg -> * (VCS/local ref)"; else fail "a/$pkg" "$pkg version '$V' (want *)"; fi
done

echo "-- (b) malformed JSON lock -> exit 0, count 0 --"
BADDIR="$WORKDIR/malformed"
mkdir -p "$BADDIR"
echo "{ this is not valid json !!!" > "$BADDIR/Pipfile.lock"
run_scan "malformed" "$BADDIR"
echo "malformed exit: $RC"
if [[ "$RC" -eq 0 ]]; then pass "b/exit" "exit 0"; else fail "b/exit" "exit $RC (want 0; stderr: $(tail -c 200 "$WORKDIR/malformed.stderr" | tr '\n' ' '))"; fi
COUNT="$(dep_count "$WORKDIR/malformed.stdout")"
if [[ "$COUNT" == "0" ]]; then pass "b/count" "count 0"; else fail "b/count" "count $COUNT (want 0)"; fi

echo "-- (c) dir-preference: lock + Pipfile/poetry/uv/requirements -> lock-only 7 --"
CODIR="$WORKDIR/copresent"
mkdir -p "$CODIR"
cp "$LOCKDIR/Pipfile.lock" "$CODIR/Pipfile.lock"
cat > "$CODIR/Pipfile" <<'EOF'
[[source]]
name = "pypi"
url = "https://pypi.org/simple"
verify_ssl = true

[packages]
requests = "*"

[dev-packages]
pytest = "*"
EOF
cat > "$CODIR/poetry.lock" <<'EOF'
[[package]]
name = "poetry-only-pkg"
version = "9.9.9"

[[package]]
name = "requests"
version = "9.9.9"
EOF
cat > "$CODIR/uv.lock" <<'EOF'
[[package]]
name = "uv-only-pkg"
version = "9.9.9"
EOF
printf 'req-only-pkg==9.9.9\n' > "$CODIR/requirements.txt"
run_scan "copresent" "$CODIR"
echo "copresent exit: $RC"
if [[ "$RC" -eq 0 ]]; then pass "c/exit" "exit 0"; else fail "c/exit" "exit $RC (want 0; stderr: $(tail -c 200 "$WORKDIR/copresent.stderr" | tr '\n' ' '))"; fi
COUNT="$(dep_count "$WORKDIR/copresent.stdout")"
if [[ "$COUNT" == "7" ]]; then pass "c/count" "count 7 (lock-only, rivals not double-counted)"; else fail "c/count" "count $COUNT (want 7)"; fi
for pkg in poetry-only-pkg uv-only-pkg req-only-pkg; do
  V="$(dep_ver "$WORKDIR/copresent.stdout" "$pkg")"
  if [[ -z "$V" ]]; then pass "c/$pkg" "$pkg absent (rival manifest ignored)"; else fail "c/$pkg" "$pkg present at $V (want absent)"; fi
done

echo "-- (d) IsTransitive=false static check --"
if sed -n '/ParsePipfileLock/,/^    }/p' "$PARSER" | grep -q "IsTransitive: false"; then
  pass "d/static" "ParsePipfileLock constructs Dependency(.., IsTransitive: false)"
else
  fail "d/static" "IsTransitive: false not found in ParsePipfileLock body"
fi
if grep -qi "transitive" "$WORKDIR/canonical.stdout"; then
  fail "d/no-field" "CLI JSON unexpectedly mentions transitive"
else
  pass "d/no-field" "CLI JSON carries no transitive field"
fi

echo "== summary =="
if [[ "$FAIL" -eq 0 ]]; then
  echo "PIP-LOCK-PROBE OK: canonical + malformed + dir-preference + transitive all pass."
  exit 0
else
  echo "PIP-LOCK-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
