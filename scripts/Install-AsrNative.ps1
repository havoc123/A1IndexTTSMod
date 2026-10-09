[CmdletBinding()]
param([string] $GameRoot = 'E:\Program Files (x86)\Steam\steamapps\common\A1',
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot '../.state/asr-native'))
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Get-Process WorldApart -ErrorAction SilentlyContinue) { throw 'Close WorldApart before installing the ASR native runtime.' }
$artifact = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'manifest.json') -Raw | ConvertFrom-Json
$source = Join-Path $ArtifactDirectory 'sherpa-onnx-c-api.dll'
$patch = Join-Path $PSScriptRoot '../native/asr/sherpa-onnx-1.13.8-hotwords.patch'
if ($artifact.upstreamCommit -ne '11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf' -or $artifact.revision -ne 'a1-context-before-topk-finalize-v2') { throw 'Unexpected native ASR artifact revision.' }
if ($artifact.patchSha256 -ne (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Artifact does not match the current native patch.' }
if ($artifact.dllSha256 -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Native artifact DLL SHA256 mismatch.' }
$runtime = Join-Path $GameRoot 'A1IndexTTSMod/asr/runtime'
foreach ($name in @('sherpa-onnx-c-api.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','onnxruntime_providers_cuda.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtime $name))) { throw "Install the base CUDA ASR runtime first: missing $name" }
}
$target = Join-Path $runtime 'sherpa-onnx-c-api.dll'
$installedManifest = Join-Path $runtime 'a1-native-manifest.json'
$oldHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
if (Test-Path -LiteralPath $installedManifest) {
    $previous = Get-Content -LiteralPath $installedManifest -Raw | ConvertFrom-Json
    if ($previous.dllSha256 -ne $oldHash) { throw 'Installed ASR DLL was changed outside this installer.' }
} elseif ($oldHash -ne '5332afa35b8dc7cb432df015a3664c82b12a056d69b68dc9db6e2d84426a4f73') {
    throw 'Unmanaged native DLL differs from the pinned official CUDA runtime.'
}
$backup = Join-Path $runtime ('.backups/' + $oldHash)
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item -LiteralPath $target -Destination (Join-Path $backup 'sherpa-onnx-c-api.dll') -Force
if (Test-Path -LiteralPath $installedManifest) { Copy-Item -LiteralPath $installedManifest -Destination $backup -Force }
$stage = $target + '.staging-' + [guid]::NewGuid().ToString('N')
Copy-Item -LiteralPath $source -Destination $stage
try {
    Move-Item -LiteralPath $stage -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.dllSha256) { throw 'Installed native DLL checksum mismatch.' }
    Copy-Item -LiteralPath (Join-Path $ArtifactDirectory 'sherpa-onnx-LICENSE') -Destination $runtime -Force
    $artifact | Add-Member -NotePropertyName previousDllSha256 -NotePropertyValue $oldHash -Force
    $artifact | Add-Member -NotePropertyName installedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
    $artifact | ConvertTo-Json | Set-Content -LiteralPath $installedManifest -Encoding utf8
} catch {
    Copy-Item -LiteralPath (Join-Path $backup 'sherpa-onnx-c-api.dll') -Destination $target -Force
    if (Test-Path -LiteralPath (Join-Path $backup 'a1-native-manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $backup 'a1-native-manifest.json') -Destination $installedManifest -Force
    } elseif (Test-Path -LiteralPath $installedManifest) { Remove-Item -LiteralPath $installedManifest -Force }
    throw
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force }
}
Write-Host "Installed $($artifact.revision); previous DLL retained at $backup"
