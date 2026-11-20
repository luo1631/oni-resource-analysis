#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Archive,
    [string] $PackageDirectory,
    [string] $GameUserDirectory
)

$ErrorActionPreference = 'Stop'
$legacyFiles = @('OniResourceBalance.dll', 'mod.yaml', 'mod_info.yaml', 'README.md', 'setup.cmd', 'setup.ps1')
$packageFiles = $legacyFiles + @('LICENSE', 'NOTICE.md')
$archivePath = (Resolve-Path -LiteralPath $Archive).ProviderPath
if (!$PackageDirectory) {
    $packageRoot = Join-Path $PSScriptRoot 'dist/local-package'
    $null = [IO.Directory]::CreateDirectory($packageRoot)
    $PackageDirectory = Join-Path $packageRoot 'OniResourceBalance'
}
$package = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageDirectory)
$package = [IO.Path]::TrimEndingDirectorySeparator($package)
$parent = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($package))
if (!$parent.PSIsContainer -or ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    (Join-Path $parent.FullName ([IO.Path]::GetFileName($package))) -ne $package) {
    throw "无效的发布路径 $package"
}

function Remove-PackageCopy([string] $Directory) {
    foreach ($name in $packageFiles) {
        [IO.File]::Delete((Join-Path $Directory $name))
    }
    [IO.Directory]::Delete($Directory)
}

$publishLock = [Threading.Mutex]::new($false, 'Local\OniResourceBalancePublish')
$lockHeld = $false
$staging = $null
try {
    try {
        $lockHeld = $publishLock.WaitOne()
    } catch [Threading.AbandonedMutexException] {
        $lockHeld = $true
    }

    $previous = Get-Item -LiteralPath $package -Force -ErrorAction SilentlyContinue
    if ($previous -and ($previous.FullName -ne $package -or !$previous.PSIsContainer -or
        ($previous.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw "发布路径已被其他对象占用 $package"
    }
    $existing = @()
    if ($previous) {
        $existing = @(Get-ChildItem -LiteralPath $package -Force)
        $unexpected = $existing | Where-Object {
            $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -cnotin $packageFiles
        }
        $manifest = Join-Path $package 'mod.yaml'
        if ($unexpected -or ($existing.Count -ne 0 -and
            (![IO.File]::Exists($manifest) -or [IO.File]::ReadAllText($manifest) -cnotmatch '(?m)^\s*staticID:\s*OniResourceBalance\s*$'))) {
            throw "发布目录中有其他文件 $package"
        }
    }

    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($zip.Entries.Count -eq $packageFiles.Count) {
            $archiveFiles = $packageFiles
        } elseif ($zip.Entries.Count -eq $legacyFiles.Count) {
            $archiveFiles = $legacyFiles
        } else {
            throw '安装包文件数量不符。'
        }
        $staging = (New-Item -ItemType Directory -Path (Join-Path $parent.FullName ('.OniResourceBalance-' + [Guid]::NewGuid().ToString('N')))).FullName
        if ([IO.Path]::GetDirectoryName($staging) -ne $parent.FullName) {
            $staging = $null
            throw '安装包暂存路径不在发布目录旁。'
        }
        foreach ($name in $archiveFiles) {
            $entry = $zip.GetEntry("OniResourceBalance/$name")
            if ($null -eq $entry -or $entry.Length -eq 0) {
                throw "安装包缺少文件 $name"
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $name))
        }
    } finally {
        $zip.Dispose()
    }
    if ([IO.File]::ReadAllText((Join-Path $staging 'mod.yaml')) -cnotmatch '(?m)^\s*staticID:\s*OniResourceBalance\s*$') {
        throw '安装包的模组 ID 不符。'
    }

    $unchanged = $existing.Count -eq $archiveFiles.Count
    foreach ($file in $existing) {
        $replacement = Join-Path $staging $file.Name
        if (![IO.File]::Exists($replacement) -or
            (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $replacement).Hash) {
            $unchanged = $false
            break
        }
    }
    if (!$unchanged) {
        $backup = $null
        if ($previous) {
            $backup = $staging + '-previous'
            if ([IO.Path]::GetDirectoryName($backup) -ne $parent.FullName) {
                throw '旧安装包的暂存路径不在发布目录旁。'
            }
            [IO.Directory]::Move($package, $backup)
        }
        try {
            [IO.Directory]::Move($staging, $package)
            $staging = $null
        } catch {
            if ($backup -and ![IO.Directory]::Exists($package)) {
                [IO.Directory]::Move($backup, $package)
            }
            throw
        }
        if ($backup) {
            try {
                Remove-PackageCopy $backup
            } catch {
                Write-Warning "旧安装包暂存目录清理失败 $backup"
            }
        }
    }
    Write-Host "安装包已解压并校验 $package"

    $installArgs = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $package 'setup.ps1'))
    if ($GameUserDirectory) {
        $installArgs += @('-GameUserDirectory', $GameUserDirectory)
    }
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @installArgs
    if ($LASTEXITCODE -ne 0) {
        throw "模组初始化失败，退出码 $LASTEXITCODE。"
    }
} finally {
    try {
        if ($staging) {
            Remove-PackageCopy $staging
        }
    } finally {
        if ($lockHeld) {
            $publishLock.ReleaseMutex()
        }
        $publishLock.Dispose()
    }
}
