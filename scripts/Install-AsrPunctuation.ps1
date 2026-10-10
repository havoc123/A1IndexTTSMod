[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $GameRoot,
    [string] $ModelFile = ''
)
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'WorldApart.exe'))) { throw '未找到 WorldApart.exe。' }
if (Get-Process WorldApart -ErrorAction SilentlyContinue) { throw '请先正常退出游戏。' }
$plugin = Join-Path $GameRoot 'BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll'
if (-not (Test-Path -LiteralPath $plugin) -or [Reflection.AssemblyName]::GetAssemblyName($plugin).Version -lt [Version]'0.7.8.0') { throw '请先升级主程序到 0.7.8 或以上。' }
$name = 'punctuation-ct-transformer-zh-en-int8'
$expected = '65a3fb9f5ad7bfb96bf69e0dc4481df97f6ee60513c1d94ce981ba6effd524b1'
$base = Join-Path $GameRoot 'A1IndexTTSMod/asr'
$target = Join-Path $base $name
$installed = Join-Path $target 'model.int8.onnx'
if ((Test-Path -LiteralPath $installed) -and (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected -and
    (Test-Path -LiteralPath (Join-Path $target 'LICENSE-Apache-2.0.txt')) -and (Test-Path -LiteralPath (Join-Path $target 'NOTICE.txt'))) {
    Write-Host '自动标点模型已经完整安装。'; return
}
$bundled = Join-Path $PSScriptRoot ('A1IndexTTSMod/asr/'+$name+'/model.int8.onnx')
if (-not $ModelFile -and (Test-Path -LiteralPath $bundled)) { $ModelFile = $bundled }
$temp = Join-Path $env:TEMP ('a1-punctuation-'+[guid]::NewGuid().ToString('N'))
$stage = Join-Path $base ($name+'.staging-'+[guid]::NewGuid().ToString('N'))
$backup = Join-Path $base ($name+'.previous-'+[guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    if (-not $ModelFile) {
        $archive = Join-Path $temp 'punctuation.tar.bz2'
        $url = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/punctuation-models/sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8.tar.bz2'
        Write-Host '下载共享 INT8 标点模型（约 65 MB）...'
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'c0d5aa5f8eeb686032345e180bedf39319dc2e0556781c6264bcadba8328a6e1') { throw '标点压缩包 SHA256 校验失败。' }
        & tar.exe -xjf $archive -C $temp
        if ($LASTEXITCODE -ne 0) { throw '标点包解压失败。' }
        $ModelFile = Join-Path $temp 'sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8/model.int8.onnx'
    }
    if ((Get-FileHash -LiteralPath $ModelFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw '标点模型 SHA256 校验失败，原有安装未改动。' }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -LiteralPath $ModelFile -Destination (Join-Path $stage 'model.int8.onnx')
    $licenses = Join-Path $PSScriptRoot 'licenses/asr-punctuation'
    if (-not (Test-Path -LiteralPath $licenses)) { $licenses = Join-Path $PSScriptRoot '../licenses/asr-punctuation' }
    foreach ($file in @('LICENSE-Apache-2.0.txt','NOTICE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $licenses $file))) { throw "安装器缺少许可证文件：$file" }
        Copy-Item -LiteralPath (Join-Path $licenses $file) -Destination $stage
    }
    if ((Get-FileHash -LiteralPath (Join-Path $stage 'model.int8.onnx') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw '标点模型暂存校验失败。' }
    $hadPrevious = Test-Path -LiteralPath $target
    if ($hadPrevious) { Move-Item -LiteralPath $target -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $target }
    catch { if ($hadPrevious -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }; throw }
    Write-Host '自动标点安装完成，下次启动游戏随 ASR 预热。14M 和 160M 共用此模型。'
    if ($hadPrevious) { Write-Host "旧文件备份：$backup" }
} finally {
    foreach ($cleanup in @($temp,$stage)) {
        $resolved = [IO.Path]::GetFullPath($cleanup)
        $allowed = if ($cleanup -eq $temp) { [IO.Path]::GetFullPath($env:TEMP) } else { [IO.Path]::GetFullPath($base) }
        if (-not $resolved.StartsWith($allowed.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw '清理路径越界。' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
