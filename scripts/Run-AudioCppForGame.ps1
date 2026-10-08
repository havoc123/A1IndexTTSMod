[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int] $GamePid,
    [Parameter(Mandatory)][ValidatePattern("^[a-f0-9]{32}$")][string] $InstanceId,
    [ValidateSet('index_tts2', 'cosyvoice3')][string] $Backend = 'cosyvoice3',
    [ValidateSet('q8_0', 'f16', 'orig')][string] $Precision = 'q8_0',
    [ValidateSet('Nvidia', 'Vulkan')][string] $GpuBackend = 'Nvidia',
    [ValidateRange(0, 15)][int] $GpuDevice = 0,
    [ValidateRange(1024, 65535)][int] $Port = 8892
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$state = Join-Path $project '.state\audiocpp'
$gameExe = [IO.Path]::GetFullPath((Join-Path $project '..\WorldApart.exe'))
$serverName = if ($GpuBackend -eq 'Vulkan') { 'audiocpp_server-vulkan.exe' } else { 'audiocpp_server.exe' }
$serverExe = [IO.Path]::GetFullPath((Join-Path $project ".cache\audiocpp\runtime\$serverName"))
$pidFile = Join-Path $state 'server.pid'
$prefix = "game-$GamePid-$InstanceId"
$readyFile = Join-Path $state "$prefix.ready"
$failedFile = Join-Path $state "$prefix.failed.txt"
$stopFile = Join-Path $state "$prefix.stop"
$ownedServerPid = $null
$startedOwned = $false
New-Item -ItemType Directory -Path $state -Force | Out-Null
Remove-Item -LiteralPath $readyFile, $failedFile, $stopFile -ErrorAction SilentlyContinue
try {
    $game = Get-Process -Id $GamePid -ErrorAction Stop
    if ($game.Path -ne $gameExe) { throw "PID $GamePid is not this game's executable." }
    $gameStarted = $game.StartTime

    & (Join-Path $PSScriptRoot 'Start-AudioCpp.ps1') -Backend $Backend -Precision $Precision -GpuBackend $GpuBackend -GpuDevice $GpuDevice -Port $Port -Prewarm $true -Visible $true
    if (-not (Test-Path -LiteralPath $pidFile)) { throw 'audio.cpp did not create a managed PID file.' }
    $ownedServerPid = [int](Get-Content -LiteralPath $pidFile -Raw)
    $startedOwned = $true
    [IO.File]::WriteAllText($readyFile, [string]$ownedServerPid)

    while ($true) {
        Start-Sleep -Seconds 1
        if (Test-Path -LiteralPath $stopFile) { break }
        $game = Get-Process -Id $GamePid -ErrorAction SilentlyContinue
        if (-not $game -or $game.Path -ne $gameExe -or $game.StartTime -ne $gameStarted) { break }
        $serverPid = [int](Get-Content -LiteralPath $pidFile -Raw)
        if (-not (Get-Process -Id $serverPid -ErrorAction SilentlyContinue)) {
            throw 'audio.cpp window was closed while the game is still running.'
        }
    }
} catch {
    [IO.File]::WriteAllText($failedFile, $_.Exception.Message)
} finally {
    Remove-Item -LiteralPath $readyFile -ErrorAction SilentlyContinue
    if ($startedOwned -and $ownedServerPid -ne $null) {
        try { & (Join-Path $PSScriptRoot 'Stop-AudioCpp.ps1') -ExpectedPid $ownedServerPid } catch {
            [IO.File]::WriteAllText($failedFile, "Could not stop audio.cpp: $($_.Exception.Message)")
        }
    }
    Remove-Item -LiteralPath $readyFile, $stopFile -ErrorAction SilentlyContinue
}
