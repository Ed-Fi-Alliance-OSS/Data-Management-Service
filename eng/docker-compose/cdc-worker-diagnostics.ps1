# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

. (Join-Path $PSScriptRoot '../ci/cdc-runbook-diagnostics.ps1')

function Invoke-CdcWorkerDiagnosticCommand {
    <# .SYNOPSIS
    Reads private command output under the capture's shared ten-second deadline.
    Each pipe has a fixed 16 KiB buffer; reaching either limit terminates collection.
    No raw output or exception is written to any PowerShell stream or file.
    #>
    param([string[]] $Arguments, $Clock, [string] $FilePath = 'docker')
    $result = @{ Status = 'CollectionFailed'; StandardOutput = ''; StandardError = '' }
    if ($Clock.ElapsedMilliseconds -ge 10000) { $result.Status = 'TimedOut'; return $result }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    $pipes = @()
    try {
        if (-not $process.Start()) { return $result }
        $pipes = @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream) | ForEach-Object {
            $buffer = [byte[]]::new(16384)
            @{ Stream = $_; Buffer = $buffer; Count = 0; Done = $false; Read = $_.ReadAsync($buffer, 0, $buffer.Length) }
        }
        while ($true) {
            if ($Clock.ElapsedMilliseconds -ge 10000) { $result.Status = 'TimedOut'; break }
            foreach ($pipe in $pipes) {
                if ($pipe.Done -or -not $pipe.Read.IsCompleted) { continue }
                $count = $pipe.Read.GetAwaiter().GetResult()
                if ($count -eq 0) { $pipe.Done = $true; continue }
                $pipe.Count += $count
                if ($pipe.Count -eq $pipe.Buffer.Length) { $result.Status = 'OutputLimit'; break }
                $pipe.Read = $pipe.Stream.ReadAsync($pipe.Buffer, $pipe.Count, $pipe.Buffer.Length - $pipe.Count)
            }
            if ($result.Status -eq 'OutputLimit') { break }
            if ($process.HasExited -and @($pipes | Where-Object { -not $_.Done }).Count -eq 0) {
                $result.Status = if ($process.ExitCode -eq 0) { 'Completed' } else { 'CommandFailed' }
                $result.CommandExitCode = $process.ExitCode
                break
            }
            Start-Sleep -Milliseconds 10
        }
    } catch {
        $result.Status = 'CollectionFailed'
    } finally {
        # Never wait indefinitely for a failed Docker client or an inherited pipe.
        try { if (-not $process.HasExited) { $process.Kill($true) } } catch {
            # Collection remains best-effort even if the process exits during termination.
            $null = $_
        }
        if ($pipes.Count -eq 2) {
            $result.StandardOutput = [Text.Encoding]::UTF8.GetString($pipes[0].Buffer, 0, $pipes[0].Count)
            $result.StandardError = [Text.Encoding]::UTF8.GetString($pipes[1].Buffer, 0, $pipes[1].Count)
        }
        $process.Dispose()
    }
    return $result
}

function Get-CdcRunbookWorkerDiagnostic {
    <# .SYNOPSIS
    Captures only the selected Compose project's worker before governed cleanup.
    Log signatures are hints, not a diagnosis; unrecognized text is never exported.
    #>
    param([ValidateSet('dms-local', 'dms-published')][string] $Project)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $record = @{ Status = 'CollectionFailed'; Stage = 'Discovery' }
    try {
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock $clock -Arguments @(
            'ps', '-a', '--no-trunc', '--filter', "label=com.docker.compose.project=$Project",
            '--filter', 'label=com.docker.compose.service=kafka-cdc-worker', '--format', '{{.ID}}'
        )
        $record.Status = $result.Status
        if ($result.ContainsKey('CommandExitCode')) { $record.CommandExitCode = $result.CommandExitCode }
        if ($result.Status -ne 'Completed') { return ConvertTo-CdcRunbookWorkerDiagnostic $record }
        $ids = @($result.StandardOutput.Trim() -split '\r?\n' | Where-Object { $_ })
        if ($ids.Count -eq 0) {
            $record.Exists = $false; $record.Status = 'NotFound'
            return ConvertTo-CdcRunbookWorkerDiagnostic $record
        }
        if ($ids.Count -ne 1 -or $ids[0] -cnotmatch '^[a-f0-9]{64}$') {
            $record.Status = 'Ambiguous'
            return ConvertTo-CdcRunbookWorkerDiagnostic $record
        }
        $record.Exists = $true
        $record.Stage = 'Inspect'
        $record.Remove('CommandExitCode')
        # Select fields inside Docker, excluding Config, environment, health output and State.Error.
        $format = '{"Running":{{.State.Running}},"Restarting":{{.State.Restarting}},"Paused":{{.State.Paused}},"OomKilled":{{.State.OOMKilled}},"ExitCode":{{.State.ExitCode}},"RestartCount":{{.RestartCount}},"Health":{{if .State.Health}}{{json .State.Health.Status}}{{else}}"none"{{end}}}'
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock $clock -Arguments @('inspect', '--format', $format, $ids[0])
        $record.Status = $result.Status
        if ($result.ContainsKey('CommandExitCode')) { $record.CommandExitCode = $result.CommandExitCode }
        if ($result.Status -ne 'Completed') { return ConvertTo-CdcRunbookWorkerDiagnostic $record }
        $state = $result.StandardOutput | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        $safeState = ConvertTo-CdcRunbookWorkerDiagnostic $state
        foreach ($field in @('Running', 'Restarting', 'Paused', 'OomKilled', 'ExitCode', 'RestartCount', 'Health')) {
            if (-not $safeState.Contains($field)) { throw 'Invalid worker state' }
            $record[$field] = $safeState[$field]
        }
        $record.Stage = 'Logs'
        $record.Remove('CommandExitCode')
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock $clock -Arguments @('logs', '--tail', '100', $ids[0])
        $record.Status = $result.Status
        if ($result.ContainsKey('CommandExitCode')) { $record.CommandExitCode = $result.CommandExitCode }
        if ($result.Status -in @('Completed', 'OutputLimit')) {
            $text = $result.StandardOutput + "`n" + $result.StandardError
            $record.JavaOutOfMemory = $text -match 'java\.lang\.OutOfMemoryError|Java heap space|GC overhead limit exceeded'
            $record.BrokerConnectionFailure = $text -match 'Connection to node .* could not be established|Bootstrap broker .* disconnected'
            $record.UnknownLogSignature = -not ($record.JavaOutOfMemory -or $record.BrokerConnectionFailure)
        }
    } catch { $record.Status = 'CollectionFailed' }
    return ConvertTo-CdcRunbookWorkerDiagnostic $record
}
