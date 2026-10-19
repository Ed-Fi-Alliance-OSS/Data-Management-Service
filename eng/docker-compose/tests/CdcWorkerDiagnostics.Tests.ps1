# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll { . (Join-Path $PSScriptRoot '../cdc-worker-diagnostics.ps1') }

Describe 'Bounded worker diagnostic command' {
    It 'captures both pipes and a nonzero exit without printing private output' {
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock ([Diagnostics.Stopwatch]::StartNew()) -FilePath pwsh -Arguments @(
            '-NoProfile', '-Command', '[Console]::Out.Write("private-out"); [Console]::Error.Write("private-error"); exit 7'
        )
        $result.Status | Should -Be 'CommandFailed'
        $result.CommandExitCode | Should -Be 7
        $result.StandardOutput | Should -BeExactly 'private-out'
        $result.StandardError | Should -BeExactly 'private-error'
    }

    It 'terminates excessive <Pipe> output within the fixed byte limit' -ForEach @(@{ Pipe = 'Out' }, @{ Pipe = 'Error' }) {
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock ([Diagnostics.Stopwatch]::StartNew()) -FilePath pwsh -Arguments @(
            '-NoProfile', '-Command', "while (`$true) { [Console]::$Pipe.Write(('x' * 65536)) }"
        )
        $result.Status | Should -Be 'OutputLimit'
        ($result.StandardOutput.Length + $result.StandardError.Length) | Should -BeLessOrEqual 32768
        ($result.StandardOutput.Length + $result.StandardError.Length) | Should -Be 16384
    }

    It 'does not start a command after the shared deadline expires' {
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock ([pscustomobject]@{ ElapsedMilliseconds = 10000 }) -FilePath missing-command -Arguments @()
        $result.Status | Should -Be 'TimedOut'
        $result.StandardOutput | Should -BeExactly ''
    }

    It 'terminates a running command when the shared deadline expires without a timing race' {
        # The first read permits launch; the next read deterministically expires the budget.
        $clock = [pscustomobject]@{ Reads = 0 }
        $clock | Add-Member ScriptProperty ElapsedMilliseconds { $this.Reads++; if ($this.Reads -eq 1) { 0 } else { 10000 } }
        $result = Invoke-CdcWorkerDiagnosticCommand -Clock $clock -FilePath pwsh -Arguments @('-NoProfile', '-Command', '[Console]::ReadLine(); while ($true) { Start-Sleep 1 }')
        $result.Status | Should -Be 'TimedOut'
    }
}

Describe 'Worker failure snapshot' {
    BeforeEach {
        $script:clockInstances = [Collections.Generic.List[object]]::new()
        Mock Invoke-CdcWorkerDiagnosticCommand {
            $script:clockInstances.Add($Clock)
            switch ($Arguments[0]) {
                ps { return @{ Status = 'Completed'; CommandExitCode = 0; StandardOutput = ('a' * 64); StandardError = '' } }
                inspect { return @{ Status = 'Completed'; CommandExitCode = 0; StandardOutput = '{"Running":false,"Restarting":false,"Paused":false,"OomKilled":true,"ExitCode":137,"RestartCount":2,"Health":"unhealthy","Raw":"password=private-secret"}'; StandardError = '' } }
                logs { return @{ Status = 'Completed'; CommandExitCode = 0; StandardOutput = 'java.lang.OutOfMemoryError: Java heap space password=private-secret'; StandardError = 'Connection to node 1 (private-host) could not be established' } }
            }
        }
    }

    It 'captures safe state and signatures using one deadline and the exact selected worker' {
        $record = Get-CdcRunbookWorkerDiagnostic -Project dms-published
        $record.Status | Should -Be 'Completed'
        $record.Stage | Should -Be 'Logs'
        $record.OomKilled | Should -BeTrue
        $record.ExitCode | Should -Be 137
        $record.RestartCount | Should -Be 2
        $record.JavaOutOfMemory | Should -BeTrue
        $record.BrokerConnectionFailure | Should -BeTrue
        $record.UnknownLogSignature | Should -BeFalse
        ($record | ConvertTo-Json) | Should -Not -Match 'private|password|Raw|StandardOutput'
        $script:clockInstances.Count | Should -Be 3
        [object]::ReferenceEquals($script:clockInstances[0], $script:clockInstances[2]) | Should -BeTrue
        Should -Invoke Invoke-CdcWorkerDiagnosticCommand -Times 1 -Exactly -ParameterFilter {
            $Arguments[0] -eq 'ps' -and $Arguments -contains 'label=com.docker.compose.project=dms-published' -and
            $Arguments -contains 'label=com.docker.compose.service=kafka-cdc-worker'
        }
        Should -Invoke Invoke-CdcWorkerDiagnosticCommand -Times 1 -Exactly -ParameterFilter {
            ($Arguments -join ' ') -ceq ('logs --tail 100 ' + ('a' * 64))
        }
    }

    It 'records a missing worker without inspecting any other container' {
        Mock Invoke-CdcWorkerDiagnosticCommand { return @{ Status = 'Completed'; StandardOutput = ''; StandardError = '' } }
        $record = Get-CdcRunbookWorkerDiagnostic -Project dms-local
        $record.Status | Should -Be 'NotFound'
        $record.Exists | Should -BeFalse
        Should -Invoke Invoke-CdcWorkerDiagnosticCommand -Times 1 -Exactly
    }

    It 'refuses ambiguous or invalid discovery output <Output>' -ForEach @(
        @{ Output = ((('a' * 64), ('b' * 64)) -join "`n") }, @{ Output = 'private-untrusted-id' }
    ) {
        $script:discoveryOutput = $Output
        Mock Invoke-CdcWorkerDiagnosticCommand { return @{ Status = 'Completed'; StandardOutput = $script:discoveryOutput } }
        (Get-CdcRunbookWorkerDiagnostic -Project dms-local).Status | Should -Be 'Ambiguous'
        Should -Invoke Invoke-CdcWorkerDiagnosticCommand -Times 1 -Exactly
    }

    It 'retains state when logs are unavailable and does not invent signatures' {
        Mock Invoke-CdcWorkerDiagnosticCommand -ParameterFilter { $Arguments[0] -eq 'logs' } {
            return @{ Status = 'TimedOut'; StandardOutput = ''; StandardError = '' }
        }
        $record = Get-CdcRunbookWorkerDiagnostic -Project dms-local
        $record.Status | Should -Be 'TimedOut'
        $record.OomKilled | Should -BeTrue
        $record.Contains('CommandExitCode') | Should -BeFalse
        $record.Contains('JavaOutOfMemory') | Should -BeFalse
    }

    It 'reports invalid inspection output without leaking it or reading logs' {
        Mock Invoke-CdcWorkerDiagnosticCommand -ParameterFilter { $Arguments[0] -eq 'inspect' } {
            return @{ Status = 'Completed'; StandardOutput = 'private-password=secret'; StandardError = '' }
        }
        $record = Get-CdcRunbookWorkerDiagnostic -Project dms-local
        $record.Status | Should -Be 'CollectionFailed'
        $record.Stage | Should -Be 'Inspect'
        ($record | ConvertTo-Json) | Should -Not -Match 'private|password'
        Should -Invoke Invoke-CdcWorkerDiagnosticCommand -Times 0 -Exactly -ParameterFilter { $Arguments[0] -eq 'logs' }
    }

    It 'classifies unrecognized truncated logs as unknown without retaining text' {
        Mock Invoke-CdcWorkerDiagnosticCommand -ParameterFilter { $Arguments[0] -eq 'logs' } {
            return @{ Status = 'OutputLimit'; StandardOutput = 'password=private-secret'; StandardError = '' }
        }
        $record = Get-CdcRunbookWorkerDiagnostic -Project dms-local
        $record.Status | Should -Be 'OutputLimit'
        $record.UnknownLogSignature | Should -BeTrue
        $record.JavaOutOfMemory | Should -BeFalse
        ($record | ConvertTo-Json) | Should -Not -Match 'private|password'
    }
}
