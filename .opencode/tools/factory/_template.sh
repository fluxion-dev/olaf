#!/usr/bin/env bash
# Template for factory tools. Copy to <name>.sh and fill in.
# Rules: repo-relative, idempotent, no secrets, executable, --help required.
# /tmp-mktemp permitted exception: ephemeral mktemp probe dirs under
# ${TMPDIR:-/tmp} with trap-cleanup are permitted (see KEEP_TEMP fragment
# below); persistent files/plans/retros/prototypes/worktrees MUST be
# repo-relative — NEVER write those outside the repo.
# --workdir standard: every probe accepts [--workdir <dir>] [--keep-temp]
# wired to WORKDIR/KEEP_TEMP below so runs are reproducible and debuggable.
VERSION="0.2.2"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# Priority: git top-level -> script-dir fallback -> fail-closed.
# NOTE: factory tools live at .opencode/tools/factory/ so repo root is
#   ../../.. from the script dir. Scratch prototypes live one level deeper
#   (.opencode/tools/factory/scratch/) so use ../../../.. there instead.
#   Verify against parser-coverage-probe.sh:11 which uses ../../...
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
# Wire --workdir/--keep-temp flags to these; mktemp under ${TMPDIR:-/tmp}.
WORKDIR=""
KEEP_TEMP=0
# if [[ -z "$WORKDIR" ]]; then
#   WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/<name>-XXXXXX")"
#   MADE_TMP=1
# else
#   mkdir -p "$WORKDIR"
#   MADE_TMP=0
# fi
# cleanup() {
#   if (( ! KEEP_TEMP )) && (( MADE_TMP )) && [[ -d "$WORKDIR" ]]; then
#     rm -rf "$WORKDIR"
#   fi
# }
# trap cleanup EXIT

# ---- Canonical dep extractors (copy-paste; python3, no jq dependency) ----
# dep_count <json>: print licenses/dependencies/resolved count ("?" on bad JSON).
# dep_field <json> <name> <field>: print <field> for package <name>
#   (case-insensitive; unwraps {"dependency":{...}} envelope; empty if absent;
#   booleans print as true/false; string values lowercased only for
#   field=="direct" so version passthrough stays verbatim).
# Thin wrappers (define live alongside): dep_ver() { dep_field "$1" "$2" "version"; }
#   dep_direct() { dep_field "$1" "$2" "direct"; }
# Canonical source promoted to template 0.2.2 after 3rd/4th family use
#   (pip-lock-probe.sh dep_ver/dep_count, transitive-surface-probe.sh
#   dep_direct/dep_count — dep_count 2nd exact copy; behavior identical).
#   Back-ported: both probes now carry this exact block live.
# dep_count() {
#   python3 - "$1" <<'PY' 2>/dev/null
# import json, sys
# try:
#     with open(sys.argv[1]) as f:
#         data = json.load(f)
# except Exception:
#     print("?")
#     sys.exit(0)
# items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
# print(len(items) if isinstance(items, list) else "?")
# PY
# }
# dep_field() {
#   python3 - "$1" "$2" "$3" <<'PY' 2>/dev/null
# import json, sys
# path, want, field = sys.argv[1], sys.argv[2].lower(), sys.argv[3]
# try:
#     with open(path) as f:
#         data = json.load(f)
# except Exception:
#     sys.exit(0)
# items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
# if isinstance(items, list):
#     for it in items:
#         if not isinstance(it, dict):
#             continue
#         dep = it.get("dependency", it)
#         name = str(dep.get("name", it.get("name", "")))
#         if name.lower() == want:
#             v = dep.get(field, it.get(field, ""))
#             if v is True:
#                 print("true")
#             elif v is False:
#                 print("false")
#             elif isinstance(v, str):
#                 print(v.lower() if field == "direct" else v)
#             else:
#                 print(str(v) if v != "" else "")
#             break
# PY
# }
# dep_ver() { dep_field "$1" "$2" "version"; }
# dep_direct() { dep_field "$1" "$2" "direct"; }

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [options]"
  echo "TODO: describe purpose, options, examples."
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi
echo "TODO: implement" >&2
exit 2
