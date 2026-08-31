#!/usr/bin/env pwsh
# eng/generate-contracts.sh 的 Windows 对应物。只拷 .cs、不拷 .csproj，理由见 .sh 与
# contract-mirror/MIRROR.md（架构源工程原样 net8.0，本构建根硬断言 net10.0）。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GitBlob.ps1')

$mvpHostDir = Split-Path -Parent $PSScriptRoot
Set-Location $mvpHostDir

$projectDir = 'src/Lumio.Server.MvpHost.GeneratedContracts'
$generatedDir = Join-Path $projectDir 'Generated'
$manifestCs = Join-Path $projectDir 'GeneratedContractManifest.cs'

$archRoot = $env:LUMIO_ARCHITECTURE_ROOT
$archRef = if ($env:LUMIO_ARCHITECTURE_REF) { $env:LUMIO_ARCHITECTURE_REF } else { 'origin/main' }

if ([string]::IsNullOrWhiteSpace($archRoot) -or -not (Test-Path (Join-Path $archRoot '.git'))) {
    Say 'MVP_HOST_GENERATE_FAIL 未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库。'
    exit 1
}

$archCommit = (& git -C $archRoot rev-parse $archRef 2>$null)
if ([string]::IsNullOrWhiteSpace($archCommit)) {
    Say "MVP_HOST_GENERATE_FAIL 架构源解析不出 $archRef"
    exit 1
}

$descriptorJson = [System.Text.Encoding]::UTF8.GetString(
    (Get-GitBlobBytes -RepoRoot $archRoot -ObjectSpec "${archCommit}:packages/csharp/Lumio.Gen.ContractTypes/artifact.descriptor.json"))
$descriptor = $descriptorJson | ConvertFrom-Json
$baselineId = $descriptor.baselineId
$schemaEpoch = $descriptor.schemaEpoch

if ([string]::IsNullOrWhiteSpace($baselineId)) {
    Say 'MVP_HOST_GENERATE_FAIL 读不出 baselineId / schemaEpoch'
    exit 1
}

# 全量重建：上游删掉一个 artifact 时，本地残留必须一起消失。
if (Test-Path $generatedDir) { Remove-Item -LiteralPath $generatedDir -Recurse -Force }
New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null

$editorConfig = @'
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
'@
[System.IO.File]::WriteAllText(
    (Join-Path $mvpHostDir (Join-Path $generatedDir '.editorconfig')),
    ($editorConfig -replace "`r`n", "`n") + "`n",
    (New-Object System.Text.UTF8Encoding $false))

$sources = @(& git -C $archRoot ls-tree -r --name-only $archCommit -- packages/csharp |
    Where-Object { $_ -like '*.cs' } | Sort-Object)
$sourceEnumerationExit = $LASTEXITCODE

if ($sourceEnumerationExit -ne 0) {
    Say "MVP_HOST_GENERATE_FAIL 架构源 packages/csharp 枚举失败（exit $sourceEnumerationExit）"
    exit 1
}
if ($sources.Count -eq 0) {
    Say 'MVP_HOST_GENERATE_FAIL 架构源 packages/csharp 下一个 .cs 都没有'
    exit 1
}

$hashLines = [System.Collections.Generic.List[string]]::new()
foreach ($src in $sources) {
    $pkg = Split-Path -Leaf (Split-Path -Parent $src)
    $base = Split-Path -Leaf $src
    $targetDir = Join-Path $generatedDir $pkg
    if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

    $bytes = Get-GitBlobBytes -RepoRoot $archRoot -ObjectSpec "${archCommit}:${src}"
    [System.IO.File]::WriteAllBytes((Join-Path $mvpHostDir (Join-Path $targetDir $base)), $bytes)
    $hashLines.Add("            `"$(Get-Sha256Hex -Bytes $bytes)  $pkg/$base`",")
}

$manifestBody = @"
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
        public static string ArchitectureBaselineId => "$baselineId";

        /// <summary>拷贝所依据的架构源提交。跨仓引用只认已推送对象，工作区状态一律不采信。</summary>
        public static string ArchitectureCommit => "$archCommit";

        /// <summary>架构源声明的 schema 世代。</summary>
        public static int SchemaEpoch => $schemaEpoch;

        /// <summary>
        /// <c>Generated/</c> 下每个 <c>.cs</c> 的 <c>sha256  相对路径</c>，格式与
        /// <c>shasum -a 256</c> 一致（两空格分隔），路径相对 <c>Generated/</c>。
        /// </summary>
        public static IReadOnlyList<string> ArtifactHashes { get; } = new[]
        {
$($hashLines -join "`n")
        };
    }
}
"@

[System.IO.File]::WriteAllText(
    (Join-Path $mvpHostDir $manifestCs),
    ($manifestBody -replace "`r`n", "`n"),
    (New-Object System.Text.UTF8Encoding $false))

Say "MVP_HOST_GENERATE_OK files=$($sources.Count) arch=$archCommit baseline=$baselineId schemaEpoch=$schemaEpoch"
