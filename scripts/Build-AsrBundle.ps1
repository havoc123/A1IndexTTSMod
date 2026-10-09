[CmdletBinding()]
param(
    [ValidateSet('CUDA','DirectML')][string] $Provider = 'DirectML',
    [string] $GameRoot = 'E:\Program Files (x86)\Steam\steamapps\common\A1',
    [string] $OutputRoot = (Join-Path $PSScriptRoot '../dist')
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $project 'src/A1IndexTTSMod.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$suffix = if ($Provider -eq 'DirectML') { '-directml' } else { '' }
$artifact = Join-Path $project ('.state/asr-native' + $suffix)
$manifest = Get-Content (Join-Path $artifact 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.provider -ne $Provider -or $manifest.revision -ne 'a1-context-before-topk-finalize-v3') { throw 'Build the matching v3 native artifact first.' }
foreach ($file in $manifest.runtimeFiles) {
    if ((Get-FileHash -LiteralPath (Join-Path $artifact $file.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Artifact checksum mismatch: $($file.file)" }
}
$name = "A1IndexTTSMod-v$version-ASR-$Provider-" + '14M' + '-win64'
$stage = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $name
if (Test-Path -LiteralPath $stage) { throw "Package already exists: $stage" }
$payload = Join-Path $stage 'A1IndexTTSMod/asr'
$runtimeName = if ($Provider -eq 'DirectML') { 'runtime-directml' } else { 'runtime' }
$runtime = Join-Path $payload $runtimeName
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
if ($Provider -eq 'CUDA') {
    $installed = Join-Path $GameRoot 'A1IndexTTSMod/asr/runtime'
    Get-ChildItem -LiteralPath $installed -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -match 'LICENSE|ThirdParty' } | Copy-Item -Destination $runtime
    $paths = @($installed,$GameRoot) + @($env:PATH.Split([IO.Path]::PathSeparator))
    foreach ($dll in @('cudnn64_9.dll','cublas64_12.dll','cublasLt64_12.dll','cudart64_12.dll')) {
        $found = $paths | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ $dll)) } | Select-Object -First 1
        if (-not $found) { throw "Missing app-local CUDA dependency: $dll" }
        Copy-Item -LiteralPath (Join-Path $found $dll) -Destination $runtime -Force
    }
}
Get-ChildItem -LiteralPath $artifact -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -match 'LICENSE|ThirdParty' } | Copy-Item -Destination $runtime -Force
Copy-Item -LiteralPath (Join-Path $artifact 'manifest.json') -Destination (Join-Path $runtime 'a1-native-manifest.json')
$models = @('sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23')
foreach ($model in $models) {
    $source = Join-Path $GameRoot ('A1IndexTTSMod/asr/' + $model)
    Copy-Item -LiteralPath $source -Destination $payload -Recurse
}
$config = Join-Path $stage 'A1IndexTTSMod/config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'config/asr-hotwords.zh-CN.txt') -Destination $config
$files = @(Get-ChildItem -LiteralPath (Join-Path $stage 'A1IndexTTSMod') -Recurse -File | ForEach-Object {
    [ordered]@{ path=$_.FullName.Substring($stage.Length+1); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$files | ConvertTo-Json | Set-Content (Join-Path $stage 'files.json') -Encoding utf8
$installer = @'
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $GamePath)
$ErrorActionPreference = 'Stop'
$game = [IO.Path]::GetFullPath($GamePath.Trim().Trim('"'))
if (Test-Path -LiteralPath $game -PathType Leaf) { $game = Split-Path -Parent $game }
if (-not (Test-Path -LiteralPath (Join-Path $game 'WorldApart.exe'))) { throw '未找到 WorldApart.exe。' }
if (Get-Process WorldApart -ErrorAction SilentlyContinue) { throw '请先正常退出游戏。' }
$plugin = Join-Path $game 'BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll'
if (-not (Test-Path -LiteralPath $plugin) -or [Reflection.AssemblyName]::GetAssemblyName($plugin).Version -lt [Version]'0.7.7.0') { throw '请先安装 0.7.7 或以上版本主程序。' }
$files = Get-Content (Join-Path $PSScriptRoot 'files.json') -Raw | ConvertFrom-Json
foreach ($file in $files) {
    $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $file.path))
    $target = [IO.Path]::GetFullPath((Join-Path $game $file.path))
    if (-not $source.StartsWith($PSScriptRoot.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith((Join-Path $game 'A1IndexTTSMod').TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw '包内路径无效。' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw ('文件校验失败：'+$file.path) }
}
$backup = Join-Path $game ('A1IndexTTSMod/.state/asr-package-backups/'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssffffZ'))
$written = [Collections.Generic.List[string]]::new()
try {
    foreach ($file in $files) {
        $target = Join-Path $game $file.path
        if ($file.path -like '*config*' -and (Test-Path -LiteralPath $target)) { continue }
        if (Test-Path -LiteralPath $target) {
            $saved = Join-Path $backup $file.path
            New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $saved
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $written.Add($file.path)
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file.path) -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw '安装后校验失败。' }
    }
} catch {
    foreach ($path in $written) {
        $saved = Join-Path $backup $path; $target = Join-Path $game $path
        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    throw
}
Write-Host 'ASR 可选包安装完成，下次启动游戏会自动检测并预热。'
'@
[IO.File]::WriteAllText((Join-Path $stage 'Install-AsrPackage.ps1'), $installer, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText((Join-Path $stage '安装ASR.bat'), "@echo off`r`nset /p game=请输入游戏文件夹路径: `r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-AsrPackage.ps1`" -GamePath `"%game%`"`r`npause`r`n", [Text.Encoding]::GetEncoding(936))
"ASR 可选包 $version / $Provider。先升级主程序，再关闭游戏安装此包。DirectML 固定 14M，适用于 AMD/Intel DirectX 12 显卡；NVIDIA 推荐 CUDA 包。未安装时不显示麦克风和语音输入页。不同运行库切换需要重启游戏。热词文件已有修改会保留。" | Set-Content (Join-Path $stage '说明.txt') -Encoding utf8
if ($Provider -eq 'CUDA') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ASR.ps1') -Destination (Join-Path $stage 'Download-AsrModel.ps1')
    [IO.File]::WriteAllText((Join-Path $stage '下载160M模型.bat'), "@echo off`r`nset /p game=请输入游戏文件夹路径: `r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Download-AsrModel.ps1`" -GameRoot `"%game%`" -Profile accurate160m -Provider CUDA`r`npause`r`n", [Text.Encoding]::GetEncoding(936))
}
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
& $sevenZip a -t7z -mx=3 (Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ($name+'.7z')) ($stage+'\*')
if ($LASTEXITCODE -ne 0) { throw 'ASR archive creation failed.' }
Write-Host "Built optional ASR package: $stage"
