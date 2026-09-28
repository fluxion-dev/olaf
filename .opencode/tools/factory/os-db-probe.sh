#!/usr/bin/env bash
# DEPRECATED (issue #123): apk/dpkg/rpm parsers deleted — no OS-DB surface remains. Kept one issue cycle per lifecycle; delete next cycle if still unused.
# os-db-probe: OS database parser probe (issue #65).
# Scans checked-in OS fixtures + synth temp DBs directly via
# `dotnet run --project src/Olaf.Cli -- --input <path> --format json` and
# asserts counts/versions/exit codes:
#   C1 apk direct (installed w/ P/V/A:/L:)       -> total 3 + verbatim versions, exit 0
#   C2 dpkg direct (status w/ Architecture:+epoch)-> total 2 + epoch kept, exit 0
#   C3 rpm text-dump direct (NVRA incl hyphenated)-> total 4 + Ecosystem=rpm, exit 0
#   C4 malformed-skip per DB (a/b/c)             -> valid preserved, exit 0
#   C5 empty DB (a/b/c)                          -> total 0, exit 0
#   C6 rpm layer inside docker-save tar          -> container total includes rpm, exit 0
#   C7 binary Packages bytes (NUL)               -> total 0, exit 0, never crash
# Exit 0 = all PASS; 1 = assertion FAIL; 2 = usage/IO error.
VERSION="0.1.0"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# Priority: git top-level -> script-dir fallback -> fail-closed.
# NOTE: factory tools live at .opencode/tools/factory/ so repo
#   root is ../../.. from the script dir.
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

# ---- Canonical pass/fail (copy-paste) ----
fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

# ---- Canonical temp-dir with KEEP_TEMP (copy-paste; adapt prefix) ----
WORKDIR=""
KEEP_TEMP=0
TIMEOUT=300
if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--workdir <dir>] [--keep-temp] [--timeout <secs>] [--help] [--version]"
  echo "Probe OS DB parsers: apk/dpkg/rpm direct fixtures + malformed/empty/binary"
  echo "edge cases + rpm-in-container layer, via Olaf CLI (json), assert counts/exit codes."
  echo "Exit 0 = all PASS; 1 = assertion FAIL; 2 = usage/IO error."
  echo "Example: ./.opencode/tools/factory/os-db-probe.sh --timeout 300"
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

CLI="$ROOT/src/Olaf.Cli"
APK_FIX="$ROOT/tests/Olaf.Tests/Fixtures/apk/installed"
DPKG_FIX="$ROOT/tests/Olaf.Tests/Fixtures/dpkg/status"
RPM_FIX="$ROOT/tests/Olaf.Tests/Fixtures/rpm/Packages"
[[ -d "$CLI" ]] || { echo "Missing CLI project: $CLI" >&2; exit 2; }
[[ -f "$APK_FIX" ]] || { echo "Missing fixture: $APK_FIX" >&2; exit 2; }
[[ -f "$DPKG_FIX" ]] || { echo "Missing fixture: $DPKG_FIX" >&2; exit 2; }
[[ -f "$RPM_FIX" ]] || { echo "Missing fixture: $RPM_FIX" >&2; exit 2; }

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/os-db-probe-XXXXXX")"
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
json_names() { python3 -c "
import json,sys
doc=json.load(open(sys.argv[1]))
print(' '.join(sorted(l['name'] for l in doc['licenses'])))
" "$1"; }
json_version() { python3 -c "
import json,sys
doc=json.load(open(sys.argv[1]))
for l in doc['licenses']:
  if l['name']==sys.argv[2]:
    print(l['version']); break
" "$1" "$2"; }
json_eco() { python3 -c "
import json,sys
doc=json.load(open(sys.argv[1]))
print(' '.join(sorted({l['ecosystem'] for l in doc['licenses']})))
" "$1"; }
json_has() { python3 -c "
import json,sys
doc=json.load(open(sys.argv[1]))
want=set(sys.argv[2].split(','))
have={l['name'] for l in doc['licenses']}
sys.exit(0 if want <= have else 1)
" "$1" "$2"; }

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
import sys, json, tarfile, os, io
out, layers = sys.argv[1], sys.argv[2:]
manifest = [{"Config": "config.json", "RepoTags": ["probe:latest"],
             "Layers": [os.path.basename(l) for l in layers]}]
cfg = json.dumps({"architecture": "amd64", "os": "linux"}).encode()
with tarfile.open(out, "w") as t:
    for name, data in (("manifest.json", json.dumps(manifest).encode()),
                       ("config.json", cfg)):
        ti = tarfile.TarInfo(name); ti.size = len(data); ti.mtime = 0
        t.addfile(ti, io.BytesIO(data))
    for l in layers:
        ti = tarfile.TarInfo(os.path.basename(l))
        ti.size = os.path.getsize(l); ti.mtime = 0
        with open(l, "rb") as f:
            t.addfile(ti, f)
PYEOF
}

# ---- C1: apk direct -> total 3 + verbatim versions, exit 0 ----
code=$(run_cli "$WORKDIR/c1.out" "$WORKDIR/c1.err" --input "$APK_FIX" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c1.out")" == "3" ]] \
  && [[ "$(json_version "$WORKDIR/c1.out" musl)" == "1.2.5-r0" ]] \
  && [[ "$(json_version "$WORKDIR/c1.out" busybox)" == "1.36.1-r0" ]] \
  && [[ "$(json_version "$WORKDIR/c1.out" zlib)" == "1.3.1-r0" ]] \
  && [[ "$(json_eco "$WORKDIR/c1.out")" == "apk" ]]; then
  pass "C1 apk direct -> total 3, verbatim V (1.2.5-r0/1.36.1-r0/1.3.1-r0), eco=apk, exit 0"
else
  fail_msg "C1 apk direct: exit=$code total=$(json_total "$WORKDIR/c1.out" 2>/dev/null || echo '?') names=$(json_names "$WORKDIR/c1.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c1.err" 2>/dev/null)"
fi

# ---- C2: dpkg direct -> total 2 + epoch kept, exit 0 ----
code=$(run_cli "$WORKDIR/c2.out" "$WORKDIR/c2.err" --input "$DPKG_FIX" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c2.out")" == "2" ]] \
  && [[ "$(json_version "$WORKDIR/c2.out" bash)" == "2:5.2-5" ]] \
  && [[ "$(json_version "$WORKDIR/c2.out" coreutils)" == "9.4-1" ]] \
  && [[ "$(json_eco "$WORKDIR/c2.out")" == "dpkg" ]] \
  && grep -q "Architecture:" "$DPKG_FIX"; then
  pass "C2 dpkg direct -> total 2, epoch 2:5.2-5 kept, eco=dpkg, exit 0"
else
  fail_msg "C2 dpkg direct: exit=$code total=$(json_total "$WORKDIR/c2.out" 2>/dev/null || echo '?') bash=$(json_version "$WORKDIR/c2.out" bash 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c2.err" 2>/dev/null)"
fi

# ---- C3: rpm text-dump direct -> total 4 + hyphenated, exit 0 ----
code=$(run_cli "$WORKDIR/c3.out" "$WORKDIR/c3.err" --input "$RPM_FIX" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c3.out")" == "4" ]] \
  && [[ "$(json_version "$WORKDIR/c3.out" bash)" == "5.2-5" ]] \
  && [[ "$(json_version "$WORKDIR/c3.out" gpg-pubkey)" == "abcdef12-1" ]] \
  && [[ "$(json_eco "$WORKDIR/c3.out")" == "rpm" ]]; then
  pass "C3 rpm direct -> total 4, hyphenated gpg-pubkey abcdef12-1, eco=rpm, exit 0"
else
  fail_msg "C3 rpm direct: exit=$code total=$(json_total "$WORKDIR/c3.out" 2>/dev/null || echo '?') names=$(json_names "$WORKDIR/c3.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c3.err" 2>/dev/null)"
fi

# ---- C4: malformed-skip per DB (filenames must be installed/status/Packages) ----
mkdir -p "$WORKDIR/c4a" "$WORKDIR/c4b" "$WORKDIR/c4c"
printf 'GARBAGE LINE WITHOUT COLON\nP:musl\nV:1.2.5-r0\n\nNOT-A-FIELD!!!\nP:busybox\nV:1.36.1-r0\n' >"$WORKDIR/c4a/installed"
code=$(run_cli "$WORKDIR/c4a.out" "$WORKDIR/c4a.err" --input "$WORKDIR/c4a/installed" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c4a.out")" == "2" ]] \
  && json_has "$WORKDIR/c4a.out" "musl,busybox"; then
  pass "C4a apk malformed-skip -> 2 valid preserved, exit 0"
else
  fail_msg "C4a apk malformed: exit=$code total=$(json_total "$WORKDIR/c4a.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c4a.err" 2>/dev/null)"
fi

printf 'this is not a stanza\n\nPackage: bash\nVersion: 5.2.15-2\nStatus: install ok installed\nArchitecture: amd64\n\nGARBAGE WITHOUT COLON\n' >"$WORKDIR/c4b/status"
code=$(run_cli "$WORKDIR/c4b.out" "$WORKDIR/c4b.err" --input "$WORKDIR/c4b/status" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c4b.out")" == "1" ]] \
  && json_has "$WORKDIR/c4b.out" "bash"; then
  pass "C4b dpkg malformed-skip -> bash preserved, exit 0"
else
  fail_msg "C4b dpkg malformed: exit=$code total=$(json_total "$WORKDIR/c4b.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c4b.err" 2>/dev/null)"
fi

printf 'not-a-package\nno-dot-here\nbash-5.2-5.x86_64\n\n   \ngpg-pubkey-abcdef12-1.noarch\n' >"$WORKDIR/c4c/Packages"
code=$(run_cli "$WORKDIR/c4c.out" "$WORKDIR/c4c.err" --input "$WORKDIR/c4c/Packages" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c4c.out")" == "2" ]] \
  && json_has "$WORKDIR/c4c.out" "bash,gpg-pubkey"; then
  pass "C4c rpm malformed-skip -> 2 valid incl hyphenated, exit 0"
else
  fail_msg "C4c rpm malformed: exit=$code total=$(json_total "$WORKDIR/c4c.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c4c.err" 2>/dev/null)"
fi

# ---- C5: empty DB -> total 0, exit 0 ----
mkdir -p "$WORKDIR/c5a" "$WORKDIR/c5b" "$WORKDIR/c5c"
: >"$WORKDIR/c5a/installed"
: >"$WORKDIR/c5b/status"
: >"$WORKDIR/c5c/Packages"
c5ok=1
for tag in "c5a:installed" "c5b:status" "c5c:Packages"; do
  d="${tag%%:*}"; f="${tag##*:}"
  code=$(run_cli "$WORKDIR/$d.out" "$WORKDIR/$d.err" --input "$WORKDIR/$d/$f" --format json)
  if [[ "$code" != "0" ]] || [[ "$(json_total "$WORKDIR/$d.out" 2>/dev/null)" != "0" ]]; then
    fail_msg "C5 $f empty: exit=$code (want 0) total=$(json_total "$WORKDIR/$d.out" 2>/dev/null || echo '?')"
    c5ok=0
  fi
done
(( c5ok )) && pass "C5 empty DB (installed/status/Packages) -> total 0, exit 0"

# ---- C6: rpm layer inside docker-save tar -> container total includes rpm ----
printf 'bash-5.2-5.x86_64\ngpg-pubkey-abcdef12-1.noarch\n' >"$WORKDIR/c6-Packages"
build_layer "$WORKDIR/c6-layer.tar" "var/lib/rpm/Packages" "$WORKDIR/c6-Packages"
build_dockersave "$WORKDIR/c6-image.tar" "$WORKDIR/c6-layer.tar"
code=$(run_cli "$WORKDIR/c6.out" "$WORKDIR/c6.err" --input "$WORKDIR/c6-image.tar" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c6.out")" == "2" ]] \
  && json_has "$WORKDIR/c6.out" "bash,gpg-pubkey" \
  && [[ "$(json_eco "$WORKDIR/c6.out")" == "rpm" ]]; then
  pass "C6 docker-save tar w/ rpm layer -> total 2 incl gpg-pubkey, eco=rpm, exit 0"
else
  fail_msg "C6 rpm-in-tar: exit=$code total=$(json_total "$WORKDIR/c6.out" 2>/dev/null || echo '?') names=$(json_names "$WORKDIR/c6.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c6.err" 2>/dev/null)"
fi

# ---- C7: binary Packages bytes -> never crash (total 0, exit 0) ----
mkdir -p "$WORKDIR/c7"
python3 -c "open('$WORKDIR/c7/Packages','wb').write(b'\x00\x01\x02BerkeleyDB\xff\xfe' + b'\x00'*256 + b'bash-5.2-5.x86_64\n')"
code=$(run_cli "$WORKDIR/c7.out" "$WORKDIR/c7.err" --input "$WORKDIR/c7/Packages" --format json)
if [[ "$code" == "0" ]] && [[ "$(json_total "$WORKDIR/c7.out" 2>/dev/null)" == "0" ]]; then
  pass "C7 binary Packages (NUL sniff) -> total 0, exit 0, never crash"
else
  fail_msg "C7 binary Packages: exit=$code (want 0) total=$(json_total "$WORKDIR/c7.out" 2>/dev/null || echo '?') stderr=$(head -c 200 "$WORKDIR/c7.err" 2>/dev/null)"
fi

echo "---"
if (( fail )); then
  echo "OS-DB-PROBE FAIL"
  exit 1
fi
echo "OS-DB-PROBE OK"
exit 0
