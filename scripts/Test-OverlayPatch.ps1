[CmdletBinding()]
param([string] $OutputRoot = (Join-Path $PSScriptRoot '../dist'))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $project ('.state/overlay-test-'+[guid]::NewGuid().ToString('N'))
$game = Join-Path $fixture 'A1'
$pluginDir = Join-Path $game 'BepInEx/plugins/A1IndexTTSMod'
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $game 'WorldApart.exe'),'Offline fixture; never execute.')
foreach ($relative in @('BepInEx/core/BepInEx.Unity.IL2CPP.dll','A1IndexTTSMod/.cache/audiocpp/runtime/audiocpp_server.exe','BepInEx/config/org.a1indextts.mod.cfg','A1IndexTTSMod/config/asr-hotwords.zh-CN.txt','A1IndexTTSMod/references/npcs/fixture.wav','A1IndexTTSMod/asr/sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30/fixture.onnx')) {
    $path = Join-Path $game $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path,'Preserve existing user fixture')
}
$preserved = @{}
foreach ($path in @('BepInEx/config/org.a1indextts.mod.cfg','A1IndexTTSMod/config/asr-hotwords.zh-CN.txt','A1IndexTTSMod/references/npcs/fixture.wav','A1IndexTTSMod/asr/sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30/fixture.onnx')) { $preserved[$path] = (Get-FileHash -LiteralPath (Join-Path $game $path)).Hash }
& 'C:/Program Files/7-Zip/7z.exe' e (Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.0-cosy-complete-win64-to-v0.7.5-patch-win64.zip') 'A1/BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll' ('-o'+$pluginDir) -y | Out-Null
if ($LASTEXITCODE) { throw 'Cannot extract real 0.7.5 assembly fixture.' }
$oldHash = (Get-FileHash -LiteralPath (Join-Path $pluginDir 'A1IndexTTSMod.dll')).Hash
foreach ($edition in @('program','with-ASR')) {
    $package = Join-Path $OutputRoot "A1IndexTTSMod-v0.7.8-overlay-$edition-win64"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-OverlayPatch.ps1') -GamePath $game
    if ($LASTEXITCODE) { throw "Overlay installation failed: $edition" }
    if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $pluginDir 'A1IndexTTSMod.dll')).Version -ne [Version]'0.7.8.0') { throw 'Wrong plugin version.' }
    foreach ($path in $preserved.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $game $path)).Hash -ne $preserved[$path]) { throw "Existing user file changed: $path" } }
    $files = Get-Content -LiteralPath (Join-Path $package 'files.json') -Raw | ConvertFrom-Json
    foreach ($file in $files) { if ((Get-FileHash -LiteralPath (Join-Path $game $file.path)).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Installed checksum mismatch: $($file.path)" } }
}
$backup = Get-ChildItem -LiteralPath (Join-Path $game 'A1IndexTTSMod/.state/overlay-backups') -Recurse -File -Filter A1IndexTTSMod.dll | Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -eq $oldHash } | Select-Object -First 1
if (-not $backup) { throw 'Original 0.7.5 DLL backup missing.' }
# Do not alter release payloads for the negative test; copy only the small program package.
$bad = Join-Path $fixture 'damaged-package'
Copy-Item -LiteralPath (Join-Path $OutputRoot 'A1IndexTTSMod-v0.7.8-overlay-program-win64') -Destination $bad -Recurse
[IO.File]::WriteAllText((Join-Path $bad 'A1/BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll'),'Damaged incoming plugin')
$before = (Get-FileHash -LiteralPath (Join-Path $pluginDir 'A1IndexTTSMod.dll')).Hash
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bad 'Install-OverlayPatch.ps1') -GamePath $game 2> (Join-Path $fixture 'expected-rejection.log')
if ($LASTEXITCODE -eq 0 -or (Get-FileHash -LiteralPath (Join-Path $pluginDir 'A1IndexTTSMod.dll')).Hash -ne $before) { throw 'Damaged package accepted or target changed.' }
Write-Host 'PASS real 0.7.5 -> 0.7.8 overlay, same-version ASR install, all payload hashes, original DLL backup, configuration/reference/160M preservation, damaged package rejection.'
Write-Host "Offline evidence: $fixture"
