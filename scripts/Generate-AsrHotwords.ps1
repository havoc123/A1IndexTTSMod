[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csv = Join-Path $project 'references\npcs\npc_id_name.csv'
$rows = @(Import-Csv -LiteralPath $csv -Encoding UTF8)
# Prefer the game's NPCs with primary voice references, then additional named NPCs.
$selected = @($rows | Where-Object { $_.audio -eq '1' -or $_.npcId -in @('111','121') })
$selected += @($rows | Where-Object { $_.audio -eq '2' -and $_.npcId -notin $selected.npcId } | Select-Object -First (96 - $selected.Count))
$words = [Collections.Generic.List[string]]::new()
$sources = [Collections.Generic.List[object]]::new()
foreach ($row in $selected) {
    $word = $row.npcName.Trim()
    if ($word -notmatch '^[\u4e00-\u9fff]{2,8}$' -or $words.Contains($word)) { continue }
    $words.Add($word)
    $sources.Add([pscustomobject]@{word=$word; kind=if($row.npcId -in @('111','121')){'sect'}else{'npc'}; source='references/npcs/npc_id_name.csv'; npcId=$row.npcId})
}
$destination = Join-Path $project 'config\asr-hotwords.zh-CN.txt'
[IO.File]::WriteAllLines($destination, $words, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{schema=1; generator='scripts/Generate-AsrHotwords.ps1'; sourceSha256=(Get-FileHash $csv).Hash.ToLowerInvariant(); count=$words.Count; entries=$sources} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $project 'config\asr-hotwords.sources.json') -Encoding UTF8
Write-Host "Generated $($words.Count) game-sourced hotwords. Model token coverage is validated at engine initialization."
