#!/usr/bin/env bash
# Template for factory tools. Copy to <name>.sh and fill in.
# Rules: repo-relative, idempotent, no secrets, executable, --help required.
VERSION="0.1.0"
set -euo pipefail
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
