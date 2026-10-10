[CmdletBinding()]
param(
    [string] $ModelFile,
    [string] $OutputRoot = (Join-Path $PSScriptRoot '../dist')
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $project 'src/A1IndexTTSMod.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $ModelFile) { $ModelFile = Join-Path $project '.cache/asr-punctuation/sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8/model.int8.onnx' }
if ((Get-FileHash -LiteralPath $ModelFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne '65a3fb9f5ad7bfb96bf69e0dc4481df97f6ee60513c1d94ce981ba6effd524b1') { throw 'Pinned punctuation model checksum mismatch.' }
$name = "A1IndexTTSMod-v$version-ASR-Punctuation-win64"
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$stage = Join-Path $OutputRoot $name
$archive = $stage + '.zip'
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $archive)) { throw 'Package already exists.' }
$model = Join-Path $stage 'A1IndexTTSMod/asr/punctuation-ct-transformer-zh-en-int8'
New-Item -ItemType Directory -Path $model -Force | Out-Null
Copy-Item -LiteralPath $ModelFile -Destination (Join-Path $model 'model.int8.onnx')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-AsrPunctuation.ps1') -Destination $stage
New-Item -ItemType Directory -Path (Join-Path $stage 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'licenses/asr-punctuation') -Destination (Join-Path $stage 'licenses') -Recurse
[IO.File]::WriteAllText((Join-Path $stage '安装自动标点.bat'), "@echo off`r`nset /p game=请输入游戏文件夹路径: `r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-AsrPunctuation.ps1`" -GameRoot `"%game%`"`r`npause`r`n", [Text.Encoding]::GetEncoding(936))
'先升级主程序至 0.7.8，再关闭游戏安装。此包仅增加标点模型，不包含 ASR 声学模型或 GPU 运行库。14M 和 160M 共用一份模型，以独立 CPU 单线程处理文字。已有 ASR 用户无需重新下载声学模型。' | Set-Content (Join-Path $stage '说明.txt') -Encoding utf8
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { [ordered]@{path=$_.FullName.Substring($stage.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
$files | ConvertTo-Json | Set-Content (Join-Path $stage 'files.json') -Encoding utf8
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Built punctuation add-on: $archive"
