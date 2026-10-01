[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string] $Version = 'v0.5.8'
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$name = "A1IndexTTSMod-$Version-win64"
$package = Join-Path $OutputRoot $name
$archive = Join-Path $OutputRoot "$name.zip"

if (Test-Path -LiteralPath $package) { throw "Package directory already exists: $package" }
if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }

$sourceBin = Join-Path $project 'src\bin\Release\net6.0'
$sourceRuntime = Join-Path $project '.cache\audiocpp\runtime'
$sourceNpcs = Join-Path $project 'references\npcs'
$sourceModel = Join-Path $project '.cache\audiocpp\models\IndexTTS2.5-GGUF\index-tts2_5-q8_0.gguf'
$pluginFiles = @('A1IndexTTSMod.dll', 'NAudio.Core.dll', 'NAudio.Wasapi.dll')
$runScripts = @('Run-AudioCppForGame.ps1', 'Start-AudioCpp.ps1', 'Stop-AudioCpp.ps1')

foreach ($path in @(
    (Join-Path $sourceRuntime 'audiocpp_server.exe'),
    (Join-Path $sourceRuntime 'LICENSE'),
    (Join-Path $sourceRuntime 'model_specs'),
    (Join-Path $project 'references\demo.wav'),
    (Join-Path $project 'references\default_male.wav'),
    (Join-Path $project 'references\default_female.wav'),
    (Join-Path $project 'config\emotions.json'),
    (Join-Path $sourceNpcs 'npc_id_name.csv'),
    $sourceModel
)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required release input is missing: $path" }
}
foreach ($namePart in $pluginFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceBin $namePart) -PathType Leaf)) {
        throw "Plugin output is missing: $namePart; build Release first."
    }
}
foreach ($namePart in $runScripts) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $namePart) -PathType Leaf)) {
        throw "Runtime script is missing: $namePart"
    }
}

$wavFiles = @(Get-ChildItem -LiteralPath $sourceNpcs -Filter '*.wav' -File)
if ($wavFiles.Count -eq 0) { throw 'No NPC WAVs found.' }
$csv = @(Import-Csv -LiteralPath (Join-Path $sourceNpcs 'npc_id_name.csv'))
if (($csv[0].PSObject.Properties.Name -join ',') -ne 'npcId,npcName,audio,gender') {
    throw 'NPC CSV columns must be npcId,npcName,audio,gender.'
}
$rows = @{}
foreach ($row in $csv) {
    if ($rows.ContainsKey($row.npcId)) { throw "Duplicate NPC ID in CSV: $($row.npcId)" }
    $rows[$row.npcId] = $row
}
foreach ($wav in $wavFiles) {
    if ($wav.BaseName -notmatch '^\d+$') { throw "Non-numeric NPC WAV: $($wav.Name)" }
    if (-not $rows.ContainsKey($wav.BaseName)) { throw "NPC WAV has no CSV row: $($wav.Name)" }
    if ($rows[$wav.BaseName].audio -eq '0') { throw "NPC WAV is marked audio=0: $($wav.Name)" }
    $stream = [IO.File]::OpenRead($wav.FullName)
    try {
        $header = [byte[]]::new(12)
        if ($stream.Read($header, 0, 12) -ne 12 -or
            [Text.Encoding]::ASCII.GetString($header, 0, 4) -ne 'RIFF' -or
            [Text.Encoding]::ASCII.GetString($header, 8, 4) -ne 'WAVE') {
            throw "Invalid RIFF/WAVE header: $($wav.Name)"
        }
    } finally { $stream.Dispose() }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$game = Join-Path $package 'A1'
$plugin = Join-Path $game 'BepInEx\plugins\A1IndexTTSMod'
$mod = Join-Path $game 'A1IndexTTSMod'
$runtime = Join-Path $mod '.cache\audiocpp\runtime'
$modelDir = Join-Path $mod '.cache\audiocpp\models\IndexTTS2.5-GGUF'
$npcs = Join-Path $mod 'references\npcs'
$scripts = Join-Path $mod 'scripts'
$config = Join-Path $mod 'config'
New-Item -ItemType Directory -Path $plugin,$runtime,$modelDir,$npcs,$scripts,$config -Force | Out-Null

foreach ($namePart in $pluginFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceBin $namePart) -Destination (Join-Path $plugin $namePart)
}
foreach ($entry in Get-ChildItem -LiteralPath $sourceRuntime -Force) {
    Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $runtime $entry.Name) -Recurse -Force
}
foreach ($wav in $wavFiles) {
    Copy-Item -LiteralPath $wav.FullName -Destination (Join-Path $npcs $wav.Name)
}
Copy-Item -LiteralPath (Join-Path $sourceNpcs 'npc_id_name.csv') -Destination (Join-Path $npcs 'npc_id_name.csv')
Copy-Item -LiteralPath (Join-Path $project 'references\demo.wav') -Destination (Join-Path $mod 'references\demo.wav')
Copy-Item -LiteralPath (Join-Path $project 'references\default_male.wav') -Destination (Join-Path $mod 'references\default_male.wav')
Copy-Item -LiteralPath (Join-Path $project 'references\default_female.wav') -Destination (Join-Path $mod 'references\default_female.wav')
Copy-Item -LiteralPath (Join-Path $project 'config\emotions.json') -Destination (Join-Path $config 'emotions.json')
foreach ($namePart in $runScripts) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $namePart) -Destination (Join-Path $scripts $namePart)
}
foreach ($doc in @('README.md', 'INSTALL.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $project $doc) -Destination (Join-Path $package $doc)
}
New-Item -ItemType Directory -Path (Join-Path $package 'third_party') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'third_party\NAudio-LICENSE.txt') -Destination (Join-Path $package 'third_party\NAudio-LICENSE.txt')

$forbidden = @('.state', 'src', '.git', 'downloads', 'nuget-packages', 'qwen3-tts-12hz-1.7b-voicedesign')
$unexpected = @(Get-ChildItem -LiteralPath $package -Directory -Recurse -Force | Where-Object { $_.Name -in $forbidden })
if ($unexpected.Count -gt 0) { throw "Forbidden release directory: $($unexpected[0].FullName)" }
$packagedWavs = @(Get-ChildItem -LiteralPath $npcs -Filter '*.wav' -File)
if ($packagedWavs.Count -ne $wavFiles.Count) { throw 'NPC WAV count changed during packaging.' }
if (Test-Path -LiteralPath (Join-Path $modelDir 'index-tts2_5-q8_0.gguf')) {
    throw 'Q8 model must remain a separate download.'
}

$modelHash = (Get-FileHash -LiteralPath $sourceModel -Algorithm SHA256).Hash.ToLowerInvariant()
@"
Release: $Version
NPC WAV files: $($packagedWavs.Count)
Model is not included. Download the IndexTTS2.5 Q8 GGUF and place it at:
A1/A1IndexTTSMod/.cache/audiocpp/models/IndexTTS2.5-GGUF/index-tts2_5-q8_0.gguf
Expected local Q8 SHA-256: $modelHash
"@ | Set-Content -LiteralPath (Join-Path $package 'PACKAGE-MANIFEST.txt') -Encoding utf8

Compress-Archive -LiteralPath $game,(Join-Path $package 'README.md'),(Join-Path $package 'INSTALL.md'),(Join-Path $package 'LICENSE'),(Join-Path $package 'THIRD_PARTY_NOTICES.md'),(Join-Path $package 'CHANGELOG.md'),(Join-Path $package 'third_party'),(Join-Path $package 'PACKAGE-MANIFEST.txt') `
    -DestinationPath $archive -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    PackageDirectory = $package
    Archive = $archive
    ArchiveSha256 = $zipHash
    NpcWavCount = $packagedWavs.Count
    ModelSha256 = $modelHash
} | Format-List
