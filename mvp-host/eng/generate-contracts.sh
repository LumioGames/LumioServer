#!/usr/bin/env bash
# 从架构源把 packages/csharp/ 的生成 artifact **源码**重新拷进
# src/Lumio.Server.MvpHost.GeneratedContracts/Generated/，并重写 GeneratedContractManifest.cs。
#
# 只拷 .cs，**不拷 .csproj**：架构源工程原样是 net8.0，本构建根的 Directory.Build.targets
# 对每个 SDK 工程硬断言 net10.0，工程引用进不来（实测输出见 contract-mirror/MIRROR.md）。
# 拷进来的 .cs 随本工程以 net10.0 编译，靠 Generated/.editorconfig 的 generated_code = true
# 规避 TreatWarningsAsErrors + 分析器。
#
# 跨仓一律只读已提交对象（git show <ref>:<path>），绝不读他仓工作区。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MVP_HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=eng/GateHelpers.sh
source "$SCRIPT_DIR/GateHelpers.sh"
cd "$MVP_HOST_DIR" || exit 1

PROJECT_DIR=src/Lumio.Server.MvpHost.GeneratedContracts
GENERATED_DIR="$PROJECT_DIR/Generated"
MANIFEST_CS="$PROJECT_DIR/GeneratedContractManifest.cs"
ARCH_ROOT="${LUMIO_ARCHITECTURE_ROOT:-}"
ARCH_REF="${LUMIO_ARCHITECTURE_REF:-origin/main}"

if [ -z "$ARCH_ROOT" ] || [ ! -d "$ARCH_ROOT/.git" ]; then
  echo 'MVP_HOST_GENERATE_FAIL 未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库。' >&2
  exit 1
fi

ARCH_COMMIT="$(git -C "$ARCH_ROOT" rev-parse "$ARCH_REF" 2>/dev/null)"
if [ -z "$ARCH_COMMIT" ]; then
  echo "MVP_HOST_GENERATE_FAIL 架构源解析不出 $ARCH_REF" >&2
  exit 1
fi

BASELINE_ID="$(git -C "$ARCH_ROOT" show "$ARCH_COMMIT:packages/csharp/Lumio.Gen.ContractTypes/artifact.descriptor.json" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["baselineId"])')"
SCHEMA_EPOCH="$(git -C "$ARCH_ROOT" show "$ARCH_COMMIT:packages/csharp/Lumio.Gen.ContractTypes/artifact.descriptor.json" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["schemaEpoch"])')"

if [ -z "$BASELINE_ID" ] || [ -z "$SCHEMA_EPOCH" ]; then
  echo 'MVP_HOST_GENERATE_FAIL 读不出 baselineId / schemaEpoch' >&2
  exit 1
fi

# 全量重建：上游删掉一个 artifact 时，本地残留必须一起消失，否则镜像会比上游多出文件。
rm -rf "$GENERATED_DIR"
mkdir -p "$GENERATED_DIR"

cat > "$GENERATED_DIR/.editorconfig" <<'EDITORCONFIG'
# Generated/ 下的每个 .cs 都是架构源 packages/csharp/ 的逐字节拷贝，不得手改。
# 本文件由 bash eng/generate-contracts.sh 与 Generated/ 一起重建。
#
# 例外一律**目录级**、不写工程级 NoWarn：工程级 NoWarn 会把例外面扩大到本工程未来
# 可能新增的任何手写文件上（R-00270 的 RS0030 教训——「有一份看起来在守护的东西」
# 必须证明它真的会响，而放宽面越大越难证明）。这里的两条降级只对 Generated/ 生效。

[*.cs]

# ① 把本目录视作生成代码，分析器的风格类诊断因此降级。
#    没有这条，架构源按 net8.0 生成的代码会在本构建根的
#    TreatWarningsAsErrors + latest-recommended 下因风格诊断整片变红，
#    而这些代码本仓无权修改（改了就不再是镜像）。
generated_code = true

# ② CS8669：生成代码里出现 nullable 注解时，编译器要求文件内有显式 #nullable 指令。
#    架构源的 ContractBodies.cs / ProtocolGate.cs 用了 `string?`，而文件里没有该指令
#    （它们在架构源自己的构建里不被视作生成代码，因此不需要）。①一旦开启，
#    这条就必然触发——两者是同一枚硬币的两面。本工程 Nullable=enable，
#    注解语义明确，缺的只是那行指令，而指令不能由本仓补（补了就不是逐字节镜像）。
dotnet_diagnostic.CS8669.severity = none
EDITORCONFIG

count=0
hash_lines=""
sources="$(git -C "$ARCH_ROOT" ls-tree -r --name-only "$ARCH_COMMIT" -- packages/csharp \
  | awk '/[.]cs$/ { print }' | sort)"
source_enumeration_status=$?
if [ "$source_enumeration_status" -ne 0 ]; then
  echo "MVP_HOST_GENERATE_FAIL 架构源 packages/csharp 枚举失败（exit $source_enumeration_status）" >&2
  exit 1
fi
while IFS= read -r src; do
  [ -n "$src" ] || continue
  pkg="$(basename "$(dirname "$src")")"
  base="$(basename "$src")"
  mkdir -p "$GENERATED_DIR/$pkg"
  git -C "$ARCH_ROOT" show "$ARCH_COMMIT:$src" > "$GENERATED_DIR/$pkg/$base" || exit 1
  digest="$(gate_sha256_file "$GENERATED_DIR/$pkg/$base")" || exit 1
  hash_lines="${hash_lines}            \"${digest}  ${pkg}/${base}\",
"
  count=$((count + 1))
done <<< "$sources"

if [ "$count" -eq 0 ]; then
  echo 'MVP_HOST_GENERATE_FAIL 架构源 packages/csharp 下一个 .cs 都没有' >&2
  exit 1
fi

cat > "$MANIFEST_CS" <<MANIFEST
using System.Collections.Generic;

namespace Lumio.Server.MvpHost.GeneratedContracts
{
    /// <summary>
    /// 拷进 <c>Generated/</c> 的架构源生成物的来源与指纹。
    ///
    /// **本文件由 <c>bash eng/generate-contracts.sh</c> 生成，不得手改。**
    /// 手改它等于伪造镜像的来源声明——而来源声明正是
    /// <c>bash eng/verify-generated-contracts.sh</c> 在架构源不可达时唯一能比对的东西。
    /// </summary>
    public static class GeneratedContractManifest
    {
        /// <summary>架构源声明的基线号，取自 <c>packages/csharp/Lumio.Gen.ContractTypes/artifact.descriptor.json</c>。</summary>
        public static string ArchitectureBaselineId => "$BASELINE_ID";

        /// <summary>拷贝所依据的架构源提交。跨仓引用只认已推送对象，工作区状态一律不采信。</summary>
        public static string ArchitectureCommit => "$ARCH_COMMIT";

        /// <summary>架构源声明的 schema 世代。</summary>
        public static int SchemaEpoch => $SCHEMA_EPOCH;

        /// <summary>
        /// <c>Generated/</c> 下每个 <c>.cs</c> 的 <c>sha256  相对路径</c>，格式与
        /// <c>shasum -a 256</c> 一致（两空格分隔），路径相对 <c>Generated/</c>。
        /// </summary>
        public static IReadOnlyList<string> ArtifactHashes { get; } = new[]
        {
$(printf '%s' "$hash_lines")        };
    }
}
MANIFEST

printf 'MVP_HOST_GENERATE_OK files=%d arch=%s baseline=%s schemaEpoch=%s\n' \
  "$count" "$ARCH_COMMIT" "$BASELINE_ID" "$SCHEMA_EPOCH"
