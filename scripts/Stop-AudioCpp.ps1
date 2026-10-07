[CmdletBinding()]
param([int] $ExpectedPid = 0)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$server = Join-Path $project '.cache\audiocpp\runtime\audiocpp_server.exe'
$pidFile = Join-Path $project '.state\audiocpp\server.pid'
if (-not (Test-Path -LiteralPath $pidFile)) { Write-Host 'No managed audio.cpp process is recorded.'; return }
$serverPid = [int](Get-Content -LiteralPath $pidFile -Raw)
if ($ExpectedPid -gt 0 -and $serverPid -ne $ExpectedPid) { throw "Refusing to stop PID $serverPid; expected owned PID $ExpectedPid." }
$process = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
if ($process) {
    if ($process.Path -ne $server) { throw "PID $serverPid is not this project's audio.cpp server." }
    Stop-Process -Id $serverPid
    $process.WaitForExit(10000) | Out-Null
}
Remove-Item -LiteralPath $pidFile
Write-Host "Stopped managed audio.cpp server PID $serverPid."
