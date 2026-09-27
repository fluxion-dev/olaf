#!/usr/bin/env bash
# Template for factory tools. Copy to <name>.sh and fill in.
# Rules: repo-relative, idempotent, no secrets, executable, --help required.
# /tmp-mktemp permitted exception: ephemeral mktemp probe dirs under
# ${TMPDIR:-/tmp} with trap-cleanup are permitted (see KEEP_TEMP fragment
# below); persistent files/plans/retros/prototypes/worktrees MUST be
# repo-relative — NEVER write those outside the repo.
# --workdir standard: every probe accepts [--workdir <dir>] [--keep-temp]
# wired to WORKDIR/KEEP_TEMP below so runs are reproducible and debuggable.
VERSION="0.2.1"
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
