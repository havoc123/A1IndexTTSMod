[CmdletBinding()]
param([string] $GameRoot = 'E:\Program Files (x86)\Steam\steamapps\common\A1',
    [ValidateSet('lightweight14m','accurate160m')][string] $Profile = 'lightweight14m',
    [ValidateSet('CUDA','DirectML')][string] $Provider = 'CUDA')

$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (Get-Process WorldApart -ErrorAction SilentlyContinue) { throw 'Close WorldApart before installing ASR.' }
if ($Provider -eq 'DirectML' -and $Profile -ne 'lightweight14m') { throw 'DirectML only permits the 14M model.' }
$artifact = Join-Path $PSScriptRoot $(if ($Provider -eq 'DirectML') { '../.state/asr-native-directml' } else { '../.state/asr-native' })
$hasArtifact = Test-Path -LiteralPath (Join-Path $artifact 'manifest.json')
$installedRuntime = Join-Path $GameRoot $(if ($Provider -eq 'DirectML') { 'A1IndexTTSMod/asr/runtime-directml' } else { 'A1IndexTTSMod/asr/runtime' })
if (-not $hasArtifact) {
    $installedManifest = Join-Path $installedRuntime 'a1-native-manifest.json'
    if (-not (Test-Path -LiteralPath $installedManifest)) { throw 'Install the prebuilt ASR optional package first, or build the native artifact.' }
    $installed = Get-Content -LiteralPath $installedManifest -Raw | ConvertFrom-Json
    if ($installed.revision -ne 'a1-context-before-topk-finalize-v3' -or $installed.provider -ne $Provider -or
        $installed.dllSha256 -ne (Get-FileHash -LiteralPath (Join-Path $installedRuntime 'sherpa-onnx-c-api.dll') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Installed native ASR runtime validation failed.' }
}
$base = Join-Path $GameRoot 'A1IndexTTSMod\asr'
$model = Join-Path $base 'sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23'
$runtime = Join-Path $base $(if ($Provider -eq 'DirectML') { 'runtime-directml' } else { 'runtime' })
$cache = Join-Path $GameRoot 'A1IndexTTSMod\.cache\asr-download'
$temp = Join-Path $env:TEMP ('sherpa-onnx-1.13.8-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $model,$runtime,$cache,$temp -Force | Out-Null

$repo = 'https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23/resolve/main'
$files = @('encoder-epoch-99-avg-1.onnx','decoder-epoch-99-avg-1.onnx','joiner-epoch-99-avg-1.onnx','tokens.txt')
$modelChecks = @{
    'encoder-epoch-99-avg-1.onnx' = @{ bytes=40948171; sha256='84c6a8f372686faa5b8f45f2d79f0816f76dcd9f547acb9a90eba2772d7eda8b' }
    'decoder-epoch-99-avg-1.onnx' = @{ bytes=7509745; sha256='5ee0f03a2768ff1d5c83ef3a493243c7935d316cd41280037b14783a3467cc78' }
    'joiner-epoch-99-avg-1.onnx' = @{ bytes=7109975; sha256='030212efaea9a8b6a4fa98faf6ac6055529c4408cf4865e898220ddd02780f34' }
    'tokens.txt' = @{ bytes=48697; sha256='8b294db9045d6e5f94647f4c1eec1af4da143a75053c399611444b378ff966ac' }
}
if ($Profile -eq 'accurate160m') {
    $model = Join-Path $base 'sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30'
    $repo = 'https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30/resolve/2501d7dbcc440fab07cf94ece62833b94a903c13'
    $files = @('encoder.fp16.onnx','decoder.fp16.onnx','joiner.fp16.onnx','tokens.txt','bpe.model')
    $modelChecks = @{
        'encoder.fp16.onnx' = @{bytes=309439670; sha256='7391045897bee71f564afcf97c6e59f8cc1aba9b6f753c595f0bb4e69a52163f'}
        'decoder.fp16.onnx' = @{bytes=2584448; sha256='e0f879ae5a563e0abbff73f8a11272c9055ca13d140d455a6541af0da23403e7'}
        'joiner.fp16.onnx' = @{bytes=2052890; sha256='f321e25ff996a3f47047b010609e735fe429a26e9ffde53dcaff1ef31a2b7357'}
        'tokens.txt' = @{bytes=20628; sha256='6193c7ea1c96d0d9a1e9652789b40d13a8a913b434a5451e93158f5a09fd6652'}
        'bpe.model' = @{bytes=263956; sha256='867a7355801cb43939962ad757ba1cb7941b6171b5a6902772483b4e3a623377'}
    }
}
$modelStage = Join-Path $temp 'model'
New-Item -ItemType Directory -Path $modelStage -Force | Out-Null
foreach ($name in $files) {
    $dest = Join-Path $model $name
    $valid = Test-Path -LiteralPath $dest
    if ($valid) {
        $item = Get-Item -LiteralPath $dest
        $valid = $item.Length -eq $modelChecks[$name].bytes
        if ($valid -and $modelChecks[$name].sha256) { $valid = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() -eq $modelChecks[$name].sha256 }
    }
    if (-not $valid) {
        Write-Host "Downloading $Profile/$name"
        $fileUrl = "$repo/$name"
        if ($Profile -eq 'accurate160m' -and $name -eq 'bpe.model') {
            # Same author's XL vocabulary is identical, including all 2000 model token IDs.
            $fileUrl = 'https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-zh-xlarge-fp16-2025-06-30/resolve/0128977216bda3dc2b7d70178be8e721c0e49b8b/bpe.model'
        }
        Invoke-WebRequest -Uri $fileUrl -OutFile (Join-Path $modelStage $name)
    } else {
        Copy-Item -LiteralPath $dest -Destination (Join-Path $modelStage $name)
    }
    $staged = Join-Path $modelStage $name
    $item = Get-Item -LiteralPath $staged
    if ($item.Length -ne $modelChecks[$name].bytes) { throw "Model file size check failed: $name" }
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant() -ne $modelChecks[$name].sha256) { throw "Model SHA256 check failed: $name" }
}
# Publish a complete, verified directory on the game volume. Keep the previous
# directory until replacement succeeds, so an interrupted download cannot leave
# a profile made of old and new weights. Run the installer with the game closed.
$publishStage = $model + '.staging-' + [guid]::NewGuid().ToString('N')
$previousModel = $model + '.previous-' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $publishStage -Force | Out-Null
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $modelStage $name) -Destination (Join-Path $publishStage $name) }
$hadPrevious = Test-Path -LiteralPath $model
if ($hadPrevious) { Move-Item -LiteralPath $model -Destination $previousModel }
try { Move-Item -LiteralPath $publishStage -Destination $model }
catch {
    if ($hadPrevious -and -not (Test-Path -LiteralPath $model)) { Move-Item -LiteralPath $previousModel -Destination $model }
    throw
}
if ($hadPrevious) {
    $resolvedPrevious = [IO.Path]::GetFullPath($previousModel)
    $resolvedBase = [IO.Path]::GetFullPath($base).TrimEnd('\') + '\'
    if (-not $resolvedPrevious.StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Previous model cleanup path is outside ASR base.' }
    Remove-Item -LiteralPath $resolvedPrevious -Recurse -Force
}

if ($Provider -eq 'CUDA') {
$archive = Join-Path $cache 'sherpa-onnx-v1.13.8-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2'
$url = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/sherpa-onnx-v1.13.8-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2'
$needed = @('sherpa-onnx-c-api.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','onnxruntime_providers_cuda.dll')
if (@($needed | Where-Object { -not (Test-Path -LiteralPath (Join-Path $runtime $_)) }).Count -gt 0) {
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
    $expected = '066c5b54dbafaa1388001a9c9837ac1374dbba6d6678f193ca06aa0d8e94d8c3'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'sherpa-onnx CUDA archive SHA256 check failed.' }
    & tar.exe -xjf $archive -C $temp
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the official sherpa-onnx CUDA archive.' }
    foreach ($name in $needed) {
        $found = Get-ChildItem -LiteralPath $temp -Recurse -File -Filter $name | Select-Object -First 1
        if (-not $found) { throw "Required CUDA runtime DLL missing from official archive: $name" }
        Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $runtime $name) -Force
    }
}
$cudnn = Join-Path $cache 'cudnn-windows-x86_64-9.14.0.64_cuda12-archive.zip'
$cudnnUrl = 'https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.14.0.64_cuda12-archive.zip'
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'cudnn64_9.dll'))) {
    if (-not (Test-Path -LiteralPath $cudnn)) { Invoke-WebRequest -Uri $cudnnUrl -OutFile $cudnn }
    $cudnnSha = '27db1cf50f830a1335de991cf1edde43afb4fc65c365dbf013245f1364ec2b12'
    if ((Get-FileHash -LiteralPath $cudnn -Algorithm SHA256).Hash.ToLowerInvariant() -ne $cudnnSha) { throw 'NVIDIA cuDNN 9.14 CUDA 12 archive SHA256 check failed.' }
    & tar.exe -xf $cudnn -C $temp
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the official NVIDIA cuDNN archive.' }
    $cudnnBin = Get-ChildItem -LiteralPath $temp -Directory -Recurse | Where-Object Name -eq 'bin' | Select-Object -First 1
    if (-not $cudnnBin) { throw 'No bin directory found in the cuDNN archive.' }
    $dlls = Get-ChildItem -LiteralPath $cudnnBin.FullName -File -Filter '*.dll'
    if (-not ($dlls.Name -contains 'cudnn64_9.dll')) { throw 'cudnn64_9.dll was not found in the official cuDNN archive.' }
    foreach ($dll in $dlls) { Copy-Item -LiteralPath $dll.FullName -Destination $runtime -Force }
    $eula = Get-ChildItem -LiteralPath $temp -File -Include '*LICENSE*','*EULA*' -Recurse | Select-Object -First 1
    if ($eula) { Copy-Item -LiteralPath $eula.FullName -Destination (Join-Path $runtime 'LICENSE-NVIDIA-cuDNN.txt') -Force }
}
}
if ($hasArtifact) { & (Join-Path $PSScriptRoot 'Install-AsrNative.ps1') -GameRoot $GameRoot -ArtifactDirectory $artifact }
$license = Join-Path $runtime 'LICENSE-sherpa-onnx.txt'
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v1.13.8/LICENSE' -OutFile $license
if ($Profile -eq 'lightweight14m') {
    Invoke-WebRequest -Uri 'https://www.apache.org/licenses/LICENSE-2.0.txt' -OutFile (Join-Path $model 'LICENSE-Apache-2.0.txt')
}
$manifest = @($files | ForEach-Object {
    $item = Get-Item -LiteralPath (Join-Path $model $_)
    [pscustomobject]@{ file=$item.Name; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $model 'installed-model-files.json') -Encoding utf8
$resolvedTemp = [IO.Path]::GetFullPath($temp)
$resolvedTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $resolvedTemp.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary cleanup path is outside TEMP.' }
Remove-Item -LiteralPath $temp -Recurse -Force
if ($Provider -eq 'CUDA') { Remove-Item -LiteralPath $archive,$cudnn -Force -ErrorAction SilentlyContinue }
Write-Host "ASR model installed at $model"
Write-Host "sherpa-onnx 1.13.8 $Provider runtime installed at $runtime"
if ($Provider -eq 'CUDA') { Write-Host 'The NVIDIA driver and CUDA 12.x runtime must be available on this PC; cuDNN 9.14 CUDA 12 DLLs were installed app-local.' }
