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

# Windows PowerShell 5.1 runs on .NET Framework, where ProcessStartInfo has only
# the single Arguments string. Quote one argv element using the Windows CRT rules.
function ConvertTo-WindowsCommandLineArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Argument)

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [System.Text.StringBuilder]::new()
    $null = $builder.Append([char]34)
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }

        if ($character -eq [char]34) {
            $null = $builder.Append([char]92, (2 * $backslashes) + 1)
            $null = $builder.Append([char]34)
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            $null = $builder.Append([char]92, $backslashes)
            $backslashes = 0
        }
        $null = $builder.Append($character)
    }

    if ($backslashes -gt 0) {
        $null = $builder.Append([char]92, 2 * $backslashes)
    }
    $null = $builder.Append([char]34)
    return $builder.ToString()
}

function Get-GitBlobBytes {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ObjectSpec
    )

    $arguments = @('-C', $RepoRoot, 'cat-file', 'blob', $ObjectSpec)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'git'
    if ($psi.PSObject.Properties.Name -contains 'ArgumentList') {
        foreach ($argument in $arguments) {
            $psi.ArgumentList.Add($argument)
        }
    }
    else {
        $psi.Arguments = ($arguments | ForEach-Object {
                ConvertTo-WindowsCommandLineArgument -Argument $_
            }) -join ' '
    }
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

function Get-NormalizedContainmentRoot {
    param([Parameter(Mandatory)][string]$BasePath)

    $base = [System.IO.Path]::GetFullPath($BasePath)
    $root = [System.IO.Path]::GetPathRoot($base)
    if ($base.Length -gt $root.Length) {
        $base = $base.TrimEnd('\', '/')
    }
    return $base
}

function Get-ContainmentPrefix {
    param([Parameter(Mandatory)][string]$BasePath)

    $base = Get-NormalizedContainmentRoot -BasePath $BasePath
    if ($base.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal)) {
        return $base
    }
    return $base + [System.IO.Path]::DirectorySeparatorChar
}

function Get-PathComparison {
    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
        return [System.StringComparison]::OrdinalIgnoreCase
    }
    return [System.StringComparison]::Ordinal
}

function Resolve-ContainedPathPortable {
    param(
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath.Contains('\') -or
        $RelativePath.Contains('//') -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '^[A-Za-z]:') {
        throw "Path '$RelativePath' is not a canonical relative path"
    }

    # Manifests use slash-separated canonical paths on every host. Rejecting
    # non-canonical segments keeps the PowerShell and bash gates identical.
    $segments = $RelativePath.Split('/')
    if ($segments | Where-Object { $_ -in @('', '.', '..') }) {
        throw "Path '$RelativePath' contains a traversal segment"
    }

    $base = Get-NormalizedContainmentRoot -BasePath $BasePath
    $prefix = Get-ContainmentPrefix -BasePath $base
    $full = [System.IO.Path]::GetFullPath((Join-Path $base $RelativePath))
    if (-not $full.StartsWith($prefix, (Get-PathComparison))) {
        throw "Path '$full' is outside '$base'"
    }
    return $full
}

function Get-RelativePathPortable {
    param(
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][string]$FullPath
    )

    $base = Get-NormalizedContainmentRoot -BasePath $BasePath
    $full = [System.IO.Path]::GetFullPath($FullPath)
    $prefix = Get-ContainmentPrefix -BasePath $base
    if (-not $full.StartsWith($prefix, (Get-PathComparison))) {
        throw "Path '$full' is outside '$base'"
    }

    return $full.Substring($prefix.Length).Replace('\', '/')
}

function Get-RecursiveFilesChecked {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Filter,
        [string[]]$Include
    )

    $arguments = @{
        LiteralPath = $Path
        Recurse = $true
        File = $true
        Force = $true
        ErrorAction = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Filter')) {
        $arguments['Filter'] = $Filter
    }

    $files = Get-ChildItem @arguments
    if (-not $PSBoundParameters.ContainsKey('Include')) {
        return $files
    }

    $files | Where-Object {
        $matched = $false
        foreach ($pattern in $Include) {
            if ($_.Name -like $pattern) {
                $matched = $true
                break
            }
        }
        $matched
    }
}
