#!/usr/bin/env pwsh
# verify-all.sh 的 Windows 对应物；步骤顺序与哨兵行必须逐条一致。
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MvpHostDir = (Resolve-Path (Join-Path $ScriptDir '..')).Path
$PowerShellExe = (Get-Process -Id $PID).Path
. (Join-Path $ScriptDir 'GitBlob.ps1')

# global.json 只按 cwd 向上查找，必须先 cd。
Set-Location $MvpHostDir

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    $global:LASTEXITCODE = 0
    try {
        & $Action
    }
    catch {
        Write-Output "MVP_HOST_VERIFY_FAIL $Name"
        Write-Error $_
        exit 1
    }

    if ($LASTEXITCODE -ne 0) {
        $exitCode = $LASTEXITCODE
        Write-Output "MVP_HOST_VERIFY_FAIL $Name"
        exit $exitCode
    }
}

Invoke-Step 'isolation' { & $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir 'verify-isolation.ps1') }
Invoke-Step 'sdk' { & $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir 'verify-sdk.ps1') }
Invoke-Step 'gate-portability' { & $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir 'verify-gate-portability.ps1') }

# 契约面的两道锁排在 restore 之前：镜像或生成物被手改时，后面的构建与测试跑出来的
# 「绿」是对着被篡改的契约算的，越早拦下越好。两者都不需要架构源在手。
Invoke-Step 'contract-mirror' { & $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir 'verify-contract-mirror.ps1') }
Invoke-Step 'generated-contracts' { & $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptDir 'verify-generated-contracts.ps1') }

Invoke-Step 'restore' { & dotnet restore build.proj --locked-mode --disable-parallel }

$sourceProjects = @()
try {
    foreach ($dir in @('src', 'tests', 'testkit')) {
        if (Test-Path -LiteralPath $dir) {
            $sourceProjects += Get-RecursiveFilesChecked -Path $dir -Filter '*.csproj'
        }
    }
    $sourceProjects = @($sourceProjects | Sort-Object FullName)
}
catch {
    Write-Output 'MVP_HOST_VERIFY_FAIL project-enumeration'
    Write-Error $_
    exit 1
}
foreach ($proj in $sourceProjects) {
    Invoke-Step "format $($proj.FullName)" { & dotnet format $proj.FullName --verify-no-changes --no-restore }
}

Invoke-Step 'build' { & dotnet build build.proj -c Release --no-restore }

if (Test-Path 'tests') {
    try {
        $testProjects = Get-RecursiveFilesChecked -Path 'tests' -Filter '*.csproj' |
            Where-Object { -not $_.Name.EndsWith('.Integration.Tests.csproj') } |
            Sort-Object FullName
    }
    catch {
        Write-Output 'MVP_HOST_VERIFY_FAIL test-enumeration'
        Write-Error $_
        exit 1
    }
    foreach ($proj in $testProjects) {
        Invoke-Step "test $($proj.FullName)" { & dotnet test $proj.FullName -c Release --no-build }
    }
}

Write-Output 'MVP_HOST_VERIFY_OK'
