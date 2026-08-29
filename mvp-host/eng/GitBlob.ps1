#!/usr/bin/env pwsh
# 从 git 对象库读一个 blob 的**原始字节**。被 sync-contract-mirror.ps1 与
# generate-contracts.ps1 共用（dot-source 引入）。
#
# 不能用 `git show ... > file` 或 `Out-String`：PowerShell 的重定向与字符串管道
# 会按行重组并按当前编码写出，在 Windows 上把 LF 变成 CRLF，字节级镜像当场作废。
# 唯一可靠的做法是直接读子进程 stdout 的 BaseStream。
Set-StrictMode -Version Latest

# 消息一律直写 stdout，**不用 Write-Output**：PowerShell 的函数返回值就是输出流，
# 用 Write-Output 打印消息会让消息与返回值混在同一个流里，调用方拿返回码时
# （`$f | Select-Object -Last 1`）把消息一并吞掉，脚本从此静默——正是「看起来在守护」的那类失效。
function Say([string]$Message) {
    [Console]::Out.WriteLine($Message)
}

function Get-GitBlobBytes {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ObjectSpec
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'git'
    foreach ($a in @('-C', $RepoRoot, 'cat-file', 'blob', $ObjectSpec)) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::Start($psi)
    $buffer = [System.IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($buffer)
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        throw "git cat-file blob ${ObjectSpec} 失败（exit $($process.ExitCode)）：$stderr"
    }

    return $buffer.ToArray()
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}
