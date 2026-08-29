#!/usr/bin/env pwsh
# verify-all.sh 的 Windows 对应物；步骤顺序与哨兵行必须逐条一致。
$ErrorActionPreference = 'Continue'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MvpHostDir = (Resolve-Path (Join-Path $ScriptDir '..')).Path

# global.json 只按 cwd 向上查找，必须先 cd。
Set-Location $MvpHostDir

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Output "MVP_HOST_VERIFY_FAIL $Name"
        exit $LASTEXITCODE
    }
}

Invoke-Step 'isolation' { & pwsh -NoProfile -File (Join-Path $ScriptDir 'verify-isolation.ps1') }
Invoke-Step 'sdk' { & pwsh -NoProfile -File (Join-Path $ScriptDir 'verify-sdk.ps1') }

# 契约面的两道锁排在 restore 之前：镜像或生成物被手改时，后面的构建与测试跑出来的
# 「绿」是对着被篡改的契约算的，越早拦下越好。两者都不需要架构源在手。
Invoke-Step 'contract-mirror' { & pwsh -NoProfile -File (Join-Path $ScriptDir 'verify-contract-mirror.ps1') }
Invoke-Step 'generated-contracts' { & pwsh -NoProfile -File (Join-Path $ScriptDir 'verify-generated-contracts.ps1') }

Invoke-Step 'restore' { & dotnet restore build.proj --locked-mode }

$sourceProjects = @()
foreach ($dir in @('src', 'tests', 'testkit')) {
    if (Test-Path $dir) {
        $sourceProjects += Get-ChildItem -Path $dir -Recurse -File -Filter '*.csproj' | Sort-Object FullName
    }
}
foreach ($proj in $sourceProjects) {
    Invoke-Step "format $($proj.FullName)" { & dotnet format $proj.FullName --verify-no-changes --no-restore }
}

Invoke-Step 'build' { & dotnet build build.proj -c Release --no-restore }

if (Test-Path 'tests') {
    $testProjects = Get-ChildItem -Path 'tests' -Recurse -File -Filter '*.csproj' |
        Where-Object { -not $_.Name.EndsWith('.Integration.Tests.csproj') } |
        Sort-Object FullName
    foreach ($proj in $testProjects) {
        Invoke-Step "test $($proj.FullName)" { & dotnet test $proj.FullName -c Release --no-build }
    }
}

Write-Output 'MVP_HOST_VERIFY_OK'
