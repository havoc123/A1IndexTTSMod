[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int] $GamePid,
    [ValidateSet('q8_0', 'f16', 'orig')][string] $Precision = 'q8_0',
    [ValidateRange(1024, 65535)][int] $Port = 8892
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$state = Join-Path $project '.state\audiocpp'
$gameExe = [IO.Path]::GetFullPath((Join-Path $project '..\WorldApart.exe'))
$serverExe = [IO.Path]::GetFullPath((Join-Path $project '.cache\audiocpp\runtime\audiocpp_server.exe'))
$pidFile = Join-Path $state 'server.pid'
$readyFile = Join-Path $state "game-$GamePid.ready"
$failedFile = Join-Path $state "game-$GamePid.failed.txt"
New-Item -ItemType Directory -Path $state -Force | Out-Null
Remove-Item -LiteralPath $readyFile, $failedFile -ErrorAction SilentlyContinue
$host.UI.RawUI.WindowTitle = 'A1 IndexTTS audio.cpp - closes with game'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace A1AudioCpp {
    public static class ConsoleWindow {
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
'@
$window = [A1AudioCpp.ConsoleWindow]::GetConsoleWindow()
if ($window -ne [IntPtr]::Zero) {
    [A1AudioCpp.ConsoleWindow]::ShowWindowAsync($window, 9) | Out-Null
    [A1AudioCpp.ConsoleWindow]::SetForegroundWindow($window) | Out-Null
}
Write-Host 'Starting audio.cpp for A1. Closing the game will close this TTS engine.'

try {
    $game = Get-Process -Id $GamePid -ErrorAction Stop
    if ($game.Path -ne $gameExe) { throw "PID $GamePid is not this game's executable." }
    $gameStarted = $game.StartTime

    # Replace a previously manual, project-owned server so this game owns a visible window.
    if (Test-Path -LiteralPath $pidFile) {
        $oldPid = [int](Get-Content -LiteralPath $pidFile -Raw)
        $old = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
        if ($old -and $old.Path -eq $serverExe) { & (Join-Path $PSScriptRoot 'Stop-AudioCpp.ps1') }
    }

    & (Join-Path $PSScriptRoot 'Start-AudioCpp.ps1') -Precision $Precision -Port $Port -Prewarm $true -UseCurrentConsole $true
    if (-not (Test-Path -LiteralPath $pidFile)) { throw 'audio.cpp did not create a managed PID file.' }
    [IO.File]::WriteAllText($readyFile, (Get-Content -LiteralPath $pidFile -Raw).Trim())

    while ($true) {
        Start-Sleep -Seconds 1
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
    if (Test-Path -LiteralPath $pidFile) {
        try { & (Join-Path $PSScriptRoot 'Stop-AudioCpp.ps1') } catch {
            [IO.File]::WriteAllText($failedFile, "Could not stop audio.cpp: $($_.Exception.Message)")
        }
    }
}
