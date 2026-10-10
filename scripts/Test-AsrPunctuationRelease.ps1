[CmdletBinding()]
param([string] $OutputRoot = (Join-Path $PSScriptRoot '../dist'))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $project ('.state/punctuation-release-test-'+[guid]::NewGuid().ToString('N'))
$game = Join-Path $fixture 'A1'
$plugin = Join-Path $game 'BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll'
New-Item -ItemType Directory -Path (Split-Path -Parent $plugin) -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $game 'WorldApart.exe'),'Offline fixture; never execute.')
foreach ($relative in @('BepInEx/core/BepInEx.Unity.IL2CPP.dll','BepInEx/plugins/A1IndexTTSMod/NAudio.Core.dll','BepInEx/plugins/A1IndexTTSMod/NAudio.Wasapi.dll','BepInEx/plugins/LocalModManager.Abstractions.dll','A1IndexTTSMod/.cache/audiocpp/runtime/audiocpp_server.exe')) {
    $path = Join-Path $game $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path,'Offline fixture')
}
$config = Join-Path $game 'BepInEx/config/org.a1indextts.mod.cfg'
New-Item -ItemType Directory -Path (Split-Path -Parent $config) -Force | Out-Null
[IO.File]::WriteAllText($config,'Preserve user configuration')
$configHash = (Get-FileHash -LiteralPath $config).Hash
$sevenZip = 'C:/Program Files/7-Zip/7z.exe'
$oldArchive = Join-Path $project 'dist/A1IndexTTSMod-v0.7.6-cosy-split-win64(语音mod)/(语音mod-程序包)A1IndexTTSMod-v0.7.6-cosy-program-win64.7z'
& $sevenZip e $oldArchive 'A1/BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll' ('-o'+(Split-Path -Parent $plugin)) -y | Out-Null
if ($LASTEXITCODE) { throw 'Cannot extract original plugin.' }
$oldHash = (Get-FileHash -LiteralPath $plugin).Hash
$stage = Join-Path $fixture 'patch076'
Expand-Archive -LiteralPath (Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.6-cosy-split-win64-to-v0.7.8-patch-win64.zip') -DestinationPath $stage
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Install-UpgradePatch.ps1') -GamePath $game
if ($LASTEXITCODE) { throw '0.7.6 patch failed.' }
if ([Reflection.AssemblyName]::GetAssemblyName($plugin).Version -ne [Version]'0.7.8.0') { throw 'Wrong upgraded version.' }
$saved = Get-ChildItem -LiteralPath (Join-Path $game 'A1IndexTTSMod/.state/patch-backups') -Recurse -Filter A1IndexTTSMod.dll | Select-Object -First 1
if (-not $saved -or (Get-FileHash -LiteralPath $saved.FullName).Hash -ne $oldHash) { throw 'Old plugin backup mismatch.' }
# Extract the previous patch DLL as the real 0.7.7 assembly fixture.
& $sevenZip e (Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.6-cosy-split-win64-to-v0.7.7-patch-win64.zip') 'A1/BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll' ('-o'+(Split-Path -Parent $plugin)) -y | Out-Null
if ($LASTEXITCODE) { throw 'Cannot extract 0.7.7 assembly.' }
$stage = Join-Path $fixture 'patch077'
Expand-Archive -LiteralPath (Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.7-to-v0.7.8-patch-win64.zip') -DestinationPath $stage
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Install-UpgradePatch.ps1') -GamePath $game
if ($LASTEXITCODE -or [Reflection.AssemblyName]::GetAssemblyName($plugin).Version -ne [Version]'0.7.8.0') { throw '0.7.7 patch failed.' }
$stage = Join-Path $fixture 'punctuation'
Expand-Archive -LiteralPath (Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.8-ASR-Punctuation-win64.zip') -DestinationPath $stage
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Install-AsrPunctuation.ps1') -GameRoot $game
if ($LASTEXITCODE) { throw 'Offline punctuation package install failed.' }
$model = Join-Path $game 'A1IndexTTSMod/asr/punctuation-ct-transformer-zh-en-int8/model.int8.onnx'
$expected = '65a3fb9f5ad7bfb96bf69e0dc4481df97f6ee60513c1d94ce981ba6effd524b1'
if ((Get-FileHash -LiteralPath $model).Hash.ToLowerInvariant() -ne $expected) { throw 'Installed model checksum mismatch.' }
if ((Get-FileHash -LiteralPath $config).Hash -ne $configHash) { throw 'User configuration changed.' }
# Simulate an invalid prior model, and reject a damaged incoming file before replacing it.
[IO.File]::WriteAllText($model,'Old invalid model; keep on failed install.')
$oldModelHash = (Get-FileHash -LiteralPath $model).Hash
$damaged = Join-Path $fixture 'damaged.onnx'
[IO.File]::WriteAllText($damaged,'Damaged incoming file')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Install-AsrPunctuation.ps1') -GameRoot $game -ModelFile $damaged 2> (Join-Path $fixture 'expected-rejection.log')
if ($LASTEXITCODE -eq 0 -or (Get-FileHash -LiteralPath $model).Hash -ne $oldModelHash) { throw 'Damaged model accepted or old install changed.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Install-AsrPunctuation.ps1') -GameRoot $game
if ($LASTEXITCODE -or (Get-FileHash -LiteralPath $model).Hash.ToLowerInvariant() -ne $expected) { throw 'Valid replacement failed.' }
$oldModel = Get-ChildItem -LiteralPath (Split-Path -Parent (Split-Path -Parent $model)) -Directory -Filter '*.previous-*' | Select-Object -First 1
if (-not $oldModel -or (Get-FileHash -LiteralPath (Join-Path $oldModel.FullName 'model.int8.onnx')).Hash -ne $oldModelHash) { throw 'Model backup missing.' }
Write-Host 'PASS actual 0.7.6/0.7.7 -> 0.7.8 patches, offline punctuation install, config preservation, damaged model rejection, old model backup.'
Write-Host "Offline evidence: $fixture"
