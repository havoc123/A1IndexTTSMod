[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = Join-Path $project 'dist'
$stage = Join-Path $dist 'A1IndexTTSMod-v0.5.8-complete-r1-to-r2-staging'
$archive = Join-Path $dist 'A1IndexTTSMod-v0.5.8-complete-r1-to-r2.zip'
$source = Join-Path $project '.cache\bepinex\2022.3.43.zip'
if (Test-Path -LiteralPath $stage) { throw "Staging directory already exists: $stage" }
if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing cached Unity libraries: $source" }
$expected = '45d51c23363ac0abbf8fdb31f5f85992e8afd1d13552759809d66142f7cbbd3f'
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Unity base library ZIP hash mismatch.'
}
$config = Join-Path $stage 'BepInEx\config\BepInEx.cfg'
$cache = Join-Path $stage 'BepInEx\unity-libs\2022.3.43.zip'
New-Item -ItemType Directory -Path (Split-Path -Parent $config),(Split-Path -Parent $cache) -Force | Out-Null
@'
[IL2CPP]
UnityBaseLibrariesSource = 2022.3.43.zip

[Logging.Console]
Enabled = true

[Logging.Disk]
Enabled = true
'@ | Set-Content -LiteralPath $config -Encoding ascii
Copy-Item -LiteralPath $source -Destination $cache
$instructions = @'
《不问凡尘》AI NPC 语音 MOD 旧整合包升级补丁

适用于文件名为 A1IndexTTSMod-v0.5.8-complete-win64.7z.001/.002 的旧整合包。
如果已有文件名包含 complete-r2 的新版整合包，不需要此补丁。

1. 先退出游戏。旧整合包的两个分卷必须已经完整解压到 WorldApart.exe 所在目录。
2. 将本补丁 ZIP 中的 BepInEx 文件夹合并到同一游戏根目录。
3. 确认以下两个文件存在：
   BepInEx\config\BepInEx.cfg
   BepInEx\unity-libs\2022.3.43.zip
4. 从 Steam 启动游戏。首次运行仍需在本地生成 IL2CPP 接口，可能等待约 1 分钟；基础库无需联网下载。

如果已经修改过 BepInEx.cfg，请先备份；只需保留其中 [IL2CPP] 的
UnityBaseLibrariesSource = 2022.3.43.zip，并保证控制台日志已启用。

此补丁只补充 BepInEx 首次启动所需基础库和配置，不包含游戏、MOD 主文件、模型或参考音。
Unity 基础库权利归 Unity Technologies，不受本项目 MIT 许可证覆盖。
'@
[IO.File]::WriteAllText((Join-Path $stage '安装补丁.txt'),$instructions,[Text.UTF8Encoding]::new($true))
Compress-Archive -LiteralPath (Join-Path $stage 'BepInEx'),(Join-Path $stage '安装补丁.txt') -DestinationPath $archive -CompressionLevel Optimal
Write-Output (Get-Item -LiteralPath $archive | Select-Object FullName,Length)
Write-Output "SHA-256: $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
