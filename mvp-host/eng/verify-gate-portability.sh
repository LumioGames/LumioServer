#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=eng/GateHelpers.sh
source "$SCRIPT_DIR/GateHelpers.sh"

gate_validate_relative_path 'eng/verify-all.sh' || exit 1
for invalid_path in '../README.md' '/tmp/file' 'C:/outside' 'eng\file' 'eng//file' './eng/file'; do
  if gate_validate_relative_path "$invalid_path"; then
    printf 'MVP_HOST_GATE_PORTABILITY_FAIL path=%s\n' "$invalid_path"
    exit 1
  fi
done

missing="$SCRIPT_DIR/missing-$RANDOM-$RANDOM"
if gate_find_sorted "$missing" -type f >/dev/null 2>&1; then
  printf 'MVP_HOST_GATE_PORTABILITY_FAIL enumeration\n'
  exit 1
fi

printf 'MVP_HOST_GATE_PORTABILITY_OK\n'
