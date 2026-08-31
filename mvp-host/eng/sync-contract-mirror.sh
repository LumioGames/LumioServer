#!/usr/bin/env bash
# 从架构源重新同步 contract-mirror/ 并重写 eng/contract-mirror.sha256。
#
# 「要镜像哪些文件」的真值是 eng/contract-mirror.sha256 的**路径列**——脚本不自己发明清单，
# 只按清单逐条重拷。新增一项镜像：先在清单里加一行
#     0000000000000000000000000000000000000000000000000000000000000000  contract-mirror/<路径>
# 再跑本脚本，哈希由脚本填实。删除一项：删掉那一行再跑本脚本（文件同时被删）。
#
# 源路径由镜像路径推导：contract-mirror/canonical/X ← packages/canonical/X，
# 其余 contract-mirror/Y ← Y（schemas/… 与 fixtures/… 与架构源同构）。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=eng/GateHelpers.sh
source "$SCRIPT_DIR/GateHelpers.sh"
cd "$MVP_HOST_DIR" || exit 1

MANIFEST=eng/contract-mirror.sha256
ARCH_ROOT="${LUMIO_ARCHITECTURE_ROOT:-}"

if [ -z "$ARCH_ROOT" ]; then
  echo 'MVP_HOST_MIRROR_SYNC_FAIL 未设置 $LUMIO_ARCHITECTURE_ROOT；同步必须有架构源在手。' >&2
  exit 1
fi
if [ ! -d "$ARCH_ROOT/.git" ]; then
  echo "MVP_HOST_MIRROR_SYNC_FAIL \$LUMIO_ARCHITECTURE_ROOT 不是 git 仓库：$ARCH_ROOT" >&2
  exit 1
fi
if [ ! -f "$MANIFEST" ]; then
  echo "MVP_HOST_MIRROR_SYNC_FAIL 清单不存在：$MANIFEST" >&2
  exit 1
fi

# 跨仓一律**只读已提交对象**（git show origin/main:<path>），绝不读他仓工作区——
# 他仓的 HEAD 可能正被另一个会话切换，读工作区会读到半截状态或误判文件不存在。
ARCH_REF="${LUMIO_ARCHITECTURE_REF:-origin/main}"
ARCH_COMMIT="$(git -C "$ARCH_ROOT" rev-parse "$ARCH_REF" 2>/dev/null)"
if [ -z "$ARCH_COMMIT" ]; then
  echo "MVP_HOST_MIRROR_SYNC_FAIL 架构源解析不出 $ARCH_REF" >&2
  exit 1
fi

source_path_of() {
  local mirrored="${1#contract-mirror/}"
  case "$mirrored" in
    canonical/*) printf 'packages/%s\n' "$mirrored" ;;
    *)           printf '%s\n' "$mirrored" ;;
  esac
}

tmp_manifest="$(mktemp)"
trap 'rm -f "$tmp_manifest"' EXIT

{
  printf '# 架构源镜像的 sha256 锁。本文件与 contract-mirror/ 一律不得手改，\n'
  printf '# 只能经 bash eng/sync-contract-mirror.sh 更新，并与镜像文件一起提交。\n'
  printf '# 来源：%s @ %s（%s）\n' "$ARCH_REF" "$ARCH_COMMIT" "$(basename "$ARCH_ROOT")"
} > "$tmp_manifest"

count=0
while IFS= read -r line; do
  case "$line" in ''|'#'*) continue ;; esac
  path="${line#* }"; path="${path# }"
  case "$path" in
    contract-mirror/?*) ;;
    *)
      echo "MVP_HOST_MIRROR_SYNC_FAIL 非法镜像路径：$path" >&2
      exit 1
      ;;
  esac
  if ! gate_validate_relative_path "$path"; then
    echo "MVP_HOST_MIRROR_SYNC_FAIL 非法镜像路径：$path" >&2
    exit 1
  fi
  src="$(source_path_of "$path")"

  if ! git -C "$ARCH_ROOT" cat-file -e "$ARCH_COMMIT:$src" 2>/dev/null; then
    echo "MVP_HOST_MIRROR_SYNC_FAIL 架构源 $ARCH_COMMIT 下没有 $src（镜像项 $path）" >&2
    exit 1
  fi

  mkdir -p "$(dirname "$path")"
  git -C "$ARCH_ROOT" show "$ARCH_COMMIT:$src" > "$path" || exit 1
  digest="$(gate_sha256_file "$path")" || exit 1
  printf '%s  %s\n' "$digest" "$path" >> "$tmp_manifest"
  count=$((count + 1))
done < "$MANIFEST"

mv "$tmp_manifest" "$MANIFEST"
trap - EXIT

printf 'MVP_HOST_MIRROR_SYNC_OK files=%d arch=%s\n' "$count" "$ARCH_COMMIT"
