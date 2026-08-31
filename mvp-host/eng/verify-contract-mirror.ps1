#!/usr/bin/env pwsh
# eng/verify-contract-mirror.sh 的 Windows 对应物。**两条互相独立的检查**：
#   ① 产物未被手改 —— 不需要架构源，漂移即退出码 33。这是门禁。
#   ② 与上游同步   —— 需要 $LUMIO_ARCHITECTURE_ROOT，只报告、不影响退出码。
# 拆开的理由见 .sh 与 contract-mirror/MIRROR.md。
# 对照组探针：pwsh eng/verify-contract-mirror.ps1 -SelfTest
param([switch]$SelfTest)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GitBlob.ps1')

$mvpHostDir = Split-Path -Parent $PSScriptRoot
Set-Location $mvpHostDir

$driftExit = 33
$manifestRelative = 'eng/contract-mirror.sha256'

function Read-ManifestEntries([string]$manifestPath) {
    Get-Content -LiteralPath $manifestPath -Encoding UTF8 |
        Where-Object { $_ -notmatch '^\s*#' -and $_ -notmatch '^\s*$' } |
        ForEach-Object {
            $parts = $_ -split '\s+', 2
            [pscustomobject]@{ Hash = $parts[0].Trim(); Path = $parts[1].Trim() }
        }
}

function Test-NotHandEdited([string]$Root) {
    $manifestPath = Join-Path $Root $manifestRelative
    if (-not (Test-Path $manifestPath)) {
        Say "MVP_HOST_MIRROR_FAIL 清单不存在：$manifestRelative"
        return $driftExit
    }

    $entries = @(Read-ManifestEntries $manifestPath)
    $drift = 0

    if ($entries.Count -eq 0) {
        Say "MVP_HOST_MIRROR_DRIFT empty-manifest $manifestRelative"
        $drift++
    }

    foreach ($entry in $entries) {
        if (-not $entry.Path.StartsWith('contract-mirror/', [System.StringComparison]::Ordinal)) {
            Say "MVP_HOST_MIRROR_DRIFT invalid-path $($entry.Path)"
            $drift++
            continue
        }
        try {
            $full = Resolve-ContainedPathPortable -BasePath $Root -RelativePath $entry.Path
        }
        catch {
            Say "MVP_HOST_MIRROR_DRIFT invalid-path $($entry.Path)"
            $drift++
            continue
        }
        if (-not (Test-Path -LiteralPath $full)) {
            Say "MVP_HOST_MIRROR_DRIFT missing $($entry.Path)"
            $drift++
            continue
        }

        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Hash) {
            Say "MVP_HOST_MIRROR_DRIFT modified $($entry.Path) (清单 $($entry.Hash) != 实际 $actual)"
            $drift++
        }
    }

    # 清单外的文件同样是漂移；白名单只有 MIRROR.md。
    $registered = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]($entries | ForEach-Object { $_.Path }), [System.StringComparer]::Ordinal)
    # 刻意用 foreach 语句而非 `| ForEach-Object`：后者的脚本块跑在自己的作用域里，
    # `$drift++` 只会改一份局部副本，计数永远归零——检查从此静默通过。
    $mirrorRoot = Join-Path $Root 'contract-mirror'
    if (Test-Path -LiteralPath $mirrorRoot) {
        foreach ($file in (Get-RecursiveFilesChecked -Path $mirrorRoot)) {
            $rel = Get-RelativePathPortable -BasePath $Root -FullPath $file.FullName
            if ($rel -ne 'contract-mirror/MIRROR.md' -and -not $registered.Contains($rel)) {
                Say "MVP_HOST_MIRROR_DRIFT unregistered $rel"
                $drift++
            }
        }
    }
    else {
        Say 'MVP_HOST_MIRROR_DRIFT missing contract-mirror'
        $drift++
    }

    if ($drift -gt 0) {
        Say "MVP_HOST_MIRROR_FAIL drift=$drift"
        return $driftExit
    }

    Say "MVP_HOST_MIRROR_OK files=$($entries.Count)"
    return 0
}

function Write-UpstreamReport {
    $archRoot = $env:LUMIO_ARCHITECTURE_ROOT
    if ([string]::IsNullOrWhiteSpace($archRoot) -or -not (Test-Path (Join-Path $archRoot '.git'))) {
        Say 'MVP_HOST_MIRROR_UPSTREAM skipped（未设置 $LUMIO_ARCHITECTURE_ROOT 或不是 git 仓库）'
        return
    }

    $archRef = if ($env:LUMIO_ARCHITECTURE_REF) { $env:LUMIO_ARCHITECTURE_REF } else { 'origin/main' }
    $archCommit = (& git -C $archRoot rev-parse $archRef 2>$null)
    if ([string]::IsNullOrWhiteSpace($archCommit)) {
        Say "MVP_HOST_MIRROR_UPSTREAM skipped（架构源解析不出 $archRef）"
        return
    }

    $behind = 0
    foreach ($entry in Read-ManifestEntries (Join-Path $mvpHostDir $manifestRelative)) {
        $rel = $entry.Path -replace '^contract-mirror/', ''
        $src = if ($rel -like 'canonical/*') { "packages/$rel" } else { $rel }
        try {
            $upstream = Get-Sha256Hex -Bytes (Get-GitBlobBytes -RepoRoot $archRoot -ObjectSpec "${archCommit}:${src}")
        }
        catch {
            Say "MVP_HOST_MIRROR_UPSTREAM gone $($entry.Path)（架构源 $archCommit 下已无 $src）"
            $behind++
            continue
        }
        if ($upstream -ne $entry.Hash) {
            Say "MVP_HOST_MIRROR_UPSTREAM behind $($entry.Path)（上游 $upstream）"
            $behind++
        }
    }

    if ($behind -gt 0) {
        Say "MVP_HOST_MIRROR_UPSTREAM behind=$behind arch=$archCommit —— 报告性质，不影响退出码；同步用 pwsh eng/sync-contract-mirror.ps1"
    }
    else {
        Say "MVP_HOST_MIRROR_UPSTREAM in-sync arch=$archCommit"
    }
}

function Invoke-SelfTest {
    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    try {
        New-Item -ItemType Directory -Path (Join-Path $sandbox 'contract-mirror') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $sandbox 'eng') -Force | Out-Null

        $probe = Join-Path $sandbox 'contract-mirror/probe.json'
        [System.IO.File]::WriteAllText($probe, "probe`n", (New-Object System.Text.UTF8Encoding $false))
        [System.IO.File]::WriteAllText((Join-Path $sandbox 'contract-mirror/MIRROR.md'), "白名单项`n",
            (New-Object System.Text.UTF8Encoding $false))
        $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $probe).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText((Join-Path $sandbox $manifestRelative),
            "$digest  contract-mirror/probe.json`n", (New-Object System.Text.UTF8Encoding $false))

        $status = Test-NotHandEdited $sandbox | Select-Object -Last 1
        if ($status -ne 0) {
            Say "MVP_HOST_MIRROR_SELFTEST_FAIL 未篡改的对照组本应通过，实际退出 $status"
            return 1
        }
        Say 'SELFTEST 对照组（未篡改）→ 退出 0'

        [System.IO.File]::AppendAllText($probe, 'tampered')
        $status = Test-NotHandEdited $sandbox | Select-Object -Last 1
        if ($status -ne $driftExit) {
            Say "MVP_HOST_MIRROR_SELFTEST_FAIL 篡改后本应退出 $driftExit，实际 $status —— 守护是空转的"
            return 1
        }
        Say "SELFTEST 实验组（篡改一个字节）→ 退出 $driftExit"

        [System.IO.File]::WriteAllText($probe, "probe`n", (New-Object System.Text.UTF8Encoding $false))
        [System.IO.File]::WriteAllText((Join-Path $sandbox 'contract-mirror/sneaked-in.json'), "unregistered`n",
            (New-Object System.Text.UTF8Encoding $false))
        $status = Test-NotHandEdited $sandbox | Select-Object -Last 1
        if ($status -ne $driftExit) {
            Say "MVP_HOST_MIRROR_SELFTEST_FAIL 清单外文件本应退出 $driftExit，实际 $status"
            return 1
        }
        Say "SELFTEST 实验组（清单外新增文件）→ 退出 $driftExit"

        Remove-Item -LiteralPath $probe -Force
        Remove-Item -LiteralPath (Join-Path $sandbox 'contract-mirror/sneaked-in.json') -Force
        [System.IO.File]::WriteAllText((Join-Path $sandbox $manifestRelative),
            "$digest  ../outside.json`n", (New-Object System.Text.UTF8Encoding $false))
        $status = Test-NotHandEdited $sandbox | Select-Object -Last 1
        if ($status -ne $driftExit) {
            Say "MVP_HOST_MIRROR_SELFTEST_FAIL 越界路径本应退出 $driftExit，实际 $status"
            return 1
        }
        Say "SELFTEST 实验组（manifest 路径越界）→ 退出 $driftExit"

        Say 'MVP_HOST_MIRROR_SELFTEST_OK'
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
