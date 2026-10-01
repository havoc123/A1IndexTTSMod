[CmdletBinding()]
param(
    [string] $GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'
$Build = '788'
$Commit = '5b766a3'
$Version = "6.0.0-be.$Build+$Commit"
$ArchiveName = "BepInEx-Unity.IL2CPP-win-x64-$Version.zip"
$DownloadUri = "https://builds.bepinex.dev/projects/bepinex_be/$Build/$ArchiveName" -replace '\+', '%2B'
$ExpectedArchiveSha256 = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$StateRoot = Join-Path $ProjectRoot '.state'
$StatePath = Join-Path $StateRoot 'bepinex-install.json'
$CacheRoot = Join-Path $ProjectRoot ".cache\$Version"
$GameRoot = [IO.Path]::GetFullPath($GameRoot)

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-InGameRoot([string] $Path) {
    $prefix = $GameRoot.TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside game root: $full"
    }
    return $full
}

if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'WorldApart.exe'))) {
    throw "WorldApart.exe was not found in game root: $GameRoot"
}
$bootConfig = Join-Path $GameRoot 'WorldApart_Data\boot.config'
if (-not (Test-Path -LiteralPath $bootConfig) -or
    -not (Select-String -LiteralPath $bootConfig -SimpleMatch 'build-guid=0acccbcdb9a14aa3a528bd4d850c3202' -Quiet)) {
    throw 'The game build GUID does not match the tested build. Review INSTALL.md before installing.'
}

if (Test-Path -LiteralPath $StatePath) {
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($state.version -eq $Version) {
        if ($state.archiveSha256 -ne $ExpectedArchiveSha256) {
            throw "Install record archive SHA256 does not match pinned BepInEx $Version."
        }
        foreach ($entry in $state.files) {
            $installed = Assert-InGameRoot (Join-Path $GameRoot $entry.path)
            if (-not (Test-Path -LiteralPath $installed) -or (Get-Sha256 $installed) -ne $entry.sha256) {
                throw "Installed BepInEx file is missing or changed: $installed"
            }
        }
        Write-Host "Pinned BepInEx $Version is already installed and verified. No files changed."
        exit 0
    }
    throw "An install record already exists ($($state.version)); uninstall it before switching loader versions."
}

$markers = @('BepInEx', 'doorstop_config.ini', 'winhttp.dll', '.doorstop_version')
$existing = @($markers | Where-Object { Test-Path -LiteralPath (Join-Path $GameRoot $_) })
if ($existing.Count -gt 0) {
    throw "Unmanaged BepInEx files already exist ($($existing -join ', ')); refusing to overwrite them."
}

New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
$cachedArchive = Join-Path $CacheRoot $ArchiveName
if ($ArchivePath) {
    $ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
    Copy-Item -LiteralPath $ArchivePath -Destination $cachedArchive -Force
} elseif (-not (Test-Path -LiteralPath $cachedArchive)) {
    Write-Host "Downloading pinned BepInEx $Version from $DownloadUri"
    try {
        Invoke-WebRequest -Uri $DownloadUri -OutFile $cachedArchive -MaximumRedirection 5
    } catch {
        Remove-Item -LiteralPath $cachedArchive -Force -ErrorAction SilentlyContinue
        throw "Could not download BepInEx. Check network access, then retry or pass -ArchivePath to the official $ArchiveName file. Details: $($_.Exception.Message)"
    }
}

$archiveHash = Get-Sha256 $cachedArchive
if ($archiveHash -ne $ExpectedArchiveSha256) {
    throw "BepInEx archive SHA256 mismatch. Expected $ExpectedArchiveSha256, got $archiveHash."
}

$extractRoot = Join-Path $CacheRoot 'unpacked'
if (Test-Path -LiteralPath $extractRoot) {
    throw "Staging directory already exists: $extractRoot. Inspect it and remove it manually before retrying."
}
New-Item -ItemType Directory -Path $extractRoot | Out-Null
$createdFiles = [Collections.Generic.List[object]]::new()
$alreadyPresent = [Collections.Generic.List[string]]::new()

try {
    Expand-Archive -LiteralPath $cachedArchive -DestinationPath $extractRoot
    $files = @(Get-ChildItem -LiteralPath $extractRoot -File -Recurse)
    if ($files.Count -eq 0) { throw 'The BepInEx archive contained no files.' }

    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($extractRoot, $file.FullName)
        $target = Assert-InGameRoot (Join-Path $GameRoot $relative)
        if (Test-Path -LiteralPath $target) {
            if ((Get-Sha256 $target) -eq (Get-Sha256 $file.FullName)) {
                $alreadyPresent.Add($relative)
                continue
            }
            throw "File collision: $target already exists and differs from the pinned archive."
        }

        $parent = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $createdFiles.Add([pscustomobject]@{ path = $relative; sha256 = Get-Sha256 $target })
    }

    New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
    [pscustomobject]@{
        version = $Version
        build = $Build
        commit = $Commit
        gameBuildGuid = '0acccbcdb9a14aa3a528bd4d850c3202'
        archive = $ArchiveName
        archiveSha256 = $archiveHash
        source = $DownloadUri
        installedAtUtc = [DateTime]::UtcNow.ToString('o')
        files = @($createdFiles)
        matchingPreexistingFiles = @($alreadyPresent)
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StatePath -Encoding utf8
} catch {
    foreach ($entry in $createdFiles) {
        $target = Assert-InGameRoot (Join-Path $GameRoot $entry.path)
        if ((Test-Path -LiteralPath $target) -and (Get-Sha256 $target) -eq $entry.sha256) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    throw
} finally {
    $cachePrefix = [IO.Path]::GetFullPath($CacheRoot).TrimEnd('\') + '\'
    $stagingPath = [IO.Path]::GetFullPath($extractRoot)
    if (-not $stagingPath.StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove staging outside cache root: $stagingPath"
    }
    $stagingItem = Get-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
    if ($stagingItem) {
        if ($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to remove a staging reparse point: $stagingPath"
        }
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

Write-Host "Installed BepInEx $Version. Archive SHA256: $archiveHash"
Write-Host 'Start WorldApart.exe once to generate LogOutput and interop. Do not start a second game instance.'
