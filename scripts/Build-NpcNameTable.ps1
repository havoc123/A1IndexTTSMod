[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$source = Join-Path (Split-Path -Parent $project) 'A1_人物卡片解包\tbnpcbasecfg.json'
$destination = Join-Path $project 'references\npcs\npc_id_name.csv'
$voiceDir = Split-Path -Parent $destination
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "NPC table not found: $source" }

$previousAudio = @{}
if (Test-Path -LiteralPath $destination -PathType Leaf) {
    foreach ($row in (Import-Csv -LiteralPath $destination -Encoding UTF8)) {
        if ($row.audio -notin @($null, '', '0', '1', '2')) { throw "Invalid audio value for NPC $($row.npcId): $($row.audio)" }
        if ($previousAudio.ContainsKey($row.npcId)) { throw "Duplicate NPC ID in existing CSV: $($row.npcId)" }
        $previousAudio[$row.npcId] = $row.audio
    }
}

$entries = @(Get-Content -LiteralPath $source -Raw -Encoding UTF8 | ConvertFrom-Json |
    Where-Object { $_.id -ge 0 -and -not [string]::IsNullOrWhiteSpace($_.npcName.'zh-Hans') } |
    Sort-Object -Property id)
$ids = @($entries | Select-Object -ExpandProperty id -Unique)
if ($entries.Count -ne $ids.Count) { throw 'NPC table contains duplicate IDs.' }

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('npcId,npcName,audio')
$audioCounts = @{ '0' = 0; '1' = 0; '2' = 0 }
foreach ($entry in $entries) {
    $id = [string]$entry.id
    $name = ([string]$entry.npcName.'zh-Hans').Replace('"', '""')
    $wav = Join-Path $voiceDir ($id + '.wav')
    $audio = if (-not (Test-Path -LiteralPath $wav -PathType Leaf)) { '0' }
        elseif ($previousAudio[$id] -eq '2') { '2' }
        else { '1' }
    $audioCounts[$audio]++
    $lines.Add(('{0},"{1}",{2}' -f $id, $name, $audio))
}
[IO.File]::WriteAllLines($destination, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($entries.Count) NPC rows to $destination (audio 0=$($audioCounts['0']), 1=$($audioCounts['1']), 2=$($audioCounts['2']))"
