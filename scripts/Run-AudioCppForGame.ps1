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
$failedFile = Join-Path $state "$prefix.failed.json"
$stopFile = Join-Path $state "$prefix.stop"
$stdoutLog = Join-Path $state "$prefix.stdout.log"
$stderrLog = Join-Path $state "$prefix.stderr.log"
$ownedServerPid = $null
$serverProcess = $null
$startedOwned = $false
$priorServerPid = $null
$script:failureReported = $false
New-Item -ItemType Directory -Path $state -Force | Out-Null
Remove-Item -LiteralPath $readyFile, $failedFile, $stopFile, $stdoutLog, $stderrLog -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $pidFile) {
    try { $priorServerPid = [int](Get-Content -LiteralPath $pidFile -Raw) } catch { }
}

function Get-WindowsCrashEvidence([string] $ApplicationName, [int] $ProcessId) {
    $since = (Get-Date).AddMinutes(-3)
    $evidence = [System.Collections.Generic.List[string]]::new()

    function Get-EventDataFields($Event) {
        try {
            [xml] $eventXml = $Event.ToXml()
            return @($eventXml.SelectNodes("//*[local-name()='EventData']/*[local-name()='Data']") | ForEach-Object {
                $name = $_.GetAttribute('Name')
                if ($name) { "$name=$($_.InnerText)" } else { [string]$_.InnerText }
            })
        } catch { return @() }
    }

    function Test-EventMatchesProcess($Event, [string] $ExpectedApplication, [int] $ExpectedProcessId) {
        if ($ExpectedProcessId -le 0) { return $false }
        $processIdValues = [System.Collections.Generic.List[string]]::new()
        foreach ($match in [regex]::Matches($Event.Message, '(?i)(?:faulting\s+)?process\s+id:\s*(0x[0-9a-f]+|\d+)')) {
            $processIdValues.Add($match.Groups[1].Value)
        }
        $fields = @(Get-EventDataFields $Event)
        foreach ($field in $fields) {
            if ($field -match '(?i)^(?:Faulting)?ProcessId=(.+)$|^(?:Faulting)?Pid=(.+)$') {
                $value = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                if ($value) { $processIdValues.Add($value.Trim()) }
            }
        }
        foreach ($value in $processIdValues) {
            try {
                $reportedPid = if ($value -match '^0x([0-9a-f]+)$') { [Convert]::ToInt64($Matches[1], 16) } else { [Convert]::ToInt64($value, 10) }
                if ($reportedPid -eq $ExpectedProcessId) { return $true }
            } catch { }
        }

        # For the resource-exhaustion list, require the exact executable (PID)
        # entry instead of associating the event by name or event-header PID.
        if ($Event.Id -eq 2004) {
            $pidHex = '{0:X}' -f $ExpectedProcessId
            $entryPattern = '(?i)\b' + [regex]::Escape($ExpectedApplication) + '\s*\(\s*(?:0x' + $pidHex + '|' + $ExpectedProcessId + ')\s*\)'
            if ($Event.Message -match $entryPattern) { return $true }
        }
        return $false
    }

    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = @(1000, 1001); StartTime = $since } -ErrorAction SilentlyContinue |
            Where-Object {
                $eventFields = @(Get-EventDataFields $_)
                (($_.Message -match [regex]::Escape($ApplicationName)) -or (($eventFields -join "`n") -match [regex]::Escape($ApplicationName))) -and
                (Test-EventMatchesProcess $_ $ApplicationName $ProcessId)
            } | Select-Object -First 2
        foreach ($event in $events) {
            $fields = @(Get-EventDataFields $event | Where-Object { $_ -match '(?i)(appname|application|module|exception|processid|reportid|error)' })
            $lines = @($event.Message -split "`r?`n" | Where-Object { $_ -match '(?i)(faulting application|faulting module|exception code|report id|process id|error)' } | Select-Object -First 5)
            $details = @($fields + $lines | Select-Object -First 8)
            if ($details.Count -gt 0) { $evidence.Add("Application event $($event.Id) (matched PID ${ProcessId}): $($details -join ' | ')") }
        }
    } catch { }
    try {
        $memoryEvents = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Resource-Exhaustion-Detector'; Id = 2004; StartTime = $since } -ErrorAction SilentlyContinue |
            Where-Object { Test-EventMatchesProcess $_ $ApplicationName $ProcessId } | Select-Object -First 1
        if ($memoryEvents) {
            $lines = @($memoryEvents.Message -split "`r?`n" | Where-Object { $_ -match '(?i)(memory|process|application|resource)' } | Select-Object -First 4)
            $evidence.Add("System resource event 2004 (matched PID ${ProcessId}, listed among memory consumers): $($lines -join ' | ')")
        }
    } catch { }
    return ($evidence -join "`n").Substring(0, [Math]::Min(1800, ($evidence -join "`n").Length))
}

function Write-FailureReport([string] $Kind, [string] $Message) {
    if ($script:failureReported) { return }
    $script:failureReported = $true
    $exitCode = $null
    $exitCodeHex = $null
    if ($serverProcess) {
        try {
            $serverProcess.Refresh()
            if ($serverProcess.HasExited) {
                $exitCode = [int]$serverProcess.ExitCode
                $exitCodeHex = '0x' + [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exitCode), 0).ToString('X8')
            }
        } catch { }
    }
    if (-not $exitCodeHex -and $Message -match '(?i)exit(?:code)?\s*[=:]\s*(0x[0-9a-f]+)') {
        $exitCodeHex = $Matches[1].ToUpperInvariant()
    } elseif (-not $exitCodeHex -and $Message -match '(?i)exit(?:code)?\s*[=:]\s*(-?\d+)') {
        try {
            $exitCode = [int]$Matches[1]
            $exitCodeHex = '0x' + [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exitCode), 0).ToString('X8')
        } catch { }
    }
    $eventEvidence = ''
    if (($Kind -eq 'service_process_exit' -or $Message -match '(?i)exit(?:code)?\s*[=:]\s*(?:0x[0-9a-f]+|-?\d+)') -and $Message -notmatch '(?i)(cancel|stop requested)') {
        $eventPid = 0
        if ($null -ne $ownedServerPid) { $eventPid = [int]$ownedServerPid }
        $eventEvidence = Get-WindowsCrashEvidence $serverName $eventPid
    }
    $report = [ordered]@{
        schema = 1
        kind = $Kind
        detectedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        processId = $ownedServerPid
        exitCode = $exitCode
        exitCodeHex = $exitCodeHex
        message = $Message
        eventLogEvidence = $eventEvidence
        stdoutLog = $stdoutLog
        stderrLog = $stderrLog
    }
    try { [IO.File]::WriteAllText($failedFile, ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false)) } catch { }
}

try {
    $game = Get-Process -Id $GamePid -ErrorAction Stop
    if ($game.Path -ne $gameExe) { throw "PID $GamePid is not this game's executable." }
    $gameStarted = $game.StartTime

    & (Join-Path $PSScriptRoot 'Start-AudioCpp.ps1') -Backend $Backend -Precision $Precision -GpuBackend $GpuBackend -GpuDevice $GpuDevice -Port $Port -Prewarm $true -Visible $false -StandardOutputLogPath $stdoutLog -StandardErrorLogPath $stderrLog
    if (-not (Test-Path -LiteralPath $pidFile)) { throw 'audio.cpp did not create a managed PID file.' }
    $ownedServerPid = [int](Get-Content -LiteralPath $pidFile -Raw)
    $serverProcess = [Diagnostics.Process]::GetProcessById($ownedServerPid)
    $startedOwned = $true
    [IO.File]::WriteAllText($readyFile, [string]$ownedServerPid)

    while ($true) {
        Start-Sleep -Seconds 1
        if (Test-Path -LiteralPath $stopFile) { break }
        $game = Get-Process -Id $GamePid -ErrorAction SilentlyContinue
        if (-not $game -or $game.Path -ne $gameExe -or $game.StartTime -ne $gameStarted) { break }
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) {
            throw "audio.cpp process exited while the game was still running (PID=$ownedServerPid exitCode=$($serverProcess.ExitCode))."
        }
    }
} catch {
    if (-not $startedOwned -and (Test-Path -LiteralPath $pidFile)) {
        try {
            $candidatePid = [int](Get-Content -LiteralPath $pidFile -Raw)
            if ($candidatePid -ne $priorServerPid) {
                $ownedServerPid = $candidatePid
                $startedOwned = $true
                try { $serverProcess = [Diagnostics.Process]::GetProcessById($candidatePid) } catch { }
            }
        } catch { }
    }
    $kind = if ($startedOwned -and $serverProcess -and $serverProcess.HasExited) { 'service_process_exit' } else { 'startup_failure' }
    Write-FailureReport $kind $_.Exception.Message
} finally {
    Remove-Item -LiteralPath $readyFile -ErrorAction SilentlyContinue
    if ($startedOwned -and $ownedServerPid -ne $null) {
        $serverStillRunning = $false
        try { $serverProcess.Refresh(); $serverStillRunning = -not $serverProcess.HasExited } catch { }
        if ($serverStillRunning) {
            try { & (Join-Path $PSScriptRoot 'Stop-AudioCpp.ps1') -ExpectedPid $ownedServerPid } catch {
                if (-not $script:failureReported) { Write-FailureReport 'service_shutdown_failure' "Could not stop audio.cpp: $($_.Exception.Message)" }
            }
        }
    }
    Remove-Item -LiteralPath $readyFile, $stopFile -ErrorAction SilentlyContinue
    if ($serverProcess) { $serverProcess.Dispose() }
}
