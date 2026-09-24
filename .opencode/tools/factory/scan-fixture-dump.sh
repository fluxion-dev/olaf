#!/usr/bin/env bash
# Nested fixture scan dump. Builds a nested fixture (root npm lock+manifest,
# deep pip requirements) and runs the CLI full scan + pypi scan.
# Usage: scan-fixture-dump.sh [--workdir <dir>|--help|--version] [workdir]
VERSION="0.1.0"
set -euo pipefail

usage() {
  echo "Usage: $(basename "$0") [workdir|--workdir <dir>] [--help|--version]"
  echo ""
  echo "Build nested scan fixture (root npm lock+manifest, deep pip"
  echo "requirements), run CLI full scan + --ecosystem pypi scan, print"
  echo "dep counts + sorted (ecosystem,name,version) list."
  echo ""
  echo "Options:"
  echo "  --workdir <dir>  Work dir (default: /tmp/olaf-nested)"
  echo "  --help           Show this help and exit 0"
  echo "  --version        Print version and exit 0"
  echo ""
  echo "Examples:"
  echo "  $(basename "$0")"
  echo "  $(basename "$0") /tmp/olaf-nested"
  echo "  $(basename "$0") --workdir /tmp/olaf-nested"
}

if [[ "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi

WORK="/tmp/olaf-nested"
if [[ "${1:-}" == "--workdir" ]]; then
  WORK="${2:?--workdir requires a directory argument}"
elif [[ $# -ge 1 ]]; then
  WORK="$1"
fi

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
FIX="$ROOT/tests/Olaf.Tests/Fixtures"

# Idempotent: reset workdir first.
rm -rf "$WORK"
mkdir -p "$WORK/nested/deep"
cp "$FIX/npm/package.json" "$WORK/package.json"
cp "$FIX/npm/package-lock.json" "$WORK/package-lock.json"
cp "$FIX/pip/requirements.txt" "$WORK/nested/deep/requirements.txt"

find "$WORK" -type f | sort

dump_sorted() {
  # Cheap sorted (ecosystem,name,version) list from a scan JSON file.
  # Args: <json-file>. Prints sorted lines, never fails the caller.
  local json="$1"
  python3 - "$json" <<'PY' 2>/dev/null || true
import json, sys
path = sys.argv[1]
try:
    with open(path) as f:
        data = json.load(f)
except Exception as e:
    print(f"(could not parse {path}: {e})")
    sys.exit(0)
items = data if isinstance(data, list) else data.get("dependencies", data.get("resolved", data.get("results", [])))
rows = set()
if isinstance(items, list):
    for it in items:
        if not isinstance(it, dict):
            continue
        dep = it.get("dependency", it)
        eco = dep.get("ecosystem", it.get("ecosystem", "?"))
        name = dep.get("name", it.get("name", "?"))
        ver = dep.get("version", it.get("version", "?"))
        rows.add((str(eco), str(name), str(ver)))
for eco, name, ver in sorted(rows):
    print(f"{eco},{name},{ver}")
print(f"count={len(rows)}")
PY
}

echo "=== full scan ==="
if dotnet run --project "$ROOT/src/Olaf.Cli" -- --input "$WORK" --format json --out "$WORK/scan-full.json" --verbose; then
  echo "--- full scan sorted (ecosystem,name,version) ---"
  dump_sorted "$WORK/scan-full.json"
else
  echo "full scan failed (exit $?)" >&2
  exit 1
fi

echo "=== pypi scan ==="
if dotnet run --project "$ROOT/src/Olaf.Cli" -- --input "$WORK" --ecosystem pypi --format json --out "$WORK/scan-pypi.json" --verbose 2>&1 | grep -o 'found [0-9]* dependencies'; then
  echo "--- pypi scan sorted (ecosystem,name,version) ---"
  dump_sorted "$WORK/scan-pypi.json"
else
  echo "pypi scan found 0 dependencies or failed" >&2
  if [[ -f "$WORK/scan-pypi.json" ]]; then
    dump_sorted "$WORK/scan-pypi.json"
  fi
fi
