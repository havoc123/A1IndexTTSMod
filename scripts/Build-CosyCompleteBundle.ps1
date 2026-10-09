[CmdletBinding()]
param(
    [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe',
    [string] $OutputRoot = '',
    [string] $SevenZipVolumeSize = '1540m',
    [switch] $SplitModel,
    [string] $ReuseModelArchive = ''
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $project 'src\A1IndexTTSMod.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $OutputRoot) {
    $edition = if ($SplitModel) { 'split' } else { 'complete' }
    $OutputRoot = Join-Path $project "dist\A1IndexTTSMod-v$version-cosy-$edition-win64"
}
$output = [IO.Path]::GetFullPath($OutputRoot)
$archiveBase = Split-Path -Leaf $output
$archive = Join-Path $output "$archiveBase.7z"
$stage = Join-Path $output 'package-stage'
$payload = Join-Path $stage 'A1'
$loaderZip = Join-Path $project '.cache\6.0.0-be.788+5b766a3\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip'
$runtime = Join-Path $project '.cache\audiocpp\runtime'
$cosyModel = Join-Path $project '.cache\audiocpp\models\CosyVoice3-GGUF\cosyvoice3-q8_0.gguf'
$game = 'E:\Program Files (x86)\Steam\steamapps\common\A1'
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $project 'src\bin\Release\net6.0\A1IndexTTSMod.dll')).Version.ToString() -ne "$version.0") { throw 'Built plugin does not match source version.' }
if ($SplitModel) { $archive = Join-Path $output "A1IndexTTSMod-v$version-cosy-program-win64.7z" }
if ($ReuseModelArchive) {
    if (-not $SplitModel) { throw 'ReuseModelArchive requires SplitModel.' }
    $ReuseModelArchive = [IO.Path]::GetFullPath($ReuseModelArchive)
    if (-not (Test-Path -LiteralPath $ReuseModelArchive -PathType Leaf)) { throw 'Existing model archive is missing.' }
    $archivedModelHash = (& $SevenZip e $ReuseModelArchive 'MODEL-SHA256.txt' -so) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the existing model archive fingerprint.' }
    $currentModelHash = (Get-FileHash -LiteralPath $cosyModel -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archivedModelHash.Trim() -ne $currentModelHash) { throw 'Existing model archive does not identify the current GGUF.' }
}

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

    if (-not $SplitModel) { Copy-Item -LiteralPath $cosyModel -Destination (Join-Path $models 'cosyvoice3-q8_0.gguf') }
    Copy-Item -LiteralPath (Join-Path $project 'src\bin\Release\net6.0\A1IndexTTSMod.dll') -Destination $pluginDir
    foreach ($dependency in @('NAudio.Core.dll', 'NAudio.Wasapi.dll', 'NAudio.WinMM.dll', 'sherpa-onnx.dll')) {
        Copy-Item -LiteralPath (Join-Path $project "src\bin\Release\net6.0\$dependency") -Destination $pluginDir
    }
    Copy-Item -LiteralPath (Join-Path $project 'src\managed-feature-api\bin\Release\net6.0\LocalModManager.Abstractions.dll') -Destination $plugins

    foreach ($scriptName in @('Run-AudioCppForGame.ps1', 'Start-AudioCpp.ps1', 'Stop-AudioCpp.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $scripts
    }
    $asrAssets = Join-Path $mod 'assets/asr'
    New-Item -ItemType Directory -Path $asrAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $project 'assets/asr/microphone-normal.png'),(Join-Path $project 'assets/asr/microphone-highlight.png') -Destination $asrAssets
    Copy-Item -LiteralPath (Join-Path $project 'config/asr-hotwords.zh-CN.txt') -Destination (Join-Path $mod 'config')
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
## Settings file for A1IndexTTSMod CosyVoice edition
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
PresetVoiceStyles = true
ReferenceId = demo
AudioCppModelId = cosyvoice3
TimeoutSeconds = 180
AutoStartAudioCpp = true
AudioCppPrecision = q8_0
GpuBackend = Auto
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
    $installDoc = if ($SplitModel) { 'INSTALL-COSY-SPLIT.md' } else { 'INSTALL-COSY-COMPLETE.md' }
    Copy-Item -LiteralPath (Join-Path $project "packaging\$installDoc") -Destination (Join-Path $packageRoot '安装说明.md')
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
    if ($SplitModel) {
        $bat = Join-Path $packageRoot '安装CosyVoice语音MOD.bat'
        (Get-Content -LiteralPath $bat -Raw).Replace('-GamePath "%~1"', '-AllowMissingModel -GamePath "%~1"') | Set-Content -LiteralPath $bat -Encoding ascii
    }

    $manifest = Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
    } | Sort-Object
    $forbidden = @($manifest | Where-Object { $_ -match '(?i)(IndexTTS2\.5-GGUF|index-tts2_5|fish_audio|stage2a|conversation|\.state/|/obj/|/bin/)' })
    if ($forbidden.Count) { throw "Forbidden artifact found in package: $($forbidden -join ', ')" }
    if (@($manifest | Where-Object { $_ -like 'A1/A1IndexTTSMod/references/npcs/*.wav' }).Count -ne 1169) { throw 'NPC reference WAV count is not 1169.' }

    $manifest | Set-Content -LiteralPath (Join-Path $packageRoot 'PACKAGE-MANIFEST.txt') -Encoding utf8
    Push-Location $packageRoot
    try {
        $archiveOptions = @('a', '-t7z', '-mx=5', '-mmt=on')
        if (-not $SplitModel) { $archiveOptions += "-v$SevenZipVolumeSize" }
        & $SevenZip @archiveOptions $archive * | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip packaging failed: $LASTEXITCODE" }
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $packageRoot 'PACKAGE-MANIFEST.txt') -Destination (Join-Path $output 'PACKAGE-MANIFEST.txt')
    Copy-Item -LiteralPath (Join-Path $packageRoot '安装说明.md') -Destination (Join-Path $output '安装说明.md')
    if (-not ([IO.Path]::GetFullPath($stage).StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Unsafe staging cleanup path.' }
    Remove-Item -LiteralPath $stage -Recurse -Force

    if ($SplitModel -and $ReuseModelArchive) {
        $modelArchive = Join-Path $output 'CosyVoice3-q8_0-model-win64.7z'
        Copy-Item -LiteralPath $ReuseModelArchive -Destination $modelArchive
        if ((Get-FileHash -LiteralPath $ReuseModelArchive -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $modelArchive -Algorithm SHA256).Hash) { throw 'Reused model archive copy differs from the source.' }
        $currentModelHash | Set-Content -LiteralPath (Join-Path $output 'MODEL-SHA256.txt') -Encoding ascii
    } elseif ($SplitModel) {
        $modelStage = Join-Path $output 'model-stage'
        $modelPayload = Join-Path $modelStage 'A1\A1IndexTTSMod\.cache\audiocpp\models\CosyVoice3-GGUF'
        New-Item -ItemType Directory -Path $modelPayload -Force | Out-Null
        Copy-Item -LiteralPath $cosyModel -Destination $modelPayload
        $modelHash = (Get-FileHash -LiteralPath $cosyModel -Algorithm SHA256).Hash.ToLowerInvariant()
        $modelHash | Set-Content -LiteralPath (Join-Path $modelStage 'MODEL-SHA256.txt') -Encoding ascii
        foreach ($name in @('LICENSE', 'third_party-CosyVoice-LICENSE.txt', 'THIRD_PARTY_NOTICES.md', '安装说明.md')) {
            $source = switch ($name) {
                'LICENSE' { Join-Path $project 'LICENSE' }
                'third_party-CosyVoice-LICENSE.txt' { Join-Path $project 'third_party\CosyVoice-LICENSE.txt' }
                'THIRD_PARTY_NOTICES.md' { Join-Path $project 'packaging\THIRD_PARTY_COSY_COMPLETE.md' }
                '安装说明.md' { Join-Path $project 'packaging\INSTALL-COSY-SPLIT.md' }
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $modelStage $name)
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-CosyModel.ps1') -Destination $modelStage
        @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CosyModel.ps1" -GamePath "%~1"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Installation failed. Error code: %RESULT%
pause
exit /b %RESULT%
'@ | Set-Content -LiteralPath (Join-Path $modelStage '安装CosyVoice模型.bat') -Encoding ascii
        $modelArchive = Join-Path $output 'CosyVoice3-q8_0-model-win64.7z'
        Push-Location $modelStage
        try {
            & $SevenZip a -t7z -mx=5 -mmt=on $modelArchive * | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Model packaging failed: $LASTEXITCODE" }
        } finally { Pop-Location }
        Copy-Item -LiteralPath (Join-Path $modelStage 'MODEL-SHA256.txt') -Destination $output
        if (-not ([IO.Path]::GetFullPath($modelStage).StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Unsafe model staging cleanup path.' }
        Remove-Item -LiteralPath $modelStage -Recurse -Force
    }

    $pattern = if ($SplitModel) { '*.7z' } else { "$archiveBase.7z.*" }
    $volumes = @(Get-ChildItem -LiteralPath $output -File -Filter $pattern | Sort-Object Name)
    if ($volumes.Count -ne 2) { throw "Expected two archive files/volumes, got $($volumes.Count)." }
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
