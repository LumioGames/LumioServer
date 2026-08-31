#!/usr/bin/env bash
# mvp-host 的一键验证入口。成功末行 MVP_HOST_VERIFY_OK 并退出 0；
# 任一步失败打印 MVP_HOST_VERIFY_FAIL <step> 并非零退出。
#
# 零工程状态（构建根刚落地、还没有任何 csproj）下空 glob 不算失败，同样输出 MVP_HOST_VERIFY_OK。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=eng/GateHelpers.sh
source "$SCRIPT_DIR/GateHelpers.sh"

# global.json 只按 cwd 向上查找、不看工程路径；cwd 在仓根时会静默绕过 SDK pin（设计 §6.3）。
cd "$MVP_HOST_DIR" || exit 1

fail() {
  printf 'MVP_HOST_VERIFY_FAIL %s\n' "$1"
  exit "${2:-1}"
}

bash eng/verify-isolation.sh || fail isolation $?
bash eng/verify-sdk.sh || fail sdk $?
bash eng/verify-gate-portability.sh || fail gate-portability $?

# 契约面的两道锁排在 restore 之前：镜像或生成物被手改时，后面的构建与测试跑出来的
# 「绿」是对着被篡改的契约算的，越早拦下越好。两者都不需要架构源在手。
bash eng/verify-contract-mirror.sh || fail contract-mirror $?
bash eng/verify-generated-contracts.sh || fail generated-contracts $?

dotnet restore build.proj --locked-mode --disable-parallel || fail restore $?

find_projects() {
  local roots=() root
  for root in "$@"; do
    [ -d "$root" ] && roots+=("$root")
  done
  [ "${#roots[@]}" -gt 0 ] || return 0
  gate_find_sorted "${roots[@]}" -name '*.csproj' -type f
}

source_projects="$(find_projects src tests testkit)"
enumeration_status=$?
[ "$enumeration_status" -eq 0 ] || fail project-enumeration "$enumeration_status"

# 逐工程 format 校验；零工程时循环体不执行。
while IFS= read -r proj; do
  [ -n "$proj" ] || continue
  dotnet format "$proj" --verify-no-changes --no-restore || fail "format $proj" $?
done <<< "$source_projects"

dotnet build build.proj -c Release --no-restore || fail build $?

# 集成测试显式触发，不进默认链路（eng/verify-integration.sh）。
while IFS= read -r proj; do
  [ -n "$proj" ] || continue
  case "$proj" in tests/*) ;; *) continue ;; esac
  case "$proj" in *.Integration.Tests.csproj) continue ;; esac
  dotnet test "$proj" -c Release --no-build || fail "test $proj" $?
done <<< "$source_projects"

echo MVP_HOST_VERIFY_OK
