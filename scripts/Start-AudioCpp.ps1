[CmdletBinding()]
param(
    [ValidateSet('q8_0', 'f16', 'orig')]
    [string] $Precision = 'q8_0',
    [ValidateRange(1024, 65535)]
    [int] $Port = 8892,
    [bool] $Prewarm = $true,
    [bool] $Visible = $true,
    [bool] $UseCurrentConsole = $false
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$root = Join-Path $project '.cache\audiocpp'
$runtime = Join-Path $root 'runtime'
$server = Join-Path $runtime 'audiocpp_server.exe'
$model = Join-Path $root "models\IndexTTS2.5-GGUF\index-tts2_5-$Precision.gguf"
$voice = Join-Path $project 'references\demo.wav'
$state = Join-Path $project '.state\audiocpp'
$pidFile = Join-Path $state 'server.pid'

foreach ($path in @($server, $model, $voice)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file is missing: $path" }
}
New-Item -ItemType Directory -Path $state -Force | Out-Null
if (Test-Path -LiteralPath $pidFile) {
    $oldPid = [int](Get-Content -LiteralPath $pidFile -Raw)
    $old = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
    if ($old -and $old.Path -eq $server) {
        throw "audio.cpp is already running (PID $oldPid). Run scripts\Stop-AudioCpp.ps1 before changing precision."
    }
    Remove-Item -LiteralPath $pidFile
}

$configPath = Join-Path $state 'server.json'
$config = [ordered]@{
    host = '127.0.0.1'
    port = $Port
    backend = 'cuda'
    device = 0
    threads = 4
    lazy_load = $false
    log_request_body = $false
    models = @([ordered]@{
        id = 'indextts25'
        family = 'index_tts2'
        path = [IO.Path]::GetFullPath($model)
        task = 'tts'
        mode = 'offline'
        default_voice_preset = @{ voice_ref = [IO.Path]::GetFullPath($voice) }
    })
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
$arguments = '--config "' + $configPath + '" --log'
if ($UseCurrentConsole) {
    $process = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $runtime `
        -NoNewWindow -PassThru
} elseif ($Visible) {
    $process = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $runtime `
        -WindowStyle Normal -PassThru
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace A1AudioCpp {
    public static class NativeWindow {
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
'@
        [A1AudioCpp.NativeWindow]::ShowWindowAsync($process.MainWindowHandle, 9) | Out-Null
        [A1AudioCpp.NativeWindow]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    }
} else {
    $process = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $runtime `
        -RedirectStandardOutput (Join-Path $state 'server.stdout.log') `
        -RedirectStandardError (Join-Path $state 'server.stderr.log') `
        -WindowStyle Hidden -PassThru
}
$process.Id | Set-Content -LiteralPath $pidFile

$health = "http://127.0.0.1:$Port/health"
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    if ($process.HasExited) {
        throw "audio.cpp exited during startup. Check its visible console or choose an unused local port (current: $Port)."
    }
    try {
        $response = Invoke-RestMethod -Uri $health -TimeoutSec 2
        if ($response.status -eq 'ok') { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw "audio.cpp did not become ready at $health. See $state\server.stderr.log" }

if ($Prewarm) {
    $warmupText = [string]::Concat([char]0x4F60, [char]0x597D, [char]0x3002)
    $body = @{ model = 'indextts25'; input = $warmupText; response_format = 'wav' } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    Invoke-WebRequest -Uri "http://127.0.0.1:$Port/v1/audio/speech" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body $bytes `
        -OutFile (Join-Path $state 'prewarm.wav') -TimeoutSec 180 | Out-Null
}
Write-Host "audio.cpp ready: precision=$Precision model=indextts25 port=$Port pid=$($process.Id) prewarm=$Prewarm"
