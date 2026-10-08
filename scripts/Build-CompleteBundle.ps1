[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string] $Version = 'v0.5.8',
    [string] $PackageRevision = 'r3',
    [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe'
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$releaseGame = Join-Path $project "dist\A1IndexTTSMod-$Version-win64\A1"
$model = Join-Path $project '.cache\audiocpp\models\IndexTTS2.5-GGUF\index-tts2_5-q8_0.gguf'
$unityBaseZip = Join-Path $project '.cache\bepinex\2022.3.43.zip'
$bepinexArchive = Join-Path $project '.cache\6.0.0-be.788+5b766a3\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip'
$proxy = Join-Path $project '.cache\doorstop\winhttp.dll'
$doorstopSource = Join-Path $project '.state\unitydoorstop-v4.5.0-src\UnityDoorstop-4.5.0'
$modelLicense = 'D:\project\fishs2\indextts25\index-tts\LICENSE'
$stageRoot = Join-Path $OutputRoot "A1IndexTTSMod-$Version-complete-$PackageRevision-staging"
$stage = Join-Path $stageRoot 'game-root'
$archive = Join-Path $OutputRoot "A1IndexTTSMod-$Version-complete-$PackageRevision-win64.7z"

foreach ($p in @($SevenZip,$releaseGame,$model,$unityBaseZip,$bepinexArchive,$proxy,$doorstopSource,$modelLicense,
    (Join-Path $project 'packaging\INSTALL-COMPLETE.md'),
    (Join-Path $project 'packaging\THIRD_PARTY_COMPLETE.md'))) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required input is missing: $p" }
}
if (Test-Path -LiteralPath $stageRoot) { throw "Staging directory already exists: $stageRoot" }
if (@(Get-ChildItem -LiteralPath $OutputRoot -File -Filter "$(Split-Path -Leaf $archive).*" -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Archive volumes already exist for: $archive"
}
function Assert-Hash([string] $Path, [string] $Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "SHA-256 mismatch for $Path`: $actual" }
}
Assert-Hash $bepinexArchive 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'
Assert-Hash $proxy '9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b'
Assert-Hash $model '5e827b2072042e4a1b21ccf24a5cb4f71cb1011403067a0a9b039311d8b38628'
Assert-Hash $unityBaseZip '45d51c23363ac0abbf8fdb31f5f85992e8afd1d13552759809d66142f7cbbd3f'

New-Item -ItemType Directory -Path $stage -Force | Out-Null
& $SevenZip x "-o$stage" -y $bepinexArchive | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not extract BepInEx: $LASTEXITCODE" }

foreach ($file in Get-ChildItem -LiteralPath $releaseGame -Recurse -File -Force) {
    $relative = $file.FullName.Substring($releaseGame.Length).TrimStart('\')
    $destination = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
Copy-Item -LiteralPath $proxy -Destination (Join-Path $stage 'winhttp.dll') -Force
$unityDestination = Join-Path $stage 'BepInEx\unity-libs\2022.3.43.zip'
New-Item -ItemType Directory -Path (Split-Path -Parent $unityDestination) -Force | Out-Null
Copy-Item -LiteralPath $unityBaseZip -Destination $unityDestination
$bepinexConfig = Join-Path $stage 'BepInEx\config\BepInEx.cfg'
New-Item -ItemType Directory -Path (Split-Path -Parent $bepinexConfig) -Force | Out-Null
@'
[IL2CPP]
UnityBaseLibrariesSource = 2022.3.43.zip

[Logging.Console]
Enabled = true

[Logging.Disk]
Enabled = true
'@ | Set-Content -LiteralPath $bepinexConfig -Encoding ascii

$modelDestination = Join-Path $stage 'A1IndexTTSMod\.cache\audiocpp\models\IndexTTS2.5-GGUF\index-tts2_5-q8_0.gguf'
New-Item -ItemType Directory -Path (Split-Path -Parent $modelDestination) -Force | Out-Null
New-Item -ItemType HardLink -Path $modelDestination -Target $model | Out-Null

$thirdParty = Join-Path $stage 'A1IndexTTSMod\third_party'
$modifiedSource = Join-Path $thirdParty 'UnityDoorstop-A1-source'
New-Item -ItemType Directory -Path $modifiedSource -Force | Out-Null
Copy-Item -LiteralPath $modelLicense -Destination (Join-Path $thirdParty 'IndexTTS-2.5-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $doorstopSource 'LICENSE') -Destination (Join-Path $thirdParty 'BepInEx-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $doorstopSource 'LICENSE') -Destination (Join-Path $thirdParty 'UnityDoorstop-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $project 'third_party\NAudio-LICENSE.txt') -Destination (Join-Path $thirdParty 'NAudio-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $project 'packaging\THIRD_PARTY_COMPLETE.md') -Destination (Join-Path $thirdParty 'THIRD_PARTY_COMPLETE.md')
foreach ($dir in @('src','assets')) {
    Copy-Item -LiteralPath (Join-Path $doorstopSource $dir) -Destination (Join-Path $modifiedSource $dir) -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $modifiedSource 'build') -Force | Out-Null
foreach ($name in @('dll.def','info.rc','proxy.c')) {
    Copy-Item -LiteralPath (Join-Path $doorstopSource "build\$name") -Destination (Join-Path $modifiedSource "build\$name")
}
foreach ($name in @('build.bat','build.ps1','build.sh','CHANGES.md','info.lua','LICENSE','README.md','xmake.lua')) {
    Copy-Item -LiteralPath (Join-Path $doorstopSource $name) -Destination (Join-Path $modifiedSource $name)
}
Copy-Item -LiteralPath (Join-Path $project 'packaging\INSTALL-COMPLETE.md') -Destination (Join-Path $stage 'INSTALL.md')

$wav = @(Get-ChildItem -LiteralPath (Join-Path $stage 'A1IndexTTSMod\references\npcs') -File -Filter '*.wav')
if ($wav.Count -ne 1169) { throw "Expected 1169 NPC WAV files, found $($wav.Count)" }
Assert-Hash (Join-Path $stage 'winhttp.dll') '9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b'
Assert-Hash $unityDestination '45d51c23363ac0abbf8fdb31f5f85992e8afd1d13552759809d66142f7cbbd3f'
if ((Get-Item -LiteralPath $modelDestination).Length -ne 3502955328) { throw 'Model length changed.' }
$forbidden = @(Get-ChildItem -LiteralPath $stage -Recurse -Force | Where-Object {
    $_.Name -in @('.state','interop','LogOutput.log','ErrorLog.log','WorldApart.exe') -or $_.Extension -in @('.jsonl','.gguf.tmp')
})
if ($forbidden.Count -gt 0) { throw "Forbidden package entry: $($forbidden[0].FullName)" }

$inputs = @('.doorstop_version','doorstop_config.ini','winhttp.dll','changelog.txt','BepInEx','dotnet','A1IndexTTSMod','INSTALL.md')
Push-Location $stage
try {
    & $SevenZip a -t7z -mx=1 -m0=lzma2 -ms=off -mmt=on -v3800m $archive @inputs
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed: $LASTEXITCODE" }
} finally { Pop-Location }

$volumes = @(Get-ChildItem -LiteralPath $OutputRoot -File -Filter "$(Split-Path -Leaf $archive).*" | Sort-Object Name)
if ($volumes.Count -ne 2) { throw "Expected exactly 2 volumes, found $($volumes.Count)" }
if (@($volumes | Where-Object Length -ge 4000000000).Count -gt 0) { throw 'A volume exceeds the 4 GB target.' }
$sumFile = Join-Path $OutputRoot "SHA256SUMS-complete-$PackageRevision.txt"
@($volumes | ForEach-Object { "$(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $($_.Name)" }) |
    Set-Content -LiteralPath $sumFile -Encoding ascii
$volumes | Select-Object Name,Length,FullName | Format-Table
Write-Output "Checksums: $sumFile"
