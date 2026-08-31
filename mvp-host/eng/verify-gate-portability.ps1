#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GitBlob.ps1')

$mvpHostDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $mvpHostDir '..')).Path

$quoted = ConvertTo-WindowsCommandLineArgument -Argument 'C:\Repo With Space\'
if ($quoted -ne '"C:\Repo With Space\\"') {
    throw "Windows native argument quoting is invalid: $quoted"
}
if ((ConvertTo-WindowsCommandLineArgument -Argument 'a"b') -ne '"a\"b"' -or
    (ConvertTo-WindowsCommandLineArgument -Argument '') -ne '""') {
    throw 'Windows native argument quoting did not preserve quotes or an empty argument.'
}

$blob = Get-GitBlobBytes -RepoRoot $repoRoot -ObjectSpec 'HEAD:README.md'
$expectedLength = & git -C $repoRoot cat-file -s 'HEAD:README.md'
if ($LASTEXITCODE -ne 0 -or $blob.Length -ne [int64]$expectedLength) {
    throw "Git blob helper length mismatch: expected $expectedLength, actual $($blob.Length)."
}

$inside = Resolve-ContainedPathPortable -BasePath $mvpHostDir -RelativePath 'eng/verify-all.ps1'
if (-not $inside.EndsWith('eng\verify-all.ps1', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Contained path resolved unexpectedly: $inside"
}

foreach ($invalidPath in @('../README.md', '/tmp/file', 'C:/outside', 'eng\file', 'eng//file', './eng/file')) {
    $outsideRejected = $false
    try {
        $null = Resolve-ContainedPathPortable -BasePath $mvpHostDir -RelativePath $invalidPath
    }
    catch {
        $outsideRejected = $true
    }
    if (-not $outsideRejected) {
        throw "Path containment helper accepted invalid path '$invalidPath'."
    }
}

$enumerationFailed = $false
try {
    $missing = Join-Path $mvpHostDir "missing-$([System.Guid]::NewGuid().ToString('N'))"
    $null = @(Get-RecursiveFilesChecked -Path $missing)
}
catch {
    $enumerationFailed = $true
}
if (-not $enumerationFailed) {
    throw 'Checked recursive enumeration swallowed a missing-root failure.'
}

Write-Output 'MVP_HOST_GATE_PORTABILITY_OK'
