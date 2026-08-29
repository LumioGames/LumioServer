#!/usr/bin/env bash
# contract-mirror/ 的守护。**两条互相独立的检查，故意不混为一谈：**
#
#   ① 产物未被手改 —— 只比对本地文件与 eng/contract-mirror.sha256。不需要架构源，
#      任何机器任何时刻都可判，漂移即以退出码 33 硬失败。这是门禁。
#   ② 与上游同步   —— 需要 $LUMIO_ARCHITECTURE_ROOT。上游 additive 增补是被鼓励的，
#      落后于上游不是本仓的错误状态，因此这条**只报告、不影响退出码**。
#
# 把两者合成一条会让「有人手改了镜像」与「上游又发了一版」共用一个红灯，
# 前者是必须拦下的事故，后者是日常——共用红灯的结果是红灯被无视。
#
# 守护本身要能被证伪：`bash eng/verify-contract-mirror.sh --self-test` 在临时目录里
# 造一份镜像、篡改一个字节，确认检查①确实返回 33，再确认原样返回 0（对照组探针）。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$MVP_HOST_DIR" || exit 1

DRIFT_EXIT=33
MANIFEST=eng/contract-mirror.sha256

# 检查①：把 <manifest> 逐行对着 <root> 下的文件比对。漂移逐条打印路径。
check_not_hand_edited() {
  local manifest="$1" root="$2"
  local drift=0 count=0

  while IFS= read -r line; do
    case "$line" in ''|'#'*) continue ;; esac
    local expected path actual
    expected="${line%% *}"
    path="${line#* }"; path="${path# }"
    count=$((count + 1))

    if [ ! -f "$root/$path" ]; then
      printf 'MVP_HOST_MIRROR_DRIFT missing %s\n' "$path"
      drift=$((drift + 1))
      continue
    fi

    actual="$(shasum -a 256 "$root/$path" | cut -d' ' -f1)"
    if [ "$actual" != "$expected" ]; then
      printf 'MVP_HOST_MIRROR_DRIFT modified %s (清单 %s != 实际 %s)\n' "$path" "$expected" "$actual"
      drift=$((drift + 1))
    fi
  done < "$root/$manifest"

  # 清单外的文件同样是漂移：白名单只有 MIRROR.md，它是本仓手写的说明，
  # 架构源没有对应文件，进清单会与「与架构源字节相同」互斥。
  local registered
  registered="$(grep -v '^#' "$root/$manifest" | grep -v '^[[:space:]]*$' | sed 's/^[^ ]*  *//')"
  while IFS= read -r found; do
    [ -n "$found" ] || continue
    case "$found" in contract-mirror/MIRROR.md) continue ;; esac
    if ! printf '%s\n' "$registered" | grep -Fxq "$found"; then
      printf 'MVP_HOST_MIRROR_DRIFT unregistered %s\n' "$found"
      drift=$((drift + 1))
    fi
  done < <(cd "$root" && find contract-mirror -type f 2>/dev/null | sort)

  if [ "$drift" -gt 0 ]; then
    printf 'MVP_HOST_MIRROR_FAIL drift=%d\n' "$drift"
    return "$DRIFT_EXIT"
  fi

  printf 'MVP_HOST_MIRROR_OK files=%d\n' "$count"
  return 0
}

# 检查②：只报告。落后于上游不是错误状态。
report_upstream_sync() {
  local arch_root="${LUMIO_ARCHITECTURE_ROOT:-}"
  if [ -z "$arch_root" ] || [ ! -d "$arch_root/.git" ]; then
    printf 'MVP_HOST_MIRROR_UPSTREAM skipped（未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库）\n'
    return 0
  fi

  local arch_ref arch_commit ahead=0
  arch_ref="${LUMIO_ARCHITECTURE_REF:-origin/main}"
  arch_commit="$(git -C "$arch_root" rev-parse "$arch_ref" 2>/dev/null)"
  if [ -z "$arch_commit" ]; then
    printf 'MVP_HOST_MIRROR_UPSTREAM skipped（架构源解析不出 %s）\n' "$arch_ref"
    return 0
  fi

  while IFS= read -r line; do
    case "$line" in ''|'#'*) continue ;; esac
    local expected path mirrored src upstream
    expected="${line%% *}"
    path="${line#* }"; path="${path# }"
    mirrored="${path#contract-mirror/}"
    case "$mirrored" in
      canonical/*) src="packages/$mirrored" ;;
      *)           src="$mirrored" ;;
    esac

    upstream="$(git -C "$arch_root" show "$arch_commit:$src" 2>/dev/null | shasum -a 256 | cut -d' ' -f1)"
    if [ -z "$upstream" ]; then
      printf 'MVP_HOST_MIRROR_UPSTREAM gone %s（架构源 %s 下已无 %s）\n' "$path" "$arch_commit" "$src"
      ahead=$((ahead + 1))
    elif [ "$upstream" != "$expected" ]; then
      printf 'MVP_HOST_MIRROR_UPSTREAM behind %s（上游 %s）\n' "$path" "$upstream"
      ahead=$((ahead + 1))
    fi
  done < "$MANIFEST"

  if [ "$ahead" -gt 0 ]; then
    printf 'MVP_HOST_MIRROR_UPSTREAM behind=%d arch=%s —— 报告性质，不影响退出码；同步用 bash eng/sync-contract-mirror.sh\n' "$ahead" "$arch_commit"
  else
    printf 'MVP_HOST_MIRROR_UPSTREAM in-sync arch=%s\n' "$arch_commit"
  fi
  return 0
}

# 对照组探针：证明检查①真的会响，而不是「看起来在守护」。
self_test() {
  local sandbox status
  sandbox="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$sandbox'" EXIT

  mkdir -p "$sandbox/contract-mirror" "$sandbox/eng"
  printf 'probe\n' > "$sandbox/contract-mirror/probe.json"
  printf 'MIRROR.md 是白名单项，不进清单。\n' > "$sandbox/contract-mirror/MIRROR.md"
  (cd "$sandbox" && shasum -a 256 contract-mirror/probe.json > eng/contract-mirror.sha256)

  check_not_hand_edited "$MANIFEST" "$sandbox" > /dev/null; status=$?
  if [ "$status" -ne 0 ]; then
    printf 'MVP_HOST_MIRROR_SELFTEST_FAIL 未篡改的对照组本应通过，实际退出 %d\n' "$status"
    return 1
  fi
  printf 'SELFTEST 对照组（未篡改）→ 退出 0\n'

  printf 'tampered' >> "$sandbox/contract-mirror/probe.json"
  check_not_hand_edited "$MANIFEST" "$sandbox" > /dev/null; status=$?
  if [ "$status" -ne "$DRIFT_EXIT" ]; then
    printf 'MVP_HOST_MIRROR_SELFTEST_FAIL 篡改后本应退出 %d，实际 %d —— 守护是空转的\n' "$DRIFT_EXIT" "$status"
    return 1
  fi
  printf 'SELFTEST 实验组（篡改一个字节）→ 退出 %d\n' "$DRIFT_EXIT"

  printf 'probe\n' > "$sandbox/contract-mirror/probe.json"
  printf 'unregistered\n' > "$sandbox/contract-mirror/sneaked-in.json"
  check_not_hand_edited "$MANIFEST" "$sandbox" > /dev/null; status=$?
  if [ "$status" -ne "$DRIFT_EXIT" ]; then
    printf 'MVP_HOST_MIRROR_SELFTEST_FAIL 清单外文件本应退出 %d，实际 %d\n' "$DRIFT_EXIT" "$status"
    return 1
  fi
  printf 'SELFTEST 实验组（清单外新增文件）→ 退出 %d\n' "$DRIFT_EXIT"

  printf 'MVP_HOST_MIRROR_SELFTEST_OK\n'
  return 0
}

if [ "${1:-}" = "--self-test" ]; then
  self_test
  exit $?
fi

if [ ! -f "$MANIFEST" ]; then
  printf 'MVP_HOST_MIRROR_FAIL 清单不存在：%s\n' "$MANIFEST"
  exit "$DRIFT_EXIT"
fi

check_not_hand_edited "$MANIFEST" "$MVP_HOST_DIR" || exit $?
report_upstream_sync
