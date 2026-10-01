[CmdletBinding()]
param(
    [string] $BepInExDir = (Join-Path (Split-Path -Parent $PSScriptRoot) '..\BepInEx')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\A1IndexTTSMod.csproj'
$projectRoot = Split-Path -Parent $PSScriptRoot
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'
$stateRoot = Join-Path $projectRoot '.state'
$packagesRoot = Join-Path $projectRoot '.cache\nuget-packages'
$BepInExDir = [IO.Path]::GetFullPath($BepInExDir)

if (-not (Test-Path -LiteralPath (Join-Path $BepInExDir 'core\BepInEx.Core.dll'))) {
    throw "BepInEx references are missing: $BepInExDir. Run scripts\Install-BepInEx.ps1 first or pass -BepInExDir."
}

New-Item -ItemType Directory -Path `
    (Join-Path $stateRoot 'dotnet-home'), `
    (Join-Path $stateRoot 'nuget-home'), `
    (Join-Path $stateRoot 'profile\Roaming'), `
    (Join-Path $stateRoot 'profile\Local'), `
    $packagesRoot -Force | Out-Null

# Keep SDK first-run state, NuGet configuration and restored packages inside this project.
# This avoids machine/user-level NuGet settings changing what a clean build resolves.
$env:DOTNET_CLI_HOME = Join-Path $stateRoot 'dotnet-home'
$env:NUGET_CLI_HOME = Join-Path $stateRoot 'nuget-home'
$env:APPDATA = Join-Path $stateRoot 'profile\Roaming'
$env:LOCALAPPDATA = Join-Path $stateRoot 'profile\Local'
$env:NUGET_PACKAGES = $packagesRoot

& dotnet restore $project --configfile $nugetConfig --packages $packagesRoot "-p:BepInExDir=$BepInExDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

& dotnet build $project --configuration Release --no-restore "-p:BepInExDir=$BepInExDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

Write-Host "Plugin built: $(Join-Path (Split-Path -Parent $PSScriptRoot) 'src\bin\Release\net6.0\A1IndexTTSMod.dll')"
