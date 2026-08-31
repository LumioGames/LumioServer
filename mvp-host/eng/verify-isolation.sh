#!/usr/bin/env bash
# mvp-host 与 Rust workspace 的物理隔离门禁（设计 §3.4 的三条结构不变量）。
# 隔离靠机制而非纪律：约定会被遗忘，退出码不会。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$MVP_HOST_DIR/.." && pwd)"
# shellcheck source=eng/GateHelpers.sh
source "$SCRIPT_DIR/GateHelpers.sh"

VIOLATION_EXIT=34
violations=0

report() {
  printf 'MVP_HOST_ISOLATION_VIOLATION %s\n' "$1"
  violations=$((violations + 1))
}

# ① 仓库根不得出现 C# 构建根文件——放在仓根会让 net10.0/LangVersion 14.0 意外管辖
#    未来 Rust 侧任意位置的测试夹具 csproj（设计 §3.4 的反向风险）。
for f in global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config; do
  if [ -e "$REPO_ROOT/$f" ]; then
    report "$f"
  fi
done

# ② Rust 侧的七个仓根目录（存在时）不得出现 C# 源码或工程。
for d in modules crates tools benches contracts generated tests; do
  [ -d "$REPO_ROOT/$d" ] || continue
  hits="$(gate_find_sorted "$REPO_ROOT/$d" \( -name '*.csproj' -o -name '*.cs' -o -name '*.slnx' \) -type f)"
  find_status=$?
  if [ "$find_status" -ne 0 ]; then
    printf 'MVP_HOST_ISOLATION_FAIL enumeration=%s\n' "$d"
    exit "$VIOLATION_EXIT"
  fi
  while IFS= read -r hit; do
    [ -n "$hit" ] || continue
    report "${hit#"$REPO_ROOT"/}"
  done <<< "$hits"
done

# ③ mvp-host/ 下不得出现 Rust 工程文件。
hits="$(gate_find_sorted "$MVP_HOST_DIR" \( -name '*.rs' -o -name 'Cargo.toml' \) -type f)"
find_status=$?
if [ "$find_status" -ne 0 ]; then
  printf 'MVP_HOST_ISOLATION_FAIL enumeration=mvp-host\n'
  exit "$VIOLATION_EXIT"
fi
while IFS= read -r hit; do
  [ -n "$hit" ] || continue
  report "${hit#"$REPO_ROOT"/}"
done <<< "$hits"

if [ "$violations" -gt 0 ]; then
  printf 'MVP_HOST_ISOLATION_FAIL violations=%d\n' "$violations"
  exit "$VIOLATION_EXIT"
fi

echo MVP_HOST_ISOLATION_OK
