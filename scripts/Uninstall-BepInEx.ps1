[CmdletBinding()]
param(
    [string] $GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$StateRoot = Join-Path $ProjectRoot '.state'

function Assert-InGameRoot([string] $Path) {
    $prefix = $GameRoot.TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the game root: $full"
    }
    return $full
}

function Remove-RecordedFile([string] $RelativePath, [string] $ExpectedHash) {
    $target = Assert-InGameRoot (Join-Path $GameRoot $RelativePath)
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { return }
    $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash) {
        Write-Warning "Keeping modified file: $target"
        return
    }
    Remove-Item -LiteralPath $target -Force
}

$pluginStatePath = Join-Path $StateRoot 'plugin-install.json'
if (Test-Path -LiteralPath $pluginStatePath) {
    $pluginState = Get-Content -LiteralPath $pluginStatePath -Raw | ConvertFrom-Json
    Remove-RecordedFile $pluginState.path $pluginState.sha256
    Remove-Item -LiteralPath $pluginStatePath -Force
}

$loaderStatePath = Join-Path $StateRoot 'bepinex-install.json'
if (-not (Test-Path -LiteralPath $loaderStatePath)) {
    Write-Host 'No managed BepInEx installation record found; no loader files removed.'
    exit 0
}

$loaderState = Get-Content -LiteralPath $loaderStatePath -Raw | ConvertFrom-Json
foreach ($entry in $loaderState.files) {
    Remove-RecordedFile $entry.path $entry.sha256
}
Remove-Item -LiteralPath $loaderStatePath -Force

# Remove only empty directories created by the loader. Generated config, interop, and logs remain intact.
$candidateDirectories = @($loaderState.files | ForEach-Object {
    $directory = Split-Path -Parent (Join-Path $GameRoot $_.path)
    while ($directory -and $directory.StartsWith($GameRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $directory
        $parent = Split-Path -Parent $directory
        if ($parent -eq $directory -or $directory -eq $GameRoot) { break }
        $directory = $parent
    }
}) | Sort-Object { $_.Length } -Descending -Unique

foreach ($directory in $candidateDirectories) {
    if ((Test-Path -LiteralPath $directory -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath (Assert-InGameRoot $directory) -Force
    }
}

Write-Host 'Removed unchanged managed BepInEx files. Generated logs, config, and interop data were preserved.'
