#!/usr/bin/env bash
# Fast test gate. Usage: dotnet-test-fast.sh [--filter <expr>|--help|--version]
VERSION="0.2.0"
set -euo pipefail
if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: dotnet-test-fast.sh [--filter <expr>]"
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "dotnet-test-fast.sh $VERSION"
  exit 0
fi
exec dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal "$@"
