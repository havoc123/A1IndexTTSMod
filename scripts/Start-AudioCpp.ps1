[CmdletBinding()]
param(
    [ValidateSet('index_tts2', 'cosyvoice3')]
    [string] $Backend = 'cosyvoice3',
    [ValidateSet('q8_0', 'f16', 'orig')]
    [string] $Precision = 'q8_0',
    [ValidateSet('Nvidia', 'Vulkan')]
    [string] $GpuBackend = 'Nvidia',
    [ValidateRange(0, 15)]
    [int] $GpuDevice = 0,
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
$serverName = if ($GpuBackend -eq 'Vulkan') { 'audiocpp_server-vulkan.exe' } else { 'audiocpp_server.exe' }
$server = Join-Path $runtime $serverName
$modelId = if ($Backend -eq 'cosyvoice3') { 'cosyvoice3' } else { 'indextts25' }
$family = if ($Backend -eq 'cosyvoice3') { 'cosyvoice3' } else { 'index_tts2' }
$model = if ($Backend -eq 'cosyvoice3') { Join-Path $root 'models\CosyVoice3-GGUF\cosyvoice3-q8_0.gguf' } else { Join-Path $root "models\IndexTTS2.5-GGUF\index-tts2_5-$Precision.gguf" }
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

$configPath = if ($Backend -eq 'cosyvoice3') { Join-Path $state 'server.cosyvoice3.json' } else { Join-Path $state 'server.json' }
$config = [ordered]@{
    host = '127.0.0.1'
    port = $Port
    backend = if ($GpuBackend -eq 'Vulkan') { 'vulkan' } else { 'cuda' }
    device = $GpuDevice
    threads = 4
    lazy_load = $false
    log_request_body = $false
    models = @([ordered]@{
        id = $modelId
        family = $family
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
    if (-not ('A1AudioCpp.NativeProcess' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace A1AudioCpp {
    public static class NativeProcess {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int processId;
            public int threadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
'@
    }

    $startup = New-Object -TypeName 'A1AudioCpp.NativeProcess+StartupInfo'
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
    $startup.dwFlags = 0x00000001
    $startup.wShowWindow = 1
    $startup.lpTitle = 'A1 audio.cpp TTS - closes with game'
    $processInfo = New-Object -TypeName 'A1AudioCpp.NativeProcess+ProcessInformation'
    $commandLine = [Text.StringBuilder]::new(('"{0}" --config "{1}" --log' -f $server, $configPath))
    $created = [A1AudioCpp.NativeProcess]::CreateProcess(
        $server, $commandLine, [IntPtr]::Zero, [IntPtr]::Zero, $false,
        [uint32]0x00000010, [IntPtr]::Zero, $runtime, [ref]$startup, [ref]$processInfo)
    if (-not $created) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw [ComponentModel.Win32Exception]::new($errorCode)
    }
    try {
        $process = Get-Process -Id $processInfo.processId -ErrorAction Stop
    } finally {
        [A1AudioCpp.NativeProcess]::CloseHandle($processInfo.hThread) | Out-Null
        [A1AudioCpp.NativeProcess]::CloseHandle($processInfo.hProcess) | Out-Null
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
    $warmup = @{ model = $modelId; input = $warmupText; response_format = 'wav' }
    if ($Backend -eq 'cosyvoice3') { $warmup.options = @{ template_name = 'instruct' } }
    $body = $warmup | ConvertTo-Json -Depth 5 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    Invoke-WebRequest -Uri "http://127.0.0.1:$Port/v1/audio/speech" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body $bytes `
        -OutFile (Join-Path $state 'prewarm.wav') -TimeoutSec 180 | Out-Null
}
Write-Host "audio.cpp ready: modelBackend=$Backend gpuBackend=$GpuBackend gpuDevice=$GpuDevice precision=$Precision model=$modelId port=$Port pid=$($process.Id) prewarm=$Prewarm"
