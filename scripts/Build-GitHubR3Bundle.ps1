[CmdletBinding()]
param(
    # ★公开仓库纪律：公开版文档目录取 $env:A1_PUBLIC_DOCS，不写死本机路径。
    [string] $PublicRoot = $(if ($env:A1_PUBLIC_DOCS) { $env:A1_PUBLIC_DOCS } else { $null })
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = Join-Path $project 'dist'
# ★7z 解析顺序：$env:A1_7Z → PATH → 常规安装目录。不写死某台机器的路径。
$sevenZip = $env:A1_7Z
if (-not $sevenZip) {
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { $sevenZip = $cmd.Source }
    elseif (Test-Path -LiteralPath "$env:ProgramFiles\7-Zip\7z.exe") { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
    elseif (Test-Path -LiteralPath "${env:ProgramFiles(x86)}\7-Zip\7z.exe") { $sevenZip = "${env:ProgramFiles(x86)}\7-Zip\7z.exe" }
    else { throw '找不到 7z.exe：请安装 7-Zip，或设置 A1_7Z 指向 7z.exe。' }
}
$baseRoot = Join-Path $dist 'A1IndexTTSMod-v0.5.8-r3-base'
$stage = Join-Path $dist 'A1IndexTTSMod-v0.5.8-r3-github-staging'
$archive = Join-Path $dist 'A1IndexTTSMod-v0.5.8-r3-win64.zip'
$patch = Join-Path $dist 'A1IndexTTSMod-v0.5.8-portable-doorstop-patch.zip'
$public = $PublicRoot
if (-not $public) { throw '请设置环境变量 A1_PUBLIC_DOCS 指向公开版文档目录（含 README/INSTALL/THIRD_PARTY_NOTICES/RELEASE-NOTES）。' }
foreach ($path in @($sevenZip,$patch,$public)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing: $path" }
}
if (Test-Path -LiteralPath $baseRoot) { throw "Base staging directory exists: $baseRoot" }
if (Test-Path -LiteralPath $stage) { throw "Staging directory exists: $stage" }
if (Test-Path -LiteralPath $archive) { throw "Archive exists: $archive" }

& (Join-Path $PSScriptRoot 'Build-TestRelease.ps1') -OutputRoot $baseRoot -SkipArchive | Out-Null
Copy-Item -LiteralPath (Join-Path $baseRoot 'A1IndexTTSMod-v0.5.8-win64') -Destination $stage -Recurse
$patchStage = Join-Path $stage '.portable-patch'
& $sevenZip x "-o$patchStage" -y $patch | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not extract portable patch: $LASTEXITCODE" }
$game = Join-Path $stage 'A1'
foreach ($item in @('winhttp.dll','BepInEx','A1IndexTTSMod')) {
    Copy-Item -LiteralPath (Join-Path $patchStage $item) -Destination $game -Recurse -Force
}
Remove-Item -LiteralPath $patchStage -Recurse -Force
foreach ($doc in @('README.md','INSTALL.md','THIRD_PARTY_NOTICES.md','RELEASE-NOTES-v0.5.8.md')) {
    Copy-Item -LiteralPath (Join-Path $public $doc) -Destination (Join-Path $stage $doc) -Force
}
$proxy = Join-Path $game 'winhttp.dll'
if ((Get-FileHash -LiteralPath $proxy -Algorithm SHA256).Hash.ToLowerInvariant() -ne '9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b') {
    throw 'Release proxy hash mismatch.'
}
if (@(Get-ChildItem -LiteralPath (Join-Path $game 'A1IndexTTSMod\references\npcs') -Filter '*.wav' -File).Count -ne 1169) {
    throw 'NPC reference audio count changed.'
}
if (Test-Path -LiteralPath (Join-Path $game 'A1IndexTTSMod\.cache\audiocpp\models\IndexTTS2.5-GGUF\index-tts2_5-q8_0.gguf')) {
    throw 'The GitHub ZIP must not contain the Q8 model.'
}
Push-Location $stage
try {
    & $sevenZip a -tzip -mx=1 -mmt=on $archive @('A1','README.md','INSTALL.md','THIRD_PARTY_NOTICES.md','RELEASE-NOTES-v0.5.8.md','LICENSE','CHANGELOG.md','third_party','PACKAGE-MANIFEST.txt') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed: $LASTEXITCODE" }
} finally { Pop-Location }
Write-Output (Get-Item -LiteralPath $archive | Select-Object FullName,Length)
Write-Output "SHA-256: $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
