[CmdletBinding()]
param([string] $OutputRoot = (Join-Path $PSScriptRoot '../dist'))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $project ('.state/asr-release-test-' + [guid]::NewGuid().ToString('N'))
$game = Join-Path $fixture 'A1'
New-Item -ItemType Directory -Path $game -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $game 'WorldApart.exe'), 'Offline installer fixture; never execute.')
$required = @('BepInEx/core/BepInEx.Unity.IL2CPP.dll','BepInEx/plugins/A1IndexTTSMod/NAudio.Core.dll',
    'BepInEx/plugins/A1IndexTTSMod/NAudio.Wasapi.dll','BepInEx/plugins/LocalModManager.Abstractions.dll',
    'A1IndexTTSMod/.cache/audiocpp/runtime/audiocpp_server.exe')
foreach ($path in $required) {
    $target = Join-Path $game $path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target,'Offline fixture')
}
$oldArchive = Join-Path $project 'dist/A1IndexTTSMod-v0.7.6-cosy-split-win64(语音mod)/(语音mod-程序包)A1IndexTTSMod-v0.7.6-cosy-program-win64.7z'
$pluginDirectory = Join-Path $game 'BepInEx/plugins/A1IndexTTSMod'
& 'C:/Program Files/7-Zip/7z.exe' e $oldArchive 'A1/BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll' "-o$pluginDirectory" -y | Out-Null
if ($LASTEXITCODE) { throw 'Cannot extract the original 0.7.6 plugin fixture.' }
$config = Join-Path $game 'A1IndexTTSMod/config/asr-hotwords.zh-CN.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $config) -Force | Out-Null
[IO.File]::WriteAllText($config,'保留用户热词')
$configHash = (Get-FileHash -LiteralPath $config).Hash
$oldPluginHash = (Get-FileHash -LiteralPath (Join-Path $pluginDirectory 'A1IndexTTSMod.dll')).Hash
$patch = Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.6-cosy-split-win64-to-v0.7.7-patch-win64.zip'
$patchStage = Join-Path $fixture 'patch'
Expand-Archive -LiteralPath $patch -DestinationPath $patchStage
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $patchStage 'Install-UpgradePatch.ps1') -GamePath $game
if ($LASTEXITCODE) { throw 'Main patch installation failed.' }
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $pluginDirectory 'A1IndexTTSMod.dll')).Version -ne [Version]'0.7.7.0') { throw 'Wrong upgraded plugin version.' }
$backup = Get-ChildItem -LiteralPath (Join-Path $game 'A1IndexTTSMod/.state/patch-backups') -Filter A1IndexTTSMod.dll -Recurse | Select-Object -First 1
if (-not $backup -or (Get-FileHash -LiteralPath $backup.FullName).Hash -ne $oldPluginHash) { throw 'Main patch did not preserve the old plugin.' }
$package = Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.7-ASR-DirectML-14M-win64'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-AsrPackage.ps1') -GamePath $game
if ($LASTEXITCODE) { throw 'Optional ASR installation failed.' }
if ((Get-FileHash -LiteralPath $config).Hash -ne $configHash) { throw 'User hotwords were overwritten.' }
$files = Get-Content (Join-Path $package 'files.json') -Raw | ConvertFrom-Json
foreach ($file in $files) {
    if ($file.path -like '*config*') { continue }
    if ((Get-FileHash -LiteralPath (Join-Path $game $file.path)).Hash -ne $file.sha256) { throw 'Optional package installation checksum mismatch.' }
}
# Damage a staging byte, verify preflight rejection, then restore it exactly.
# The release archive remains untouched throughout this negative test.
$source = Join-Path $package 'A1IndexTTSMod/asr/runtime-directml/DirectML.dll'
$stream = [IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite)
$original = $stream.ReadByte(); $stream.Position=0; $stream.WriteByte([byte]($original -bxor 1)); $stream.Dispose()
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-AsrPackage.ps1') -GamePath $game 2> (Join-Path $fixture 'expected-checksum-rejection.log')
    if ($LASTEXITCODE -eq 0) { throw 'Damaged optional package was accepted.' }
} finally {
    $stream = [IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Write)
    $stream.WriteByte([byte]$original); $stream.Dispose()
}
$restoredHash = (Get-FileHash -LiteralPath $source).Hash
$expected = $files | Where-Object { $_.path -like '*runtime-directml*DirectML.dll' } | Select-Object -First 1
if ($restoredHash -ne $expected.sha256) { throw 'Negative test did not restore the package staging file.' }
Write-Host 'PASS actual 0.7.6 -> 0.7.7 patch, old DLL backup, optional ASR installation, preserved user hotwords, damaged package rejected and staging restored.'
Write-Host "Offline evidence: $fixture"
