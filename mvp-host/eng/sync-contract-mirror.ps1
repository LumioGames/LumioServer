#!/usr/bin/env pwsh
# eng/sync-contract-mirror.sh 的 Windows 对应物。行为、输出前缀与退出码逐条对齐；
# 设计理由（为什么清单的路径列是真值、源路径怎么推导）见 .sh 与 contract-mirror/MIRROR.md。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GitBlob.ps1')

$mvpHostDir = Split-Path -Parent $PSScriptRoot
Set-Location $mvpHostDir

$manifest = 'eng/contract-mirror.sha256'
$archRoot = $env:LUMIO_ARCHITECTURE_ROOT
$archRef = if ($env:LUMIO_ARCHITECTURE_REF) { $env:LUMIO_ARCHITECTURE_REF } else { 'origin/main' }

if ([string]::IsNullOrWhiteSpace($archRoot) -or -not (Test-Path (Join-Path $archRoot '.git'))) {
    Say 'MVP_HOST_MIRROR_SYNC_FAIL 未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库。'
    exit 1
}
if (-not (Test-Path $manifest)) {
    Say "MVP_HOST_MIRROR_SYNC_FAIL 清单不存在：$manifest"
    exit 1
}

$archCommit = (& git -C $archRoot rev-parse $archRef 2>$null)
if ([string]::IsNullOrWhiteSpace($archCommit)) {
    Say "MVP_HOST_MIRROR_SYNC_FAIL 架构源解析不出 $archRef"
    exit 1
}

function Get-SourcePath([string]$mirroredPath) {
    $rel = $mirroredPath -replace '^contract-mirror/', ''
    if ($rel -like 'canonical/*') { return "packages/$rel" }
    return $rel
}

$paths = @(Get-Content $manifest -Encoding UTF8 |
    Where-Object { $_ -notmatch '^\s*#' -and $_ -notmatch '^\s*$' } |
    ForEach-Object { ($_ -split '\s+', 2)[1].Trim() })

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# 架构源镜像的 sha256 锁。本文件与 contract-mirror/ 一律不得手改，')
$lines.Add('# 只能经 bash eng/sync-contract-mirror.sh 更新，并与镜像文件一起提交。')
$lines.Add("# 来源：$archRef @ $archCommit（$(Split-Path -Leaf $archRoot)）")

foreach ($path in $paths) {
    if (-not $path.StartsWith('contract-mirror/', [System.StringComparison]::Ordinal)) {
        Say "MVP_HOST_MIRROR_SYNC_FAIL 非法镜像路径：$path"
        exit 1
    }
    try {
        $targetPath = Resolve-ContainedPathPortable -BasePath $mvpHostDir -RelativePath $path
    }
    catch {
        Say "MVP_HOST_MIRROR_SYNC_FAIL 非法镜像路径：$path"
        exit 1
    }

    $src = Get-SourcePath $path
    try {
        $bytes = Get-GitBlobBytes -RepoRoot $archRoot -ObjectSpec "${archCommit}:${src}"
    }
    catch {
        Say "MVP_HOST_MIRROR_SYNC_FAIL 架构源 $archCommit 下没有 $src（镜像项 $path）"
        exit 1
    }

    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    [System.IO.File]::WriteAllBytes($targetPath, $bytes)
    $lines.Add("$(Get-Sha256Hex -Bytes $bytes)  $path")
}

# 清单本身用 LF 写死：它同时被 bash 与 pwsh 读，行尾在两侧必须一致，
# 否则同一份镜像在 Windows 上算出的清单与 Linux/macOS 上的逐行不等。
[System.IO.File]::WriteAllText(
    (Join-Path $mvpHostDir $manifest),
    (($lines -join "`n") + "`n"),
    (New-Object System.Text.UTF8Encoding $false))

Say "MVP_HOST_MIRROR_SYNC_OK files=$($paths.Count) arch=$archCommit"
