[CmdletBinding()]
param(
    [string] $GameRoot = 'E:\Program Files (x86)\Steam\steamapps\common\A1',
    [string] $Version = 'v0.5.8'
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = Join-Path $project 'dist'
$archive = Join-Path $outputRoot "A1IndexTTSMod-$Version-doorstop-compat-win64.zip"
$stage = Join-Path $outputRoot "A1IndexTTSMod-$Version-doorstop-compat-staging"
$source = Join-Path $project '.state\unitydoorstop-v4.5.0-src\UnityDoorstop-4.5.0'
$proxy = Join-Path $GameRoot 'winhttp.dll'
if (Test-Path -LiteralPath $stage) { throw "Staging directory already exists: $stage" }
if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
foreach ($input in @($source,$proxy)) {
    if (-not (Test-Path -LiteralPath $input)) { throw "Required input is missing: $input" }
}
$expected = 'a2bfe64fd9ae63354ebb5e79be36e7ea29be9021ff6941e82487212a67fd1b6b'
if ((Get-FileHash -LiteralPath $proxy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Doorstop proxy hash does not match the game-tested build.'
}
New-Item -ItemType Directory -Path (Join-Path $stage 'third_party\UnityDoorstop-A1-source\build') -Force | Out-Null
Copy-Item -LiteralPath $proxy -Destination (Join-Path $stage 'winhttp.dll')
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $stage 'third_party\UnityDoorstop-LICENSE.txt')
$dest = Join-Path $stage 'third_party\UnityDoorstop-A1-source'
foreach ($dir in @('src','assets')) {
    Copy-Item -LiteralPath (Join-Path $source $dir) -Destination (Join-Path $dest $dir) -Recurse
}
foreach ($file in @('dll.def','info.rc','proxy.c')) {
    Copy-Item -LiteralPath (Join-Path $source "build\$file") -Destination (Join-Path $dest "build\$file")
}
foreach ($file in @('build.bat','build.ps1','build.sh','CHANGES.md','info.lua','LICENSE','README.md','xmake.lua')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $dest $file)
}
@'
# 游戏加载器兼容补丁

适用于 A1IndexTTSMod v0.5.8 和当前已验证的《不问凡尘》Windows 构建。

1. 退出游戏。先按主安装说明将 BepInEx 6 Unity IL2CPP 官方包解压到 `WorldApart.exe` 所在目录。
2. 将本补丁 ZIP 内的 `winhttp.dll` 和 `third_party` 解压到同一目录，覆盖官方包中的 `winhttp.dll`。
3. 再安装 MOD 主 ZIP 和 IndexTTS 2.5 Q8 模型，随后从 Steam 启动游戏。

此文件是针对游戏加载入口修改的 UnityDoorstop 4.5.0 代理，SHA-256：
`a2bfe64fd9ae63354ebb5e79be36e7ea29be9021ff6941e82487212a67fd1b6b`。
对应修改源码、构建脚本与 LGPL-2.1 许可证随 ZIP 提供于 `third_party/`。
如果你已有经验证可用的同一代理，无需重复覆盖。
'@ | Set-Content -LiteralPath (Join-Path $stage 'INSTALL-DOORSTOP.md') -Encoding utf8
Compress-Archive -LiteralPath (Join-Path $stage 'winhttp.dll'),(Join-Path $stage 'third_party'),(Join-Path $stage 'INSTALL-DOORSTOP.md') -DestinationPath $archive -CompressionLevel Optimal
Write-Output (Get-Item -LiteralPath $archive | Select-Object FullName,Length)
Write-Output (Get-FileHash -LiteralPath $archive -Algorithm SHA256)
