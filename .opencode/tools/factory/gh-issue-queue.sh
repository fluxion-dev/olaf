#!/usr/bin/env bash
# List OPEN issues for the factory loop. Usage: gh-issue-queue.sh [--json|--help|--version]
VERSION="0.2.0"
set -euo pipefail
if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: gh-issue-queue.sh [--json]"
  echo "Prints OPEN issues as number|title, or raw JSON with --json."
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "gh-issue-queue.sh $VERSION"
  exit 0
fi
if [[ "${1:-}" == "--json" ]]; then
  exec gh issue list --limit 50 --json number,title,state --jq '.[] | select(.state=="OPEN")'
else
  exec gh issue list --limit 50 --json number,title,state --jq '.[] | select(.state=="OPEN") | "\(.number)|\(.title)"'
fi
