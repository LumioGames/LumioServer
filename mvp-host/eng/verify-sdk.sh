#!/usr/bin/env bash
# SDK 族与 runtime 族的双口径校验。
#
# 判据刻意只到 major.minor：补丁号写进门禁就是重犯 LumioClient/eng/verify-toolchain.sh 的
# `grep -q '10.0.400'`（设计 §7.1 点名的反面样板）——任一台机器升一个 runtime 补丁、或
# Windows 侧 runtime 号不同，本脚本即红，而后续每张卡的验收都以 verify-all 为前置。
# 补丁号只作为交回物里记录的观测值，不作判据。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# global.json 只按当前工作目录向上查找、不看工程路径，因此必须先 cd（设计 §3.4 / §6.3）。
cd "$MVP_HOST_DIR" || exit 1

EXPECTED_BAND="10.0."

sdk_version="$(dotnet --version 2>/dev/null)"
# 先按期望族过滤再取族内最高：取全机最高会让一台同时装了 .NET 11 预览的开发机
# 直接 SDK_MISMATCH，尽管匹配的 10.0 runtime 就在机器上。
runtime_version="$(dotnet --list-runtimes 2>/dev/null \
  | awk -v band="^${EXPECTED_BAND//./\\.}" '$1 == "Microsoft.NETCore.App" && $2 ~ band { print $2 }' \
  | sort -V | tail -1)"

if [ -z "$sdk_version" ] || [ -z "$runtime_version" ]; then
  printf 'SDK_MISMATCH expected=sdk %s* / Microsoft.NETCore.App %s* actual=sdk %s / runtime %s\n' \
    "$EXPECTED_BAND" "$EXPECTED_BAND" "${sdk_version:-<none>}" "${runtime_version:-<none>}"
  exit 1
fi

minor_of() { printf '%s' "$1" | cut -d. -f1,2; }
sdk_minor="$(minor_of "$sdk_version")"
runtime_minor="$(minor_of "$runtime_version")"

mismatch=0
case "$sdk_version" in "$EXPECTED_BAND"*) ;; *) mismatch=1 ;; esac
case "$runtime_version" in "$EXPECTED_BAND"*) ;; *) mismatch=1 ;; esac
[ "$sdk_minor" = "$runtime_minor" ] || mismatch=1

if [ "$mismatch" -ne 0 ]; then
  printf 'SDK_MISMATCH expected=sdk %s* / Microsoft.NETCore.App %s* / same major.minor actual=sdk %s / runtime %s\n' \
    "$EXPECTED_BAND" "$EXPECTED_BAND" "$sdk_version" "$runtime_version"
  exit 1
fi

printf 'SDK_OK sdk=%s runtime=%s\n' "$sdk_version" "$runtime_version"
