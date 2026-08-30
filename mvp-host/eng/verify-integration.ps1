#!/usr/bin/env pwsh
$ErrorActionPreference = 'Continue'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MvpHostDir = (Resolve-Path (Join-Path $ScriptDir '..')).Path
Set-Location $MvpHostDir

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Output "MVP_HOST_INTEGRATION_FAIL $Name"
        exit $LASTEXITCODE
    }
}

Invoke-Step 'build' { & dotnet build build.proj -c Release }

$integrationProject = Join-Path $MvpHostDir 'tests/Lumio.Server.MvpHost.Integration.Tests/Lumio.Server.MvpHost.Integration.Tests.csproj'
if (-not (Test-Path $integrationProject)) {
    Write-Output 'MVP_HOST_INTEGRATION_FAIL missing-integration-project'
    exit 2
}

Invoke-Step 'test' { & dotnet test $integrationProject -c Release --no-build }

Write-Output 'MVP_HOST_INTEGRATION_OK'
