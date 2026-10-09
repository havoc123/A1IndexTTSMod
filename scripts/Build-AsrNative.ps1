[CmdletBinding()]
param([ValidateRange(1,32)][int] $Parallel = 8)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$commit = '11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf'
$archiveSha = '0a8db6c55dd318f4a688faba85f7760b99a6c92e8ef8864479d418531bee1ac2'
$cache = Join-Path $projectRoot '.cache/sherpa-native-build'
$source = Join-Path $cache ('sherpa-onnx-' + $commit)
$build = Join-Path $cache 'build'
$output = Join-Path $projectRoot '.state/asr-native'
$patch = Join-Path $projectRoot 'native/asr/sherpa-onnx-1.13.8-hotwords.patch'
New-Item -ItemType Directory -Path $cache,$output -Force | Out-Null
$archive = Join-Path $cache 'upstream.tar.gz'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://codeload.github.com/k2-fsa/sherpa-onnx/tar.gz/$commit" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveSha) { throw 'Upstream source SHA256 mismatch.' }
if (-not (Test-Path -LiteralPath $source)) {
    & tar.exe -xzf $archive -C $cache
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
}
$relativeSource = ".cache/sherpa-native-build/sherpa-onnx-$commit"
# Only accepts pristine pinned source or this exact patch, never silently edits
# a different local modification. Applying the patch does not alter the Git index.
& git -C $projectRoot apply --reverse --check "--directory=$relativeSource" $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    & git -C $projectRoot apply --check "--directory=$relativeSource" $patch
    if ($LASTEXITCODE -ne 0) { throw 'Source differs from pinned upstream and the maintained patch.' }
    & git -C $projectRoot apply "--directory=$relativeSource" $patch
    if ($LASTEXITCODE -ne 0) { throw 'Applying native ASR patch failed.' }
}
$cmake = (Get-Command cmake.exe -ErrorAction Stop).Source
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
if (-not $vs) { throw 'Visual Studio C++ build tools are required.' }
$major = [int]($vs.installationVersion.Split('.')[0])
$generator = if ($major -ge 18) { 'Visual Studio 18 2026' } elseif ($major -eq 17) { 'Visual Studio 17 2022' } else { throw 'Visual Studio 2022 or newer is required.' }
$options = @('-DSHERPA_ONNX_ENABLE_GPU=ON','-DSHERPA_ONNX_ENABLE_C_API=ON',
    '-DSHERPA_ONNX_ENABLE_BINARY=OFF','-DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF',
    '-DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF','-DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF',
    '-DSHERPA_ONNX_ENABLE_TESTS=OFF','-DSHERPA_ONNX_ENABLE_TTS=OFF',
    '-DSHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=OFF')
& $cmake -S $source -B $build -G $generator -A x64 @options
if ($LASTEXITCODE -ne 0) { throw 'Native CMake configuration failed.' }
$env:MSBUILDDISABLENODEREUSE = '1'
& $cmake --build $build --config Release --target sherpa-onnx-c-api --parallel $Parallel -- /nr:false
if ($LASTEXITCODE -ne 0) { throw 'Native ASR build failed.' }
$dll = Join-Path $build 'bin/Release/sherpa-onnx-c-api.dll'
Copy-Item -LiteralPath $dll -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $output 'sherpa-onnx-LICENSE') -Force
[ordered]@{
    upstreamVersion='1.13.8'; upstreamCommit=$commit; upstreamArchiveSha256=$archiveSha
    revision='a1-context-before-topk-finalize-v2'; patchSha256=(Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
    dllSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    architecture='win-x64'; onnxruntime='1.28.2'; cuda='12.x'; cudnn='9.x'
    generator=$generator; builtUtc=[DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
Write-Host "Built native ASR artifact: $output"
