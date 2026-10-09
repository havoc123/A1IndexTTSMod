[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string] $FromVersion = 'v0.7.0',
    [string] $BasePackageName = 'A1IndexTTSMod-v0.7.0-cosy-complete-win64'
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$projectFile = Join-Path $project 'src\A1IndexTTSMod.csproj'
$versionMatch = [regex]::Match((Get-Content -LiteralPath $projectFile -Raw), '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { throw "Could not read plugin version from $projectFile" }
$toVersion = 'v' + $versionMatch.Groups[1].Value
$fromAssemblyVersion = [Version]($FromVersion.TrimStart('v') + '.0')
$legacyPayload = $fromAssemblyVersion -lt [Version]'0.7.3.0'
$name = "$BasePackageName-to-$toVersion-patch-win64"
$stage = Join-Path $OutputRoot $name
$archive = Join-Path $OutputRoot "$name.zip"
$outputPrefix = $OutputRoot.TrimEnd('\') + '\'
$stageFullPath = [IO.Path]::GetFullPath($stage)
if (-not $stageFullPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Patch staging path escaped the output directory: $stageFullPath"
}
if (Test-Path -LiteralPath $stage) { throw "Patch staging directory already exists: $stage" }
if (Test-Path -LiteralPath $archive) { throw "Patch archive already exists: $archive" }

$sourceBin = Join-Path $project 'src\bin\Release\net6.0'
$patchFiles = @(
    [pscustomobject]@{ Source = (Join-Path $sourceBin 'A1IndexTTSMod.dll'); Relative = 'A1\BepInEx\plugins\A1IndexTTSMod\A1IndexTTSMod.dll' },
    [pscustomobject]@{ Source = (Join-Path $PSScriptRoot 'Start-AudioCpp.ps1'); Relative = 'A1\A1IndexTTSMod\scripts\Start-AudioCpp.ps1' },
    [pscustomobject]@{ Source = (Join-Path $PSScriptRoot 'Run-AudioCppForGame.ps1'); Relative = 'A1\A1IndexTTSMod\scripts\Run-AudioCppForGame.ps1' },
    [pscustomobject]@{ Source = (Join-Path $PSScriptRoot 'Stop-AudioCpp.ps1'); Relative = 'A1\A1IndexTTSMod\scripts\Stop-AudioCpp.ps1' }
)
foreach ($name in @('NAudio.Core.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll','sherpa-onnx.dll')) {
    $patchFiles += [pscustomobject]@{ Source = (Join-Path $sourceBin $name); Relative = ('A1\BepInEx\plugins\A1IndexTTSMod\' + $name) }
}
foreach ($name in @('microphone-normal.png','microphone-highlight.png')) {
    $patchFiles += [pscustomobject]@{ Source = (Join-Path $project ('assets\asr\' + $name)); Relative = ('A1\A1IndexTTSMod\assets\asr\' + $name) }
}
if ($legacyPayload) {
    $patchFiles += [pscustomobject]@{ Source = (Join-Path $project '.cache\audiocpp\runtime\audiocpp_server-vulkan.exe'); Relative = 'A1\A1IndexTTSMod\.cache\audiocpp\runtime\audiocpp_server-vulkan.exe' }
}
foreach ($file in $patchFiles) {
    if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
        throw "Patch input is missing: $($file.Source). Build Release first."
    }
}
$builtVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $sourceBin 'A1IndexTTSMod.dll')).Version
if ($builtVersion -ne [Version]($versionMatch.Groups[1].Value + '.0')) {
    throw "Built DLL version $builtVersion does not match source $toVersion. Build Release first."
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    foreach ($file in $patchFiles) {
        $destination = Join-Path $stage $file.Relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $destination
    }

    $installer = @'
[CmdletBinding()]
param([string] $GamePath = '')

$ErrorActionPreference = 'Stop'
function Resolve-GameRoot([string] $InputPath) {
    $value = $InputPath.Trim().Trim('"')
    if (-not $value) { throw '未提供游戏路径。' }
    $path = [IO.Path]::GetFullPath($value)
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        if ([IO.Path]::GetFileName($path) -ne 'WorldApart.exe') { throw "请选择 WorldApart.exe：$path" }
        $path = Split-Path -Parent $path
    }
    if (-not (Test-Path -LiteralPath (Join-Path $path 'WorldApart.exe') -PathType Leaf)) {
        throw "未找到 WorldApart.exe：$path"
    }
    return $path
}

try {
    $patchRoot = $PSScriptRoot
    $patchVersion = '__PATCH_VERSION__'
    $basePackageName = '__BASE_PACKAGE_NAME__'
    $payload = Join-Path $patchRoot 'A1'
    $relativeFiles = @(
__RELATIVE_FILES__
    )
    $expectedHashes = @{
__EXPECTED_HASHES__
    }
    foreach ($relative in $relativeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) {
            throw "补丁包不完整，缺少：$relative"
        }
        if ((Get-FileHash -LiteralPath (Join-Path $payload $relative) -Algorithm SHA256).Hash -ne $expectedHashes[$relative]) {
            throw "补丁文件校验失败：$relative。请重新下载并完整解压。"
        }
    }
    if (-not $GamePath) {
        Write-Host '请输入 WorldApart.exe 的完整路径或其所在文件夹：'
        $GamePath = Read-Host '游戏路径'
    }
    $gameRoot = Resolve-GameRoot $GamePath
    if (Get-Process -Name 'WorldApart' -ErrorAction SilentlyContinue) {
        throw '游戏正在运行。请先正常退出游戏，再重新执行补丁安装。'
    }
    $requiredExisting = @(
        'BepInEx\core\BepInEx.Unity.IL2CPP.dll',
        'BepInEx\plugins\A1IndexTTSMod\A1IndexTTSMod.dll',
        'BepInEx\plugins\A1IndexTTSMod\NAudio.Core.dll',
        'BepInEx\plugins\A1IndexTTSMod\NAudio.Wasapi.dll',
        'A1IndexTTSMod\.cache\audiocpp\runtime\audiocpp_server.exe',
        'BepInEx\plugins\LocalModManager.Abstractions.dll'
    )
    foreach ($relative in $requiredExisting) {
        if (-not (Test-Path -LiteralPath (Join-Path $gameRoot $relative) -PathType Leaf)) {
            throw "没有找到 __FROM_VERSION__ 所需的已安装文件：$relative。此补丁不包含完整运行时。"
        }
    }
    $installedPlugin = Join-Path $gameRoot 'BepInEx\plugins\A1IndexTTSMod\A1IndexTTSMod.dll'
    $installedVersion = [Reflection.AssemblyName]::GetAssemblyName($installedPlugin).Version
    if ($installedVersion -ne [Version]'__FROM_ASSEMBLY_VERSION__') {
        throw "此补丁只针对 $basePackageName（插件版本 __FROM_ASSEMBLY_VERSION__），当前检测到 $installedVersion。"
    }

    $backupRoot = Join-Path $gameRoot ('A1IndexTTSMod\.state\patch-backups\' + $basePackageName + '-to-' + $patchVersion)
    $backupRoot = Join-Path $backupRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    foreach ($relative in $relativeFiles) {
        $source = Join-Path $payload $relative
        $target = Join-Path $gameRoot $relative
        $targetDirectory = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $backup = Join-Path $backupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $backup
        }
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expectedHashes[$relative]) {
            throw "安装后文件校验失败：$relative。原文件备份：$backupRoot"
        }
    }
    Write-Host "已升级至 $patchVersion。配置、模型和参考音均未覆盖。"
    Write-Host "原文件备份位置：$backupRoot"
} catch {
    [Console]::Error.WriteLine("补丁安装失败：$($_.Exception.Message)")
    exit 1
}
'@
    $relativeList = ($patchFiles | ForEach-Object { "        '" + $_.Relative.Substring(3) + "'" }) -join ",`r`n"
    $hashEntries = ($patchFiles | ForEach-Object { "        '" + $_.Relative.Substring(3) + "' = '" + (Get-FileHash -LiteralPath $_.Source -Algorithm SHA256).Hash + "'" }) -join "`r`n"
    $installer = $installer.Replace('__PATCH_VERSION__', $toVersion).Replace('__BASE_PACKAGE_NAME__', $BasePackageName).Replace('__RELATIVE_FILES__', $relativeList).Replace('__EXPECTED_HASHES__', $hashEntries).Replace('__FROM_VERSION__', $FromVersion).Replace('__FROM_ASSEMBLY_VERSION__', $fromAssemblyVersion.ToString())
    [IO.File]::WriteAllText((Join-Path $stage 'Install-UpgradePatch.ps1'), $installer, [Text.UTF8Encoding]::new($true))

    $launcher = @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-UpgradePatch.ps1" %*
if errorlevel 1 pause
'@
    [IO.File]::WriteAllText((Join-Path $stage '安装补丁.bat'), $launcher, [Text.Encoding]::ASCII)

    $instructions = @"
A1 CosyVoice NPC 语音 MOD 升级补丁：$BasePackageName -> $toVersion

此补丁专用于 $FromVersion（插件程序集版本 $fromAssemblyVersion）。请先正常退出游戏并等待 Steam 云存档同步完成，将本补丁完整解压到任意文件夹，双击“安装补丁.bat”并输入 WorldApart.exe 的路径。

本补丁更新插件 DLL 和三个配套启停脚本。安装器会备份被替换目标的同名文件，并保留配置、模型、参考音及 BepInEx 加载器。补丁不会安装大肥鱼或改写游戏 AI 服务商设置。

更新内容：有可朗读台词时要求返回 content 内部语音风格帧，无 Schema 时也增强文本输出契约；默认内置预设问候语和开场白情感库，按角色与实际台词精确匹配，第三页区分模型风格与离线预设来源；源码新装默认使用 CosyVoice。已有配置保留，IndexTTS 用户迁移时需要准备 CosyVoice 模型并核对后端设置。同步 DLL 与监督进程参数，避免新 DLL 配旧脚本导致服务启动失败。

默认 GPU 路由为 Nvidia。安装后完全退出游戏，编辑 BepInEx\config\org.a1indextts.mod.cfg，在 [Stage3Mvp] 设置 GpuBackend = Vulkan 可改用 AMD/Vulkan；多显卡设备可用 GpuDevice 指定 AMD 的序号（默认 0），改回 Nvidia 即恢复 CUDA 默认路线。Vulkan 路线要求显卡驱动提供 Vulkan，服务日志应出现 Vulkan0 才表示模型确实加载在 GPU 上。

如果目标目录缺少 BepInEx、LocalModManager.Abstractions.dll、CUDA 版 audiocpp_server.exe 或版本为 $fromAssemblyVersion 的插件文件，请先完整安装指定旧版；本补丁不包含模型、CUDA 运行时 DLL 或 NPC 参考音。

安装后，BepInEx/LogOutput.log 应显示 Loading [A1-TTS-Mod $($versionMatch.Groups[1].Value)]。原文件备份位置由安装器输出；需要回退时，退出游戏并将备份中 BepInEx、A1IndexTTSMod 两个目录内的文件复制回游戏根目录。

验证范围：v0.7.4 的无 Schema 修复已在本机与大肥鱼 2.0 共载，用可控本地上游验证实际 NPC 对话、正文帧剥离和 TTS 播放。当前必填风格契约、预设情感库和 CosyVoice 默认配置已编译；按要求未运行新增测试或游戏试听。真实模型仍可能不遵守帧要求，插件无法恢复上游未生成或已删除的数据；离线库仅补充精确匹配的既有预设台词。
"@
    if ([Version]($versionMatch.Groups[1].Value) -ge [Version]'0.7.5') {
        $instructions += "`r`nv0.7.5 补强：有可朗读台词时，要求同次模型回复必须在 content 字符串内部末尾返回一个风格帧；平淡或上下文不足时生成中性表达。统一提示词、字段说明和 Schema 描述，明确外层 JSON 转义及旧格式约束。第三页区分已要求但未检测到的情况，诊断摘要包含 VoiceStyleRequired。`r`n"
    }
    if ($legacyPayload) {
        $instructions += "`r`n从早期版本升级的此包额外包含 NAudio.WinMM.dll 和 audiocpp_server-vulkan.exe；独立 Vulkan exe 不覆盖 Nvidia 服务端。`r`n"
    }
    [IO.File]::WriteAllText((Join-Path $stage '升级说明.txt'), $instructions, [Text.UTF8Encoding]::new($true))

    $manifest = @($patchFiles | ForEach-Object {
        $path = Join-Path $stage $_.Relative
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Relative)"
    })
    [IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $manifest, [Text.UTF8Encoding]::new($false))

    Compress-Archive -LiteralPath (Join-Path $stage 'A1'),(Join-Path $stage 'Install-UpgradePatch.ps1'),(Join-Path $stage '安装补丁.bat'),(Join-Path $stage '升级说明.txt'),(Join-Path $stage 'SHA256SUMS.txt') `
        -DestinationPath $archive -CompressionLevel Optimal
    $zipHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{
        FromVersion = $FromVersion
        ToVersion = $toVersion
        Archive = $archive
        ArchiveBytes = (Get-Item -LiteralPath $archive).Length
        ArchiveSha256 = $zipHash
        PayloadFiles = $patchFiles.Count
    } | Format-List
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
