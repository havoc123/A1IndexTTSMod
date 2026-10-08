[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = Join-Path $project 'dist'
$stage = Join-Path $dist 'A1IndexTTSMod-v0.5.8-portable-doorstop-staging'
$archive = Join-Path $dist 'A1IndexTTSMod-v0.5.8-portable-doorstop-patch.zip'
$proxy = Join-Path $project '.cache\doorstop\winhttp.dll'
$source = Join-Path $project '.state\unitydoorstop-v4.5.0-src\UnityDoorstop-4.5.0'
$unityBase = Join-Path $project '.cache\bepinex\2022.3.43.zip'
$expectedProxy = '9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b'
if (Test-Path -LiteralPath $stage) { throw "Staging directory exists: $stage" }
if (Test-Path -LiteralPath $archive) { throw "Archive exists: $archive" }
if ((Get-FileHash -LiteralPath $proxy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedProxy) {
    throw 'Portable Doorstop hash mismatch.'
}
if ((Get-FileHash -LiteralPath $unityBase -Algorithm SHA256).Hash.ToLowerInvariant() -ne '45d51c23363ac0abbf8fdb31f5f85992e8afd1d13552759809d66142f7cbbd3f') {
    throw 'Unity base library hash mismatch.'
}

New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath $proxy -Destination (Join-Path $stage 'winhttp.dll')
$configDir = Join-Path $stage 'BepInEx\config'
$unityDir = Join-Path $stage 'BepInEx\unity-libs'
New-Item -ItemType Directory -Path $configDir,$unityDir -Force | Out-Null
@'
[IL2CPP]
UnityBaseLibrariesSource = 2022.3.43.zip

[Logging.Console]
Enabled = true

[Logging.Disk]
Enabled = true
'@ | Set-Content -LiteralPath (Join-Path $configDir 'BepInEx.cfg') -Encoding ascii
Copy-Item -LiteralPath $unityBase -Destination (Join-Path $unityDir '2022.3.43.zip')
$thirdParty = Join-Path $stage 'A1IndexTTSMod\third_party'
$sourceOut = Join-Path $thirdParty 'UnityDoorstop-A1-source'
New-Item -ItemType Directory -Path $sourceOut -Force | Out-Null
foreach ($dir in @('src','assets')) {
    Copy-Item -LiteralPath (Join-Path $source $dir) -Destination (Join-Path $sourceOut $dir) -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $sourceOut 'build') -Force | Out-Null
foreach ($name in @('dll.def','info.rc','proxy.c')) {
    Copy-Item -LiteralPath (Join-Path $source "build\$name") -Destination (Join-Path $sourceOut "build\$name")
}
foreach ($name in @('build.bat','build.ps1','build.sh','CHANGES.md','info.lua','LICENSE','README.md','xmake.lua')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $sourceOut $name)
}
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $thirdParty 'UnityDoorstop-LICENSE.txt')
@'
《不问凡尘》AI NPC 语音 MOD 启动器修复补丁

适用于此前的 v0.5.8 完整分卷 r1/r2，也可覆盖 GitHub 版的旧 Doorstop 兼容补丁。
安装前正常退出游戏。将本 ZIP 内的文件和文件夹直接解压到 WorldApart.exe 所在的游戏根目录，允许覆盖 winhttp.dll 和 BepInEx.cfg。不要多套一层文件夹。随后直接从 Steam 启动游戏；无需运行任何检查脚本。

修复内容：旧启动器仅识别作者电脑的 E 盘绝对路径，其他安装路径无法加载 BepInEx。新版本按 WorldApart.exe 文件名识别，并同时提供 BepInEx 首次运行所需的 Unity 2022.3.43 基础库缓存。

winhttp.dll SHA-256: 9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b
本补丁不包含游戏本体、MOD 主程序、模型或 NPC 参考音。修改后的 UnityDoorstop 源码及 LGPL-2.1 许可随包提供。
'@ | Set-Content -LiteralPath (Join-Path $stage '安装说明.txt') -Encoding utf8

Compress-Archive -LiteralPath (Join-Path $stage 'winhttp.dll'),(Join-Path $stage 'BepInEx'),(Join-Path $stage 'A1IndexTTSMod'),(Join-Path $stage '安装说明.txt') -DestinationPath $archive -CompressionLevel Optimal
Write-Output (Get-Item -LiteralPath $archive | Select-Object FullName,Length)
Write-Output "SHA-256: $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
