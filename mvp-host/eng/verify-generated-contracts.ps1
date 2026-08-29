#!/usr/bin/env pwsh
# eng/verify-generated-contracts.sh 的 Windows 对应物。**两条互相独立的检查**：
#   ① 产物未被手改 —— 不需要架构源，漂移即退出码 32。这是门禁。
#   ② 与上游同步   —— 需要 $LUMIO_ARCHITECTURE_ROOT，只报告、不影响退出码。
# 对照组探针：pwsh eng/verify-generated-contracts.ps1 -SelfTest
param([switch]$SelfTest)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GitBlob.ps1')

$mvpHostDir = Split-Path -Parent $PSScriptRoot
Set-Location $mvpHostDir

$driftExit = 32
$projectDir = 'src/Lumio.Server.MvpHost.GeneratedContracts'
$generatedRelative = "$projectDir/Generated"
$manifestRelative = "$projectDir/GeneratedContractManifest.cs"

# 从 manifest 的 C# 数组字面量里取出 "<sha256>  <相对路径>" 行。
function Read-ManifestEntries([string]$manifestPath) {
    $text = [System.IO.File]::ReadAllText($manifestPath)
    foreach ($m in [regex]::Matches($text, '"([0-9a-f]{64})  ([^"]+)"')) {
        [pscustomobject]@{ Hash = $m.Groups[1].Value; Path = $m.Groups[2].Value }
    }
}

function Test-NotHandEdited([string]$Root) {
    $manifestPath = Join-Path $Root $manifestRelative
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        Say "MVP_HOST_GENERATED_DRIFT missing-manifest $manifestRelative"
        Say 'MVP_HOST_GENERATED_FAIL drift=1'
        return $driftExit
    }

    $entries = @(Read-ManifestEntries $manifestPath)
    if ($entries.Count -eq 0) {
        Say "MVP_HOST_GENERATED_DRIFT empty-manifest $manifestRelative"
        Say 'MVP_HOST_GENERATED_FAIL drift=1'
        return $driftExit
    }

    $generatedRoot = Join-Path $Root $generatedRelative
    $drift = 0

    foreach ($entry in $entries) {
        $full = Join-Path $generatedRoot $entry.Path
        if (-not (Test-Path -LiteralPath $full)) {
            Say "MVP_HOST_GENERATED_DRIFT missing $($entry.Path)"
            $drift++
            continue
        }

        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Hash) {
            Say "MVP_HOST_GENERATED_DRIFT modified $($entry.Path) (manifest $($entry.Hash) != 实际 $actual)"
            $drift++
        }
    }

    # manifest 之外的 .cs 同样是漂移。foreach 语句而非 ForEach-Object：后者的脚本块
    # 有自己的作用域，$drift++ 只改局部副本，计数永远归零。
    $registered = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]($entries | ForEach-Object { $_.Path }), [System.StringComparer]::Ordinal)
    if (Test-Path -LiteralPath $generatedRoot) {
        foreach ($file in (Get-ChildItem -LiteralPath $generatedRoot -Recurse -File -Filter '*.cs')) {
            $rel = [System.IO.Path]::GetRelativePath($generatedRoot, $file.FullName).Replace('\', '/')
            if (-not $registered.Contains($rel)) {
                Say "MVP_HOST_GENERATED_DRIFT unregistered $rel"
                $drift++
            }
        }
    }

    if ($drift -gt 0) {
        Say "MVP_HOST_GENERATED_FAIL drift=$drift"
        return $driftExit
    }

    Say "MVP_HOST_GENERATED_OK files=$($entries.Count)"
    return 0
}

function Write-UpstreamReport {
    $archRoot = $env:LUMIO_ARCHITECTURE_ROOT
    if ([string]::IsNullOrWhiteSpace($archRoot) -or -not (Test-Path (Join-Path $archRoot '.git'))) {
        Say 'MVP_HOST_GENERATED_UPSTREAM skipped（未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库）'
        return
    }

    $archRef = if ($env:LUMIO_ARCHITECTURE_REF) { $env:LUMIO_ARCHITECTURE_REF } else { 'origin/main' }
    $archCommit = (& git -C $archRoot rev-parse $archRef 2>$null)
    $manifestText = [System.IO.File]::ReadAllText((Join-Path $mvpHostDir $manifestRelative))
    $pinnedMatch = [regex]::Match($manifestText, 'ArchitectureCommit => "([0-9a-f]{40})"')

    if ([string]::IsNullOrWhiteSpace($archCommit) -or -not $pinnedMatch.Success) {
        Say "MVP_HOST_GENERATED_UPSTREAM skipped（解析不出 $archRef 或 manifest 里的 commit）"
        return
    }
    $pinned = $pinnedMatch.Groups[1].Value

    $drift = 0
    foreach ($entry in (Read-ManifestEntries (Join-Path $mvpHostDir $manifestRelative))) {
        try {
            $upstream = Get-Sha256Hex -Bytes (Get-GitBlobBytes -RepoRoot $archRoot `
                    -ObjectSpec "${archCommit}:packages/csharp/$($entry.Path)")
        }
        catch {
            Say "MVP_HOST_GENERATED_UPSTREAM gone packages/csharp/$($entry.Path)"
            $drift++
            continue
        }
        if ($upstream -ne $entry.Hash) {
            Say "MVP_HOST_GENERATED_UPSTREAM behind $($entry.Path)（上游 $upstream）"
            $drift++
        }
    }

    if ($pinned -ne $archCommit) {
        Say "MVP_HOST_GENERATED_UPSTREAM pinned=$pinned ref=$archCommit —— commit 已前进"
    }

    if ($drift -gt 0) {
        Say "MVP_HOST_GENERATED_UPSTREAM behind=$drift —— 报告性质，不影响退出码；同步用 pwsh eng/generate-contracts.ps1"
    }
    else {
        Say "MVP_HOST_GENERATED_UPSTREAM in-sync arch=$archCommit"
    }
}

function Invoke-SelfTest {
    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    try {
        $probeDir = Join-Path $sandbox "$generatedRelative/Lumio.Gen.Probe"
        New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $sandbox $projectDir) -Force | Out-Null

        $probe = Join-Path $probeDir 'Probe.cs'
        [System.IO.File]::WriteAllText($probe, "namespace Probe { }`n", (New-Object System.Text.UTF8Encoding $false))
        $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $probe).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText((Join-Path $sandbox $manifestRelative),
            "ArtifactHashes = new[] { `"$digest  Lumio.Gen.Probe/Probe.cs`" };`n",
            (New-Object System.Text.UTF8Encoding $false))

        $status = Test-NotHandEdited $sandbox | Select-Object -Last 1
        if ($status -ne 0) {
            Say "MVP_HOST_GENERATED_SELFTEST_FAIL 未篡改的对照组本应通过，实际退出 $status"
            return 1
        }
        Say 'SELFTEST 对照组（未篡改）→ 退出 0'

        [System.IO.File]::AppendAllText($probe, "// tampered`n")
        $status = Test-NotHandEdited $sandbox | Select-Object -Last 1
        if ($status -ne $driftExit) {
            Say "MVP_HOST_GENERATED_SELFTEST_FAIL 篡改后本应退出 $driftExit，实际 $status —— 守护是空转的"
            return 1
        }
        Say "SELFTEST 实验组（篡改一个字节）→ 退出 $driftExit"

        Say 'MVP_HOST_GENERATED_SELFTEST_OK'
        return 0
    }
    finally {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    exit (Invoke-SelfTest | Select-Object -Last 1)
}

$status = Test-NotHandEdited $mvpHostDir | Select-Object -Last 1
if ($status -ne 0) { exit $status }
Write-UpstreamReport
