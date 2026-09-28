#!/usr/bin/env bash
# DEPRECATED (issue #123): apk/dpkg/rpm/container parsers + --max-image-mb deleted — no image-scan surface remains. Kept one issue cycle per lifecycle; delete next cycle if still unused.
# image-scan-probe: container-image scan probe (issue #64).
# Synthesizes minimal docker-save-style tarballs (apk installed / dpkg status
# layers) + an OCI exploded-layout dir, then scans each via
# `dotnet run --project src/Olaf.Cli -- --input <tar|dir> --format json` and
# asserts counts/exit codes:
#   C1 docker-save tar (apk 2 pkgs + dpkg 1 pkg)      -> total 3, exit 0
#   C2 OCI exploded-layout dir (same layers)          -> total 3, exit 0
#   C3 last-wins (swap 1.0 bottom, 2.0 top)           -> version 2.0, exit 0
#   C4 whiteout (.wh.installed hides lower DB)        -> pkg absent, exit 0
#   C5 size cap (--max-image-mb tiny)                 -> exit 2
#   C6 truncated/garbage tar                          -> exit 2 + stderr
#   C7 empty image (layer without DB files)           -> total 0, exit 0
# Exit 0 = all PASS; 1 = assertion FAIL; 2 = usage/IO error.
VERSION="0.1.0"
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
CLI="$ROOT/src/Olaf.Cli"

WORKDIR=""
KEEP_TEMP=0
TIMEOUT=300

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--workdir <dir>] [--keep-temp] [--timeout <secs>] [--help] [--version]"
  echo "Probe container-image scans: synth docker-save tar + OCI layout dir, scan via"
  echo "Olaf CLI (json), assert counts/exit codes (valid->0, cap/corrupt->2, empty->0/0)."
  echo "Exit 0 = all PASS; 1 = assertion FAIL; 2 = usage/IO error."
  echo "Example: ./.opencode/tools/factory/image-scan-probe.sh --timeout 300"
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi
while [[ $# -gt 0 ]]; do
  case "$1" in
    --workdir) WORKDIR="${2:-}"; [[ -n "$WORKDIR" ]] || { echo "Missing value for --workdir" >&2; exit 2; }; shift 2 ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    --timeout) TIMEOUT="${2:-}"; [[ "$TIMEOUT" =~ ^[0-9]+$ ]] || { echo "Invalid --timeout '$TIMEOUT'" >&2; exit 2; }; shift 2 ;;
    *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null || { echo "Missing required tool: dotnet" >&2; exit 2; }
command -v python3 >/dev/null || { echo "Missing required tool: python3" >&2; exit 2; }
[[ -d "$CLI" ]] || { echo "Missing CLI project: $CLI" >&2; exit 2; }

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/image-scan-probe-XXXXXX")"
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

fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

# Run the CLI with an optional timeout; echoes "exit=<n>" last line.
run_cli() {
  local out_file="$1" err_file="$2"; shift 2
  local code=0
  if command -v timeout >/dev/null; then
    timeout "$TIMEOUT" dotnet run --project "$CLI" -- "$@" >"$out_file" 2>"$err_file" || code=$?
    (( code == 124 )) && echo "TIMEOUT after ${TIMEOUT}s" >>"$err_file"
  else
    dotnet run --project "$CLI" -- "$@" >"$out_file" 2>"$err_file" || code=$?
  fi
  echo "$code"
}

json_total() { python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['summary']['total'])" "$1"; }
json_has() { python3 -c "
import json,sys
doc=json.load(open(sys.argv[1]))
want=set(sys.argv[2].split(','))
have={(l['ecosystem'],l['name'],l['version']) for l in doc['licenses']}
sys.exit(0 if all(any(w==n for (_,n,_) in have) for w in want) else 1)
" "$1" "$2"; }
json_version() { python3 -c "
import json,sys
doc=json.load(open(sys.argv[1]))
for l in doc['licenses']:
  if l['name']==sys.argv[2]:
    print(l['version']); break
" "$1" "$2"; }

APK_TWO='P: musl
V: 1.2.5-r0

P: busybox
V: 1.36.1-r0
'
DPKG_ONE='Package: bash
Version: 5.2.15-2
Status: install ok installed

'

# ---- Synth builders (python3 tarfile; no network, deterministic) ----
build_layer() { # <out.tar> <arcname> <content-file> [<arcname2> <file2> ...]
  local out="$1"; shift
  python3 - "$out" "$@" <<'PYEOF'
import sys, tarfile, os
out, pairs = sys.argv[1], sys.argv[2:]
with tarfile.open(out, "w") as t:
    for i in range(0, len(pairs), 2):
        ti = tarfile.TarInfo(pairs[i])
        ti.size = os.path.getsize(pairs[i+1])
        ti.mtime = 0
        with open(pairs[i+1], "rb") as f:
            t.addfile(ti, f)
PYEOF
}
build_dockersave() { # <out.tar> <layer1> [<layer2> ...]
  local out="$1"; shift
  python3 - "$out" "$@" <<'PYEOF'
import sys, json, tarfile, os
out, layers = sys.argv[1], sys.argv[2:]
manifest = [{"Config": "config.json", "RepoTags": ["probe:latest"],
             "Layers": [os.path.basename(l) for l in layers]}]
cfg = json.dumps({"architecture": "amd64", "os": "linux"}).encode()
with tarfile.open(out, "w") as t:
    for name, data in (("manifest.json", json.dumps(manifest).encode()),
                       ("config.json", cfg)):
        ti = tarfile.TarInfo(name); ti.size = len(data); ti.mtime = 0
        import io; t.addfile(ti, io.BytesIO(data))
    for l in layers:
        ti = tarfile.TarInfo(os.path.basename(l))
        ti.size = os.path.getsize(l); ti.mtime = 0
        with open(l, "rb") as f:
            t.addfile(ti, f)
PYEOF
}

printf '%s' "$APK_TWO" >"$WORKDIR/apk-installed"
printf '%s' "$DPKG_ONE" >"$WORKDIR/dpkg-status"
build_layer "$WORKDIR/layer-apk.tar" "lib/apk/db/installed" "$WORKDIR/apk-installed"
build_layer "$WORKDIR/layer-dpkg.tar" "var/lib/dpkg/status" "$WORKDIR/dpkg-status"
build_dockersave "$WORKDIR/image.tar" "$WORKDIR/layer-apk.tar" "$WORKDIR/layer-dpkg.tar"

# OCI exploded layout: manifest.json + layer blobs as sibling files.
mkdir -p "$WORKDIR/oci"
cp "$WORKDIR/layer-apk.tar" "$WORKDIR/oci/layer-apk.tar"
cp "$WORKDIR/layer-dpkg.tar" "$WORKDIR/oci/layer-dpkg.tar"
cat >"$WORKDIR/oci/manifest.json" <<'EOF'
[{"Config": "config.json", "RepoTags": ["probe:latest"], "Layers": ["layer-apk.tar", "layer-dpkg.tar"]}]
EOF

# ---- C1: docker-save tar -> total 3, exit 0 ----
code=$(run_cli "$WORKDIR/c1.out" "$WORKDIR/c1.err" --input "$WORKDIR/image.tar" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c1.out")" == "3" ]] \
  && json_has "$WORKDIR/c1.out" "musl,busybox,bash"; then
  pass "C1 docker-save tar -> total 3 (musl/busybox/bash), exit 0"
else
  fail_msg "C1 docker-save tar: exit=$code total=$(json_total "$WORKDIR/c1.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c1.err" 2>/dev/null)"
fi

# ---- C2: OCI exploded dir -> total 3, exit 0 ----
code=$(run_cli "$WORKDIR/c2.out" "$WORKDIR/c2.err" --input "$WORKDIR/oci" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c2.out")" == "3" ]]; then
  pass "C2 OCI exploded dir -> total 3, exit 0"
else
  fail_msg "C2 OCI dir: exit=$code total=$(json_total "$WORKDIR/c2.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c2.err" 2>/dev/null)"
fi

# ---- C3: last-wins (swap 1.0 bottom, 2.0 top) -> version 2.0 ----
printf 'P: swap\nV: 1.0\n' >"$WORKDIR/swap1"
printf 'P: swap\nV: 2.0\n' >"$WORKDIR/swap2"
build_layer "$WORKDIR/lw0.tar" "lib/apk/db/installed" "$WORKDIR/swap1"
build_layer "$WORKDIR/lw1.tar" "lib/apk/db/installed" "$WORKDIR/swap2"
build_dockersave "$WORKDIR/lastwins.tar" "$WORKDIR/lw0.tar" "$WORKDIR/lw1.tar"
code=$(run_cli "$WORKDIR/c3.out" "$WORKDIR/c3.err" --input "$WORKDIR/lastwins.tar" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_version "$WORKDIR/c3.out" swap)" == "2.0" ]]; then
  pass "C3 last-wins: top layer swap 2.0 wins, exit 0"
else
  fail_msg "C3 last-wins: exit=$code version=$(json_version "$WORKDIR/c3.out" swap 2>/dev/null || echo '?')"
fi

# ---- C4: whiteout (.wh.installed hides lower DB) -> pkg absent ----
printf 'P: vanishpkg\nV: 9.9\n' >"$WORKDIR/vanish"
build_layer "$WORKDIR/wo0.tar" "lib/apk/db/installed" "$WORKDIR/vanish"
: >"$WORKDIR/empty-marker"
build_layer "$WORKDIR/wo1.tar" "lib/apk/db/.wh.installed" "$WORKDIR/empty-marker"
build_dockersave "$WORKDIR/whiteout.tar" "$WORKDIR/wo0.tar" "$WORKDIR/wo1.tar"
code=$(run_cli "$WORKDIR/c4.out" "$WORKDIR/c4.err" --input "$WORKDIR/whiteout.tar" --format json)
if [[ "$code" == "0" ]] && ! json_has "$WORKDIR/c4.out" "vanishpkg"; then
  pass "C4 whiteout: vanishpkg hidden by .wh.installed, exit 0"
else
  fail_msg "C4 whiteout: exit=$code total=$(json_total "$WORKDIR/c4.out" 2>/dev/null || echo '?')"
fi

# ---- C5: size cap (tiny --max-image-mb) -> exit 2 ----
code=$(run_cli "$WORKDIR/c5.out" "$WORKDIR/c5.err" --input "$WORKDIR/image.tar" --format json --max-image-mb 0.00001)
if [[ "$code" == "2" ]] && grep -qiE 'cap|max-image' "$WORKDIR/c5.err"; then
  pass "C5 size cap: tiny --max-image-mb -> exit 2 with cap stderr"
else
  fail_msg "C5 size cap: exit=$code (want 2) stderr=$(head -c 200 "$WORKDIR/c5.err" 2>/dev/null)"
fi

# ---- C6: truncated + garbage tars -> exit 2 + non-empty stderr ----
head -c 512 "$WORKDIR/image.tar" >"$WORKDIR/trunc.tar"
code=$(run_cli "$WORKDIR/c6a.out" "$WORKDIR/c6a.err" --input "$WORKDIR/trunc.tar" --format json)
if [[ "$code" == "2" ]] && [[ -s "$WORKDIR/c6a.err" ]]; then
  pass "C6a truncated tar -> exit 2 + stderr"
else
  fail_msg "C6a truncated tar: exit=$code (want 2) stderr_bytes=$(wc -c <"$WORKDIR/c6a.err" 2>/dev/null)"
fi
head -c 1024 /dev/urandom >"$WORKDIR/garbage.tar"
code=$(run_cli "$WORKDIR/c6b.out" "$WORKDIR/c6b.err" --input "$WORKDIR/garbage.tar" --format json)
if [[ "$code" == "2" ]] && [[ -s "$WORKDIR/c6b.err" ]]; then
  pass "C6b garbage tar -> exit 2 + stderr"
else
  fail_msg "C6b garbage tar: exit=$code (want 2) stderr_bytes=$(wc -c <"$WORKDIR/c6b.err" 2>/dev/null)"
fi

# ---- C7: empty image (no DB files) -> total 0, exit 0 ----
printf 'hello\n' >"$WORKDIR/hostname-file"
build_layer "$WORKDIR/empty-layer.tar" "etc/hostname" "$WORKDIR/hostname-file"
build_dockersave "$WORKDIR/empty.tar" "$WORKDIR/empty-layer.tar"
code=$(run_cli "$WORKDIR/c7.out" "$WORKDIR/c7.err" --input "$WORKDIR/empty.tar" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c7.out")" == "0" ]]; then
  pass "C7 empty image -> total 0, exit 0"
else
  fail_msg "C7 empty image: exit=$code total=$(json_total "$WORKDIR/c7.out" 2>/dev/null || echo '?')"
fi

echo "---"
if (( fail )); then
  echo "IMAGE-SCAN-PROBE FAIL"
  exit 1
fi
echo "IMAGE-SCAN-PROBE OK"
exit 0
