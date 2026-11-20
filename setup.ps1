#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $GameUserDirectory
)

$ErrorActionPreference = 'Stop'
$modId = 'OniResourceBalance'
$modTitle = '缺氧资源分析'
$payload = @('OniResourceBalance.dll', 'mod.yaml', 'mod_info.yaml')
$staging = $null
$setupLock = $null
$lockHeld = $false

function Get-FileDigest([string] $Path) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash([IO.File]::ReadAllBytes($Path)))
    } finally {
        $sha256.Dispose()
    }
}

function Test-Payload([string] $Directory, [hashtable] $Hashes) {
    foreach ($name in $Hashes.Keys) {
        $file = Join-Path $Directory $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileDigest $file) -ne $Hashes[$name]) {
            return $false
        }
    }
    return $true
}

function Get-PayloadVersion([string] $Directory) {
    $manifest = Join-Path $Directory 'mod.yaml'
    $metadata = Join-Path $Directory 'mod_info.yaml'
    if (![IO.File]::Exists($manifest) -or ![IO.File]::Exists($metadata)) {
        return $null
    }
    if ([IO.File]::ReadAllText($manifest) -cnotmatch ('(?m)^\s*staticID:\s*' + [regex]::Escape($modId) + '\s*$')) {
        return $null
    }
    if ([IO.File]::ReadAllText($metadata) -match '(?m)^\s*version:\s*([0-9]+\.[0-9]+\.[0-9]+)\s*$') {
        return [Version]$Matches[1]
    }
    return $null
}

function Wait-ForGameExit {
    $notified = $false
    while ($true) {
        $games = [Diagnostics.Process]::GetProcessesByName('OxygenNotIncluded')
        if ($games.Count -eq 0) {
            return
        }
        if (!$notified) {
            Write-Host '《缺氧》正在运行。请正常退出游戏，等这里显示「已启用」后再启动。'
            $notified = $true
        }
        foreach ($game in $games) {
            try {
                $game.WaitForExit()
            } finally {
                $game.Dispose()
            }
        }
    }
}

function Get-LocalModVersion([string] $FolderName) {
    # U60 expects Mono's signed 32-bit string hash for local mod labels.
    $even = 5381L
    $odd = 5381L
    for ($index = 0; $index -lt $FolderName.Length; $index++) {
        $codeUnit = [int]$FolderName[$index]
        if ($index % 2 -eq 0) {
            $even = (($even * 33) -bxor $codeUnit) -band 4294967295L
        } else {
            $odd = (($odd * 33) -bxor $codeUnit) -band 4294967295L
        }
    }
    $version = ($even + $odd * 1566083941L) -band 4294967295L
    if ($version -ge 2147483648L) {
        $version -= 4294967296L
    }
    return $version
}

function Enable-LocalMod([string] $ModsDirectory, [string] $FolderName) {
    $settingsPath = Join-Path $ModsDirectory 'mods.json'
    $originalHash = $null
    if ([IO.File]::Exists($settingsPath)) {
        $originalHash = Get-FileDigest $settingsPath
        try {
            $settings = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($settingsPath))
        } catch {
            throw "无法读取游戏的模组配置 $settingsPath"
        }
        if ($settings.mods -isnot [array]) {
            throw "游戏模组配置缺少模组列表 $settingsPath"
        }
    } else {
        $settings = [pscustomobject]@{
            version = 1
            mods = @()
            mod_load_in_progress = $false
        }
    }

    $entries = @($settings.mods | Where-Object {
        $_.label.distribution_platform -eq 0 -and $_.label.id -ceq $FolderName
    })
    if ($entries.Count -gt 1 -or ($entries.Count -eq 1 -and $entries[0].staticID -and $entries[0].staticID -cne $modId)) {
        throw "模组配置中有冲突的本地记录 $FolderName"
    }

    $gameModes = @('', 'EXPANSION1_ID')
    $folderVersion = Get-LocalModVersion $FolderName
    if ($entries.Count -eq 0) {
        $settings.mods += [pscustomobject]@{
            label = [pscustomobject]@{
                distribution_platform = 0
                id = $FolderName
                title = $modTitle
                version = $folderVersion
            }
            status = 1
            enabled = $true
            enabledForDlc = $gameModes
            crash_count = 0
            reinstall_path = $null
            staticID = $modId
        }
    } else {
        $entry = $entries[0]
        $missingModes = @($gameModes | Where-Object { $_ -cnotin $entry.enabledForDlc })
        # U60 reads enabledForDlc and can reset the legacy enabled flag when saving.
        if ($entry.enabled -eq $true -and $entry.status -eq 1 -and $entry.staticID -ceq $modId -and
            $entry.label.title -ceq $modTitle -and $entry.label.version -eq $folderVersion -and $missingModes.Count -eq 0) {
            return
        }
        $entry | Add-Member -NotePropertyMembers @{
            status = 1
            enabled = $true
            enabledForDlc = @($entry.enabledForDlc | Where-Object { $null -ne $_ }) + $missingModes
            staticID = $modId
        } -Force
        $entry.label | Add-Member -NotePropertyMembers @{
            title = $modTitle
            version = $folderVersion
        } -Force
    }

    $pending = Join-Path $ModsDirectory (".$modId-" + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        $json = ConvertTo-Json -InputObject $settings -Depth 100
        [IO.File]::WriteAllText($pending, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        if ($originalHash) {
            if ((Get-FileDigest $settingsPath) -ne $originalHash) {
                throw '模组配置在安装期间发生了变化，请重新运行安装脚本。'
            }
            $backup = "$settingsPath.$modId-$([Guid]::NewGuid().ToString('N')).bak"
            [IO.File]::Replace($pending, $settingsPath, $backup)
            Write-Host "原模组配置已备份到 $backup"
        } else {
            [IO.File]::Move($pending, $settingsPath)
        }
    } finally {
        [IO.File]::Delete($pending)
    }
}

try {
    $hashes = @{}
    foreach ($name in $payload) {
        $file = Join-Path $PSScriptRoot $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "安装包缺少 $name。请将安装脚本与完整模组文件放在同一目录。"
        }
        $hashes[$name] = Get-FileDigest $file
    }

    if (!$GameUserDirectory) {
        $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
        if (!$documents) {
            throw '找不到 Windows 的「文档」文件夹。'
        }
        $GameUserDirectory = Join-Path $documents 'Klei\OxygenNotIncluded'
    }
    $mods = [IO.Path]::GetFullPath((Join-Path $GameUserDirectory 'mods'))
    $local = Join-Path $mods 'Local'
    $destination = Join-Path $local $modId

    $setupLock = [Threading.Mutex]::new($false, 'Local\OniResourceBalanceSetup')
    try {
        $lockHeld = $setupLock.WaitOne()
    } catch [Threading.AbandonedMutexException] {
        $lockHeld = $true
    }
    Wait-ForGameExit

    $hasInstalledFiles = Test-Path -LiteralPath $destination
    if ($hasInstalledFiles -and (Test-Payload $destination $hashes)) {
        Write-Host "相同版本已安装到 $destination"
    } else {
        $backupDirectory = $null
        if ($hasInstalledFiles) {
            $installed = Get-Item -LiteralPath $destination
            $installedVersion = Get-PayloadVersion $destination
            $packageVersion = Get-PayloadVersion $PSScriptRoot
            if (!$installed.PSIsContainer -or ($installed.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                !$installedVersion -or !$packageVersion -or $installedVersion -ge $packageVersion) {
                throw "目标路径已有不同的模组文件，请检查 $destination"
            }
            $backupDirectory = Join-Path $mods ("$modId-$installedVersion-backup-" + [Guid]::NewGuid().ToString('N'))
        }
        $null = [IO.Directory]::CreateDirectory($local)
        # Stage outside Local so the game only discovers the complete installation.
        $staging = Join-Path $mods (".$modId-setup-" + [Guid]::NewGuid().ToString('N'))
        $null = [IO.Directory]::CreateDirectory($staging)
        foreach ($name in $payload) {
            [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $staging $name))
        }
        if (!(Test-Payload $staging $hashes)) {
            throw '模组文件校验失败。'
        }

        $staging = (Resolve-Path -LiteralPath $staging).ProviderPath
        $local = (Resolve-Path -LiteralPath $local).ProviderPath
        $destination = Join-Path $local $modId
        if ([IO.Path]::GetDirectoryName($staging) -ne $mods -or [IO.Path]::GetDirectoryName($local) -ne $mods -or
            [IO.Path]::GetDirectoryName($destination) -ne $local) {
            throw "安装路径不在预期的模组目录下 $mods"
        }
        if ($backupDirectory) {
            $previous = (Resolve-Path -LiteralPath $destination).ProviderPath
            if ([IO.Path]::GetDirectoryName($previous) -ne $local -or [IO.Path]::GetDirectoryName($backupDirectory) -ne $mods) {
                throw "旧版目录不在预期的模组目录下 $local"
            }
            [IO.Directory]::Move($previous, $backupDirectory)
            Write-Host "旧版模组已备份到 $backupDirectory"
        }
        try {
            [IO.Directory]::Move($staging, $destination)
            $staging = $null
        } catch {
            if ($backupDirectory -and !(Test-Path -LiteralPath $destination)) {
                [IO.Directory]::Move($backupDirectory, $destination)
            }
            if (!(Test-Payload $destination $hashes)) {
                throw
            }
        }
        Write-Host "模组文件已放在 $destination"
    }

    Enable-LocalMod $mods ((Get-Item -LiteralPath $destination).Name)
    Write-Host "「$modTitle」已安装并启用。现在可以启动游戏。"
} catch {
    Write-Host "安装或启用未完成。$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($staging -and [IO.Directory]::Exists($staging)) {
        try {
            foreach ($name in $payload) {
                [IO.File]::Delete((Join-Path $staging $name))
            }
            [IO.Directory]::Delete($staging)
        } catch {
            Write-Host "临时目录清理失败 $staging" -ForegroundColor Yellow
        }
    }
    if ($setupLock) {
        if ($lockHeld) {
            $setupLock.ReleaseMutex()
        }
        $setupLock.Dispose()
    }
}
