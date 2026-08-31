#!/usr/bin/env bash
# Generated/ 的守护。与 verify-contract-mirror.sh 同一形状的**两条独立检查**：
#
#   ① 产物未被手改 —— 比对 Generated/ 下每个 .cs 与 GeneratedContractManifest.cs 记录的哈希。
#      不需要架构源，漂移即以退出码 32 硬失败。这是门禁。
#   ② 与上游同步   —— 需要 $LUMIO_ARCHITECTURE_ROOT，把 manifest 记录的 commit 与 $ARCH_REF
#      当前指向比较。上游发新版不是本仓的错误状态，**只报告、不影响退出码**。
#
# 对照组探针：`bash eng/verify-generated-contracts.sh --self-test`。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=eng/GateHelpers.sh
source "$SCRIPT_DIR/GateHelpers.sh"
cd "$MVP_HOST_DIR" || exit 1

DRIFT_EXIT=32
PROJECT_DIR=src/Lumio.Server.MvpHost.GeneratedContracts
GENERATED_DIR="$PROJECT_DIR/Generated"
MANIFEST_CS="$PROJECT_DIR/GeneratedContractManifest.cs"

# 从 manifest 的 C# 数组字面量里取出 "<sha256>  <相对路径>" 行。
manifest_entries() {
  local manifest="$1"
  grep -oE '"[0-9a-f]{64}  [^"]+"' "$manifest" | tr -d '"'
}

check_not_hand_edited() {
  local root="$1"
  local manifest="$root/$MANIFEST_CS"
  local generated="$root/$GENERATED_DIR"
  local drift=0 count=0

  if [ ! -f "$manifest" ]; then
    printf 'MVP_HOST_GENERATED_DRIFT missing-manifest %s\n' "$MANIFEST_CS"
    printf 'MVP_HOST_GENERATED_FAIL drift=1\n'
    return "$DRIFT_EXIT"
  fi

  local entries
  entries="$(manifest_entries "$manifest")"
  if [ -z "$entries" ]; then
    printf 'MVP_HOST_GENERATED_DRIFT empty-manifest %s\n' "$MANIFEST_CS"
    printf 'MVP_HOST_GENERATED_FAIL drift=1\n'
    return "$DRIFT_EXIT"
  fi

  while IFS= read -r entry; do
    [ -n "$entry" ] || continue
    local expected path actual
    expected="${entry%% *}"
    path="${entry#* }"; path="${path# }"
    count=$((count + 1))

    if ! gate_validate_relative_path "$path"; then
      printf 'MVP_HOST_GENERATED_DRIFT invalid-path %s\n' "$path"
      drift=$((drift + 1))
      continue
    fi

    if [ ! -f "$generated/$path" ]; then
      printf 'MVP_HOST_GENERATED_DRIFT missing %s\n' "$path"
      drift=$((drift + 1))
      continue
    fi

    actual="$(gate_sha256_file "$generated/$path")"
    if [ "$actual" != "$expected" ]; then
      printf 'MVP_HOST_GENERATED_DRIFT modified %s (manifest %s != 实际 %s)\n' "$path" "$expected" "$actual"
      drift=$((drift + 1))
    fi
  done <<< "$entries"

  # manifest 之外的 .cs 同样是漂移——多拷一个文件与改一个字节等价危险。
  if [ -d "$generated" ]; then
    found_files="$(cd "$generated" && gate_find_sorted . -name '*.cs' -type f | sed 's|^\./||')"
    find_status=$?
    if [ "$find_status" -ne 0 ]; then
      printf 'MVP_HOST_GENERATED_FAIL enumeration-error\n'
      return "$DRIFT_EXIT"
    fi
    while IFS= read -r found; do
      [ -n "$found" ] || continue
      if ! printf '%s\n' "$entries" | sed 's/^[^ ]*  *//' | grep -Fxq "$found"; then
        printf 'MVP_HOST_GENERATED_DRIFT unregistered %s\n' "$found"
        drift=$((drift + 1))
      fi
    done <<< "$found_files"
  fi

  if [ "$drift" -gt 0 ]; then
    printf 'MVP_HOST_GENERATED_FAIL drift=%d\n' "$drift"
    return "$DRIFT_EXIT"
  fi

  printf 'MVP_HOST_GENERATED_OK files=%d\n' "$count"
  return 0
}

report_upstream_sync() {
  local arch_root="${LUMIO_ARCHITECTURE_ROOT:-}"
  if [ -z "$arch_root" ] || [ ! -d "$arch_root/.git" ]; then
    printf 'MVP_HOST_GENERATED_UPSTREAM skipped（未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库）\n'
    return 0
  fi

  local arch_ref arch_commit pinned drift=0
  arch_ref="${LUMIO_ARCHITECTURE_REF:-origin/main}"
  arch_commit="$(git -C "$arch_root" rev-parse "$arch_ref" 2>/dev/null)"
  pinned="$(grep -oE 'ArchitectureCommit => "[0-9a-f]{40}"' "$MANIFEST_CS" | grep -oE '[0-9a-f]{40}')"

  if [ -z "$arch_commit" ] || [ -z "$pinned" ]; then
    printf 'MVP_HOST_GENERATED_UPSTREAM skipped（解析不出 %s 或 manifest 里的 commit）\n' "$arch_ref"
    return 0
  fi

  while IFS= read -r entry; do
    [ -n "$entry" ] || continue
    local expected path upstream
    expected="${entry%% *}"
    path="${entry#* }"; path="${path# }"
    if ! upstream="$(git -C "$arch_root" show "$arch_commit:packages/csharp/$path" 2>/dev/null | gate_sha256_stream)"; then
      printf 'MVP_HOST_GENERATED_UPSTREAM gone packages/csharp/%s\n' "$path"
      drift=$((drift + 1))
    elif [ "$upstream" != "$expected" ]; then
      printf 'MVP_HOST_GENERATED_UPSTREAM behind %s（上游 %s）\n' "$path" "$upstream"
      drift=$((drift + 1))
    fi
  done <<< "$(manifest_entries "$MANIFEST_CS")"

  if [ "$pinned" != "$arch_commit" ]; then
    printf 'MVP_HOST_GENERATED_UPSTREAM pinned=%s ref=%s —— commit 已前进\n' "$pinned" "$arch_commit"
  fi

  if [ "$drift" -gt 0 ]; then
    printf 'MVP_HOST_GENERATED_UPSTREAM behind=%d —— 报告性质，不影响退出码；同步用 bash eng/generate-contracts.sh\n' "$drift"
  else
    printf 'MVP_HOST_GENERATED_UPSTREAM in-sync arch=%s\n' "$arch_commit"
  fi
  return 0
}

self_test() {
  local sandbox status
  sandbox="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$sandbox'" EXIT

  mkdir -p "$sandbox/$GENERATED_DIR/Lumio.Gen.Probe"
  printf 'namespace Probe { }\n' > "$sandbox/$GENERATED_DIR/Lumio.Gen.Probe/Probe.cs"
  local digest
  digest="$(gate_sha256_file "$sandbox/$GENERATED_DIR/Lumio.Gen.Probe/Probe.cs")"
  mkdir -p "$sandbox/$PROJECT_DIR"
  printf 'ArtifactHashes = new[] { "%s  Lumio.Gen.Probe/Probe.cs" };\n' "$digest" > "$sandbox/$MANIFEST_CS"

  check_not_hand_edited "$sandbox" > /dev/null; status=$?
  if [ "$status" -ne 0 ]; then
    printf 'MVP_HOST_GENERATED_SELFTEST_FAIL 未篡改的对照组本应通过，实际退出 %d\n' "$status"
    return 1
  fi
  printf 'SELFTEST 对照组（未篡改）→ 退出 0\n'

  printf '// tampered\n' >> "$sandbox/$GENERATED_DIR/Lumio.Gen.Probe/Probe.cs"
  check_not_hand_edited "$sandbox" > /dev/null; status=$?
  if [ "$status" -ne "$DRIFT_EXIT" ]; then
    printf 'MVP_HOST_GENERATED_SELFTEST_FAIL 篡改后本应退出 %d，实际 %d —— 守护是空转的\n' "$DRIFT_EXIT" "$status"
    return 1
  fi
  printf 'SELFTEST 实验组（篡改一个字节）→ 退出 %d\n' "$DRIFT_EXIT"

  rm -f "$sandbox/$GENERATED_DIR/Lumio.Gen.Probe/Probe.cs"
  printf 'ArtifactHashes = new[] { "%s  ../outside.cs" };\n' "$digest" > "$sandbox/$MANIFEST_CS"
  check_not_hand_edited "$sandbox" > /dev/null; status=$?
  if [ "$status" -ne "$DRIFT_EXIT" ]; then
    printf 'MVP_HOST_GENERATED_SELFTEST_FAIL 越界路径本应退出 %d，实际 %d\n' "$DRIFT_EXIT" "$status"
    return 1
  fi
  printf 'SELFTEST 实验组（manifest 路径越界）→ 退出 %d\n' "$DRIFT_EXIT"

  printf 'MVP_HOST_GENERATED_SELFTEST_OK\n'
  return 0
}

if [ "${1:-}" = "--self-test" ]; then
  self_test
  exit $?
fi

check_not_hand_edited "$MVP_HOST_DIR" || exit $?
report_upstream_sync
