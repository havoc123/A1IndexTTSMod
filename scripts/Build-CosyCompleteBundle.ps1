[CmdletBinding()]
param(
    [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe',
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\A1IndexTTSMod-v0.7.3-cosy-complete-win64'),
    [string] $SevenZipVolumeSize = '1540m'
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputRoot)
$archiveBase = Split-Path -Leaf $output
$archive = Join-Path $output "$archiveBase.7z"
$stage = Join-Path $output 'package-stage'
$payload = Join-Path $stage 'A1'
$loaderZip = Join-Path $project '.cache\6.0.0-be.788+5b766a3\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip'
$runtime = Join-Path $project '.cache\audiocpp\runtime'
$cosyModel = Join-Path $project '.cache\audiocpp\models\CosyVoice3-GGUF\cosyvoice3-q8_0.gguf'
$game = 'E:\Program Files (x86)\Steam\steamapps\common\A1'

foreach ($path in @($SevenZip, $loaderZip, $runtime, $cosyModel, (Join-Path $project '.cache\bepinex\2022.3.43.zip'), (Join-Path $project '.cache\doorstop\winhttp.dll'), (Join-Path $project 'references\npcs\npc_id_name.csv'), (Join-Path $game 'dotnet\coreclr.dll'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required local asset is missing: $path" }
}
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw "Output directory is not empty: $output" }
if (Test-Path -LiteralPath $archive) { throw "Output archive already exists: $archive" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $payload | Out-Null

try {
    # The pinned official archive contains all BepInEx and .NET files. Replace its
    # generic proxy with the project's tested A1-compatible Doorstop build.
    & $SevenZip x $loaderZip "-o$payload" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not extract pinned BepInEx archive: $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $project '.cache\doorstop\winhttp.dll') -Destination (Join-Path $payload 'winhttp.dll')
    $unityLibs = Join-Path $payload 'BepInEx\unity-libs'
    New-Item -ItemType Directory -Path $unityLibs -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $project '.cache\bepinex\2022.3.43.zip') -Destination (Join-Path $unityLibs '2022.3.43.zip')

    $mod = Join-Path $payload 'A1IndexTTSMod'
    $models = Join-Path $mod '.cache\audiocpp\models\CosyVoice3-GGUF'
    $audioRuntime = Join-Path $mod '.cache\audiocpp\runtime'
    $references = Join-Path $mod 'references'
    $scripts = Join-Path $mod 'scripts'
    $plugins = Join-Path $payload 'BepInEx\plugins'
    $pluginDir = Join-Path $plugins 'A1IndexTTSMod'
    New-Item -ItemType Directory -Path $models, $audioRuntime, $references, $scripts, $pluginDir, (Join-Path $mod 'config'), (Join-Path $payload 'BepInEx\config') -Force | Out-Null

    Copy-Item -LiteralPath $cosyModel -Destination (Join-Path $models 'cosyvoice3-q8_0.gguf')
    Copy-Item -LiteralPath (Join-Path $project 'src\bin\Release\net6.0\A1IndexTTSMod.dll') -Destination $pluginDir
    foreach ($dependency in @('NAudio.Core.dll', 'NAudio.Wasapi.dll', 'NAudio.WinMM.dll')) {
        Copy-Item -LiteralPath (Join-Path $project "src\bin\Release\net6.0\$dependency") -Destination $pluginDir
    }
    Copy-Item -LiteralPath (Join-Path $project 'src\managed-feature-api\bin\Release\net6.0\LocalModManager.Abstractions.dll') -Destination $plugins

    foreach ($scriptName in @('Run-AudioCppForGame.ps1', 'Start-AudioCpp.ps1', 'Stop-AudioCpp.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $scripts
    }
    Copy-Item -LiteralPath (Join-Path $project 'config\emotions.json') -Destination (Join-Path $mod 'config\emotions.json') -Force
    Copy-Item -LiteralPath (Join-Path $project 'references\demo.wav') -Destination $references
    Copy-Item -LiteralPath (Join-Path $project 'references\default_female.wav') -Destination $references
    Copy-Item -LiteralPath (Join-Path $project 'references\default_male.wav') -Destination $references
    Copy-Item -LiteralPath (Join-Path $project 'references\npcs') -Destination $references -Recurse
    Copy-Item -LiteralPath (Join-Path $project 'references\npcs\npc_id_name.csv') -Destination (Join-Path $references 'npcs\npc_id_name.csv') -Force

    # audio.cpp 0.8.2 A1 performance build, CUDA and VC runtime DLLs, plus its
    # CosyVoice3 model spec. Omit unrelated model catalogs and development tools.
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtime -File | Where-Object { $_.Extension -eq '.dll' }) + @(
        (Get-Item -LiteralPath (Join-Path $runtime 'audiocpp_server.exe')),
        (Get-Item -LiteralPath (Join-Path $runtime 'audiocpp_server-vulkan.exe'))
    )
    foreach ($file in $runtimeFiles) { Copy-Item -LiteralPath $file.FullName -Destination $audioRuntime }
    New-Item -ItemType Directory -Path (Join-Path $audioRuntime 'model_specs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtime 'model_specs\cosyvoice3.json') -Destination (Join-Path $audioRuntime 'model_specs\cosyvoice3.json')
    Copy-Item -LiteralPath (Join-Path $runtime 'LICENSE') -Destination $audioRuntime

    $pluginConfig = @'
## Settings file for A1IndexTTSMod v0.7.3 CosyVoice complete edition
## Plugin GUID: org.a1indextts.mod

[Stage2A]
Enabled = false
CaptureFullPrompt = false
MaxRecordChars = 120000
CaptureDirectory = .state/stage2a

[SpeechPanel]
VolumePercent = 100
AutoRead = true

[Stage3Mvp]
Enabled = true
TtsUrl = http://127.0.0.1:8892/v1/audio/speech
Backend = CosyVoiceAudioCpp
PromptEnhancement = true
ReferenceId = demo
AudioCppModelId = cosyvoice3
TimeoutSeconds = 180
AutoStartAudioCpp = true
AudioCppPrecision = q8_0
GpuBackend = Nvidia
GpuDevice = 0
'@
    $pluginConfig | Set-Content -LiteralPath (Join-Path $payload 'BepInEx\config\org.a1indextts.mod.cfg') -Encoding utf8

    $packageRoot = Split-Path -Parent $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-WorkshopPackage.ps1') -Destination (Join-Path $packageRoot 'Install-CosyVoice.ps1')
    Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $project 'third_party\NAudio-LICENSE.txt') -Destination (Join-Path $packageRoot 'third_party-NAudio-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $project 'third_party\BepInEx-LICENSE.txt') -Destination (Join-Path $packageRoot 'third_party-BepInEx-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $project 'third_party\CosyVoice-LICENSE.txt') -Destination (Join-Path $packageRoot 'third_party-CosyVoice-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $project '.state\unitydoorstop-v4.5.0-src\UnityDoorstop-4.5.0\LICENSE') -Destination (Join-Path $packageRoot 'third_party-UnityDoorstop-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $project 'packaging\THIRD_PARTY_COSY_COMPLETE.md') -Destination (Join-Path $packageRoot 'THIRD_PARTY_NOTICES.md')
    Copy-Item -LiteralPath (Join-Path $project 'packaging\INSTALL-COSY-COMPLETE.md') -Destination (Join-Path $packageRoot '安装说明.md')
    @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CosyVoice.ps1" -GamePath "%~1"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Installation failed. Error code: %RESULT%
pause
exit /b %RESULT%
'@ | Set-Content -LiteralPath (Join-Path $packageRoot '安装CosyVoice语音MOD.bat') -Encoding ascii

    $manifest = Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
    } | Sort-Object
    $forbidden = @($manifest | Where-Object { $_ -match '(?i)(IndexTTS2\.5-GGUF|index-tts2_5|fish_audio|stage2a|conversation|\.state/|/obj/|/bin/)' })
    if ($forbidden.Count) { throw "Forbidden artifact found in package: $($forbidden -join ', ')" }
    if (@($manifest | Where-Object { $_ -like 'A1/A1IndexTTSMod/references/npcs/*.wav' }).Count -ne 1169) { throw 'NPC reference WAV count is not 1169.' }

    $manifest | Set-Content -LiteralPath (Join-Path $packageRoot 'PACKAGE-MANIFEST.txt') -Encoding utf8
    Push-Location $packageRoot
    try {
        & $SevenZip a -t7z -mx=5 -mmt=on "-v$SevenZipVolumeSize" $archive * | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip packaging failed: $LASTEXITCODE" }
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $packageRoot 'PACKAGE-MANIFEST.txt') -Destination (Join-Path $output 'PACKAGE-MANIFEST.txt')
    Copy-Item -LiteralPath (Join-Path $packageRoot '安装说明.md') -Destination (Join-Path $output '安装说明.md')
    Remove-Item -LiteralPath $stage -Recurse -Force

    $volumes = @(Get-ChildItem -LiteralPath $output -File -Filter "$archiveBase.7z.*" | Sort-Object Name)
    if ($volumes.Count -ne 2) { throw "Expected exactly .001/.002 volume files, got $($volumes.Count)." }
    if (@($volumes | Where-Object { $_.Length -ge 4GB }).Count) { throw 'Each archive volume must be smaller than 4 GB.' }
    $sumFile = Join-Path $output "SHA256SUMS-$archiveBase.txt"
    $volumes | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$($_.Name)"
    } | Set-Content -LiteralPath $sumFile -Encoding ascii
    Write-Host "Package: $output"
    $volumes | Select-Object FullName,Length
    Get-Item -LiteralPath $sumFile | Select-Object FullName,Length
} catch {
    Write-Error $_
    throw
}
