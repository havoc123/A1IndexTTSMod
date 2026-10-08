[CmdletBinding()]
param(
    [string] $GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string] $BepInExDir
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (-not $BepInExDir) { $BepInExDir = Join-Path $GameRoot 'BepInEx' }
$BepInExDir = [IO.Path]::GetFullPath($BepInExDir)
$PluginSource = Join-Path $ProjectRoot 'src\bin\Release\net6.0\A1IndexTTSMod.dll'
$PluginTarget = Join-Path $BepInExDir 'plugins\A1IndexTTSMod\A1IndexTTSMod.dll'
$DependencyNames = @('NAudio.Core.dll', 'NAudio.Wasapi.dll', 'NAudio.WinMM.dll')
$SharedDependencyNames = @('LocalModManager.Abstractions.dll')
$RuntimeScriptNames = @('Run-AudioCppForGame.ps1', 'Start-AudioCpp.ps1', 'Stop-AudioCpp.ps1')
$RuntimeScriptsTarget = Join-Path $GameRoot 'A1IndexTTSMod\scripts'
$StateRoot = Join-Path $ProjectRoot '.state'
$StatePath = Join-Path $StateRoot 'plugin-install.json'

if (-not (Test-Path -LiteralPath (Join-Path $BepInExDir 'LogOutput.log')) -and
    -not (Test-Path -LiteralPath (Join-Path $BepInExDir 'LogOutput.txt'))) {
    throw 'No BepInEx log found. Start the game once with BepInEx before installing the plugin.'
}
if (-not (Test-Path -LiteralPath $PluginSource)) { throw 'Plugin DLL is missing; run scripts\Build.ps1 first.' }
$hash = (Get-FileHash -LiteralPath $PluginSource -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($name in $RuntimeScriptNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))) {
        throw "Plugin runtime script is missing: $name"
    }
}

if (Test-Path -LiteralPath $PluginTarget) {
    if (-not (Test-Path -LiteralPath $StatePath)) {
        throw "Plugin path already exists and is not managed by this project: $PluginTarget"
    }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $existingHash = (Get-FileHash -LiteralPath $PluginTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existingHash -ne $state.sha256) { throw "Installed plugin was changed outside this script: $PluginTarget" }
}

foreach ($name in @($DependencyNames) + @($SharedDependencyNames)) {
    $source = Join-Path (Split-Path -Parent $PluginSource) $name
    $target = if ($SharedDependencyNames -contains $name) { Join-Path $BepInExDir ('plugins\' + $name) } else { Join-Path (Split-Path -Parent $PluginTarget) $name }
    if (-not (Test-Path -LiteralPath $source)) { throw "Plugin dependency is missing: $source" }
    if (Test-Path -LiteralPath $target) {
        $known = @($state.dependencies | Where-Object { $_.name -eq $name }) | Select-Object -First 1
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $sharedMatch = ($SharedDependencyNames -contains $name) -and ($actual -eq $sourceHash)
        if ((-not $known -and -not $sharedMatch) -or ($known -and $actual -ne $known.sha256)) {
            throw "Installed dependency was changed outside this script: $target"
        }
    }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $PluginTarget) -Force | Out-Null
Copy-Item -LiteralPath $PluginSource -Destination $PluginTarget -Force
foreach ($name in @($DependencyNames) + @($SharedDependencyNames)) {
    $target = if ($SharedDependencyNames -contains $name) { Join-Path $BepInExDir ('plugins\' + $name) } else { Join-Path (Split-Path -Parent $PluginTarget) $name }
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PluginSource) $name) -Destination $target -Force
}
New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
# Keep the supervisor's command-line contract in step with the installed DLL.
New-Item -ItemType Directory -Path $RuntimeScriptsTarget -Force | Out-Null
foreach ($name in $RuntimeScriptNames) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $RuntimeScriptsTarget $name) -Force
}
[pscustomobject]@{
    path = [IO.Path]::GetRelativePath($GameRoot, $PluginTarget)
    sha256 = $hash
    dependencies = @((@($DependencyNames) + @($SharedDependencyNames)) | ForEach-Object {
        [pscustomobject]@{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $PluginSource) $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    installedAtUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding utf8
Write-Host "Installed plugin to $PluginTarget"
