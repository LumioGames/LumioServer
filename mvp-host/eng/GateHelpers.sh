#!/usr/bin/env bash

gate_validate_relative_path() {
  local path="${1:-}"
  [ -n "$path" ] || return 1
  case "$path" in
    /*|\\*|[A-Za-z]:*|*\\*|*//*|.|./*|*/./*|*/.|..|../*|*/../*|*/..)
      return 1
      ;;
  esac
  return 0
}

gate_find_sorted() (
  set -o pipefail
  find "$@" | sort
)

gate_sha256_stream() (
  set -o pipefail
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 | awk '{ print $1 }'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum | awk '{ print $1 }'
  else
    printf 'MVP_HOST_GATE_HELPER_FAIL missing-sha256-tool\n' >&2
    return 127
  fi
)

gate_sha256_file() {
  local path="$1"
  [ -f "$path" ] || return 1
  gate_sha256_stream < "$path"
}
