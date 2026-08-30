#!/usr/bin/env bash
# Explicit process-level verification entry point. Integration tests are kept
# out of verify-all so ordinary builds remain bounded and deterministic.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$MVP_HOST_DIR" || exit 1

fail() {
  printf 'MVP_HOST_INTEGRATION_FAIL %s\n' "$1"
  exit "${2:-1}"
}

dotnet build build.proj -c Release || fail build $?

integration_project="tests/Lumio.Server.MvpHost.Integration.Tests/Lumio.Server.MvpHost.Integration.Tests.csproj"
if [ ! -f "$integration_project" ]; then
  fail missing-integration-project 2
fi

dotnet test "$integration_project" -c Release --no-build || fail test $?

echo MVP_HOST_INTEGRATION_OK
