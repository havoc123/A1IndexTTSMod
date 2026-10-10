[CmdletBinding()]
param(
    [switch] $IncludeAsr,
    [string] $OutputRoot = (Join-Path $PSScriptRoot '../dist')
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $project 'src/A1IndexTTSMod.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$bin = Join-Path $project 'src/bin/Release/net6.0'
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $bin 'A1IndexTTSMod.dll')).Version -ne [Version]($version+'.0')) { throw 'Built plugin does not match source version.' }
$edition = if ($IncludeAsr) { 'with-ASR' } else { 'program' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$name = "A1IndexTTSMod-v$version-overlay-$edition-win64"
$stage = Join-Path $OutputRoot $name
$archive = $stage + '.7z'
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $archive)) { throw 'Output already exists.' }
$payload = Join-Path $stage 'A1'
$plugins = Join-Path $payload 'BepInEx/plugins/A1IndexTTSMod'
$mod = Join-Path $payload 'A1IndexTTSMod'
New-Item -ItemType Directory -Path $plugins,(Join-Path $mod 'scripts'),(Join-Path $mod 'assets/asr') -Force | Out-Null
foreach ($dll in @('A1IndexTTSMod.dll','NAudio.Core.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll','sherpa-onnx.dll')) { Copy-Item -LiteralPath (Join-Path $bin $dll) -Destination $plugins }
Copy-Item -LiteralPath (Join-Path $bin 'LocalModManager.Abstractions.dll') -Destination (Join-Path $payload 'BepInEx/plugins')
foreach ($script in @('Start-AudioCpp.ps1','Run-AudioCppForGame.ps1','Stop-AudioCpp.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $mod 'scripts') }
foreach ($asset in @('microphone-normal.png','microphone-highlight.png')) { Copy-Item -LiteralPath (Join-Path $project ('assets/asr/'+$asset)) -Destination (Join-Path $mod 'assets/asr') }
if ($IncludeAsr) {
    foreach ($provider in @('CUDA','DirectML')) {
        $source = Join-Path $OutputRoot "A1IndexTTSMod-v$version-ASR-$provider-14M-win64"
        $files = Get-Content -LiteralPath (Join-Path $source 'files.json') -Raw | ConvertFrom-Json
        foreach ($file in $files) {
            # Configuration defaults are seeded only when absent by the installer.
            if ($file.path -like '*config*') { continue }
            $from = [IO.Path]::GetFullPath((Join-Path $source $file.path))
            $to = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
            if (-not $from.StartsWith($source.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
                -not $to.StartsWith($payload.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Optional payload path escaped package.' }
            if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $file.sha256) { throw "ASR payload checksum mismatch: $($file.path)" }
            if (Test-Path -LiteralPath $to) {
                if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $file.sha256) { throw "Conflicting shared ASR payload: $($file.path)" }
            } else {
                New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
                Copy-Item -LiteralPath $from -Destination $to
            }
        }
    }
    New-Item -ItemType Directory -Path (Join-Path $stage 'defaults'),(Join-Path $stage 'licenses') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $project 'config/asr-hotwords.zh-CN.txt') -Destination (Join-Path $stage 'defaults')
    Copy-Item -LiteralPath (Join-Path $project 'licenses/asr-punctuation') -Destination (Join-Path $stage 'licenses') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ASR.ps1') -Destination (Join-Path $stage 'Download-AsrModel.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-AsrPunctuation.ps1') -Destination $stage
    [IO.File]::WriteAllText((Join-Path $stage '可选下载160M.bat'),"@echo off`r`nset /p game=请输入游戏文件夹路径: `r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Download-AsrModel.ps1`" -GameRoot `"%game%`" -Profile accurate160m -Provider CUDA`r`npause`r`n",[Text.Encoding]::GetEncoding(936))
}
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object { [ordered]@{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
$files | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'files.json') -Encoding utf8
$installer = @'
[CmdletBinding()]
param([string] $GamePath = '')
$ErrorActionPreference = 'Stop'
if (-not $GamePath) { $GamePath = Read-Host '请输入 WorldApart.exe 所在游戏文件夹（或 exe 完整路径）' }
$game = [IO.Path]::GetFullPath($GamePath.Trim().Trim('"'))
if (Test-Path -LiteralPath $game -PathType Leaf) {
    if ([IO.Path]::GetFileName($game) -ne 'WorldApart.exe') { throw '请选择 WorldApart.exe。' }
    $game = Split-Path -Parent $game
}
if (-not (Test-Path -LiteralPath (Join-Path $game 'WorldApart.exe'))) { throw '未找到 WorldApart.exe。' }
if (Get-Process WorldApart -ErrorAction SilentlyContinue) { throw '请先正常退出游戏并等待 Steam 云同步。' }
$plugin = Join-Path $game 'BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll'
if (-not (Test-Path -LiteralPath $plugin)) { throw '这是覆盖补丁，请先完整安装 CosyVoice 语音 MOD。' }
$version = [Reflection.AssemblyName]::GetAssemblyName($plugin).Version
if ($version -lt [Version]'0.7.4.0' -or $version -gt [Version]'__VERSION__.0') { throw "仅适用于 CosyVoice 0.7.4～__VERSION__，当前插件 $version。" }
foreach ($path in @('BepInEx/core/BepInEx.Unity.IL2CPP.dll','A1IndexTTSMod/.cache/audiocpp/runtime/audiocpp_server.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $game $path))) { throw "现有安装缺少 $path，请先完整安装程序包。" }
}
$duplicates = @(Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx/plugins') -Recurse -File -Filter A1IndexTTSMod.dll | Where-Object { $_.FullName -ne $plugin })
if ($duplicates.Count) { throw ('检测到重复插件，请先处理重复安装：'+($duplicates.FullName -join ', ')) }
$payload = Join-Path $PSScriptRoot 'A1'
$files = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'files.json') -Raw | ConvertFrom-Json
foreach ($file in $files) {
    $from = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
    $to = [IO.Path]::GetFullPath((Join-Path $game $file.path))
    if (-not $from.StartsWith($payload.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        -not $to.StartsWith($game.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $file.path -notmatch '^(BepInEx[\\/]plugins[\\/]|A1IndexTTSMod[\\/](scripts|assets|asr)[\\/])') { throw '包内路径无效。' }
    if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "文件校验失败：$($file.path)，目标未改动。" }
}
$backup = Join-Path $game ('A1IndexTTSMod/.state/overlay-backups/v__VERSION__/'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssffffZ')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$written = [Collections.Generic.List[string]]::new()
try {
    foreach ($file in $files) {
        $target = Join-Path $game $file.path
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256) { continue }
            $saved = Join-Path $backup $file.path
            New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $saved
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $written.Add($file.path)
        Copy-Item -LiteralPath (Join-Path $payload $file.path) -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "安装后校验失败：$($file.path)" }
    }
    $defaults = Join-Path $PSScriptRoot 'defaults/asr-hotwords.zh-CN.txt'
    $hotwords = Join-Path $game 'A1IndexTTSMod/config/asr-hotwords.zh-CN.txt'
    if ((Test-Path -LiteralPath $defaults) -and -not (Test-Path -LiteralPath $hotwords)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $hotwords) -Force | Out-Null
        Copy-Item -LiteralPath $defaults -Destination $hotwords
    }
} catch {
    foreach ($path in $written) {
        $saved = Join-Path $backup $path; $target = Join-Path $game $path
        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    throw
}
Write-Host '已覆盖更新到 __VERSION__。配置、参考音、CosyVoice 及已有 160M 模型保留。'
Write-Host "被替换文件备份：$backup"
Write-Host '下次从 Steam 启动游戏生效。'
'@
$installer = $installer.Replace('__VERSION__',$version)
[IO.File]::WriteAllText((Join-Path $stage 'Install-OverlayPatch.ps1'),$installer,[Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText((Join-Path $stage '安装覆盖补丁.bat'),"@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-OverlayPatch.ps1`" %*`r`npause`r`n",[Text.Encoding]::ASCII)
$asrText = if ($IncludeAsr) { '本包包含 CUDA 与 DirectML 两套 ASR 运行库、14M 流式模型及共享 INT8 标点模型。NVIDIA 自动优先 CUDA；AMD/Intel DirectX 12 自动使用 DirectML 14M。已有 160M 保留；需要新装 160M 的 NVIDIA 用户可使用“可选下载160M.bat”。' } else { '本包仅更新插件与配套脚本，不增加 ASR 模型或运行库。已有 ASR 保留并可继续使用。需要新装 ASR 请使用含 ASR 覆盖补丁。' }
$readme = @"
A1 CosyVoice 语音 MOD $version 覆盖补丁（$edition）

适用：已完整安装 CosyVoice 语音 MOD 0.7.4～$version 的 Windows x64 玩家。包含 0.7.5、0.7.6、0.7.7 升级，以及同版本修复覆盖。不含 BepInEx 加载器、CosyVoice GGUF 和 NPC 参考音，不能用于首次完整安装。

推荐安装：正常退出游戏、等待 Steam 云存档同步，完整解压本包到任意临时文件夹，双击“安装覆盖补丁.bat”，输入 WorldApart.exe 所在文件夹。安装器先检查全部文件，备份被替换文件，再覆盖安装；已有配置与热词保留。安装器拒绝重复插件与更高版本，失败会恢复本次已替换文件。

手工覆盖：将本包 A1 文件夹里面的 BepInEx 和 A1IndexTTSMod 两个文件夹复制到游戏根目录，合并目录并替换同名文件。不要把整个 A1 文件夹再放进游戏目录。手工覆盖不生成备份；含 ASR 包可在覆盖后运行安装器补齐缺少的默认热词。

$asrText

0.7.8 保留强制语音风格提示、预设开场白情感库及 CosyVoice instruct 传输。取消停止/发送前整段录音二次识别，保留流式尾部收尾并支持自动标点。情感提示不能保证上游每句都返回有效风格；缺失时“本轮数据”会如实显示。

从 Steam 重启后，BepInEx/LogOutput.log 应显示 Loading [A1-TTS-Mod $version]。对有可朗读台词的模型回复，本轮诊断摘要会包含 VoiceStyleRequired，PromptAddition 应说明必须返回风格帧，而非旧的“无风格时不追加帧”。

回退：退出游戏，将安装器报告的备份文件复制回原位置。补丁经离线安装验证，未启动游戏测试。
"@
[IO.File]::WriteAllText((Join-Path $stage '覆盖安装说明.txt'),$readme,[Text.UTF8Encoding]::new($true))
& 'C:/Program Files/7-Zip/7z.exe' a -t7z -mx=3 $archive ($stage+'\*')
if ($LASTEXITCODE) { throw 'Archive creation failed.' }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive+'.sha256'),$hash+'  '+[IO.Path]::GetFileName($archive)+"`n",[Text.UTF8Encoding]::new($false))
Write-Host "Built overlay patch: $archive"
