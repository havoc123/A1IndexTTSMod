[CmdletBinding()]
param(
    [string] $GameRoot = 'E:\Program Files (x86)\Steam\steamapps\common\A1',
    [string] $RuntimeDirectory,
    [ValidateSet('lightweight14m','accurate160m')][string] $Profile = 'lightweight14m',
    [ValidateSet('cuda','directml')][string] $Provider = 'cuda',
    [ValidateRange(0,15)][int] $Device = 0
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRoot = Join-Path $projectRoot '.state/asr-quality'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$tool = Join-Path $PSScriptRoot 'bin/Release/net6.0/AsrQualityCheck.dll'
$stdout = Join-Path $outputRoot "residency-$Provider-$Device-$Profile.jsonl"
$stderr = Join-Path $outputRoot "residency-$Provider-$Device-$Profile.log"
$env:PATH = (Join-Path $GameRoot 'A1IndexTTSMod/asr/runtime') + [IO.Path]::PathSeparator + $env:PATH
$taskArguments = @(('"{0}"' -f $tool),'--game-root',('"{0}"' -f $GameRoot),'--profile',$Profile,'--warm-only','--hold-seconds','15','--provider',$Provider,'--device',$Device)
if ($RuntimeDirectory) { $taskArguments += @('--runtime-dir',('"{0}"' -f $RuntimeDirectory)) }
$taskProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $taskArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$null = $taskProcess.Handle # Keep the Windows process handle so ExitCode remains available.
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 200
        $rows = @(Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json })
        if ($taskProcess.HasExited -and -not ($rows | Where-Object stage -eq 'ready')) { throw (Get-Content -LiteralPath $stderr -Raw) }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'ASR warm-up resource probe timed out.' }
    } while (-not ($rows | Where-Object stage -eq 'ready'))
    Start-Sleep -Seconds 2
    $taskGpu = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory | Where-Object { $_.Name -like "pid_$($taskProcess.Id)_*" })
    $taskGpuEngines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine | Where-Object { $_.Name -like "pid_$($taskProcess.Id)_*" })
    $gpuMetrics = [ordered]@{
        pid = $taskProcess.Id
        counterInstances = $taskGpu.Count
        dedicatedMiB = $(if ($taskGpu.Count) { ($taskGpu | Measure-Object DedicatedUsage -Sum).Sum / 1MB } else { $null })
        sharedMiB = $(if ($taskGpu.Count) { ($taskGpu | Measure-Object SharedUsage -Sum).Sum / 1MB } else { $null })
        idleEngineUtilizationSum = $(if ($taskGpuEngines.Count) { ($taskGpuEngines | Measure-Object UtilizationPercentage -Sum).Sum } else { $null })
        measuredUtc = [DateTime]::UtcNow.ToString('o')
    }
    $gpuMetrics | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot "residency-$Provider-$Device-$Profile-gpu.json") -Encoding utf8
    if (-not $taskProcess.WaitForExit(30000)) { throw 'Resource probe did not exit after idle hold.' }
    if ($taskProcess.ExitCode -ne 0) { throw (Get-Content -LiteralPath $stderr -Raw) }
    Get-Content -LiteralPath $stdout
    $gpuMetrics | ConvertTo-Json -Compress
} finally {
    if (-not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue }
    $taskProcess.Dispose()
}
