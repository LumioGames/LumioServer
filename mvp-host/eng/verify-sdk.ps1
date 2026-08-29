#!/usr/bin/env pwsh
# verify-sdk.sh 的 Windows 对应物；判据同为「前缀 10.0. + major.minor 一致」，不锁补丁号。
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MvpHostDir = (Resolve-Path (Join-Path $ScriptDir '..')).Path

# global.json 只按 cwd 向上查找，必须先 cd。
Set-Location $MvpHostDir

$ExpectedBand = '10.0.'

$sdkVersion = (& dotnet --version 2>$null | Select-Object -First 1)
# 先按期望族过滤再取族内最高：取全机最高会让一台同时装了 .NET 11 预览的开发机
# 直接 SDK_MISMATCH，尽管匹配的 10.0 runtime 就在机器上。
$runtimeVersion = (& dotnet --list-runtimes 2>$null |
    Where-Object { $_ -match '^Microsoft\.NETCore\.App\s' } |
    ForEach-Object { ($_ -split '\s+')[1] } |
    Where-Object { $_.StartsWith($ExpectedBand) } |
    Sort-Object { [version]($_ -replace '-.*$', '') } |
    Select-Object -Last 1)

if ([string]::IsNullOrWhiteSpace($sdkVersion) -or [string]::IsNullOrWhiteSpace($runtimeVersion)) {
    Write-Output "SDK_MISMATCH expected=sdk $ExpectedBand* / Microsoft.NETCore.App $ExpectedBand* actual=sdk $sdkVersion / runtime $runtimeVersion"
    exit 1
}

function Get-MajorMinor([string]$v) { ($v -split '\.')[0..1] -join '.' }

$mismatch = $false
if (-not $sdkVersion.StartsWith($ExpectedBand)) { $mismatch = $true }
if (-not $runtimeVersion.StartsWith($ExpectedBand)) { $mismatch = $true }
if ((Get-MajorMinor $sdkVersion) -ne (Get-MajorMinor $runtimeVersion)) { $mismatch = $true }

if ($mismatch) {
    Write-Output "SDK_MISMATCH expected=sdk $ExpectedBand* / Microsoft.NETCore.App $ExpectedBand* / same major.minor actual=sdk $sdkVersion / runtime $runtimeVersion"
    exit 1
}

Write-Output "SDK_OK sdk=$sdkVersion runtime=$runtimeVersion"
