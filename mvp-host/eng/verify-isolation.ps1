#!/usr/bin/env pwsh
# verify-isolation.sh 的 Windows 对应物；三条断言与退出码口径必须逐条一致。
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MvpHostDir = (Resolve-Path (Join-Path $ScriptDir '..')).Path
$RepoRoot = (Resolve-Path (Join-Path $MvpHostDir '..')).Path

$violations = 0
function Report([string]$Path) {
    Write-Output "MVP_HOST_ISOLATION_VIOLATION $Path"
    $script:violations++
}

# ① 仓库根不得出现 C# 构建根文件。
foreach ($f in @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.config')) {
    if (Test-Path (Join-Path $RepoRoot $f)) { Report $f }
}

# ② Rust 侧的七个仓根目录（存在时）不得出现 C# 源码或工程。
foreach ($d in @('modules', 'crates', 'tools', 'benches', 'contracts', 'generated', 'tests')) {
    $dir = Join-Path $RepoRoot $d
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem -Path $dir -Recurse -File -Include '*.csproj', '*.cs', '*.slnx' -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object { Report ($_.FullName.Substring($RepoRoot.Length + 1) -replace '\\', '/') }
}

# ③ mvp-host/ 下不得出现 Rust 工程文件。
Get-ChildItem -Path $MvpHostDir -Recurse -File -Include '*.rs', 'Cargo.toml' -ErrorAction SilentlyContinue |
    Sort-Object FullName |
    ForEach-Object { Report ($_.FullName.Substring($RepoRoot.Length + 1) -replace '\\', '/') }

if ($violations -gt 0) {
    Write-Output "MVP_HOST_ISOLATION_FAIL violations=$violations"
    exit 34
}

Write-Output 'MVP_HOST_ISOLATION_OK'
