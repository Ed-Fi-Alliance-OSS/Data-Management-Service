# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'CDC qualification result boundary' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
        function Write-Report {
            param([string] $Outcome = 'Passed', [string] $Message = '', [int] $Total = 1)
            $script:report = Join-Path $TestDrive 'report.trx'
            $passed = if ($Outcome -eq 'Passed') { 1 } else { 0 }
            @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
<UnitTestResult testName="It_preserves_continuity" outcome="$Outcome"><Output><ErrorInfo><Message>$Message</Message></ErrorInfo></Output></UnitTestResult>
</Results><ResultSummary><Counters total="$Total" executed="1" passed="$passed" /></ResultSummary></TestRun>
"@ | Set-Content $script:report
        }
    }

    It 'rejects absent reports' {
        (Get-CdcQualificationReport (Join-Path $TestDrive 'absent.trx') 0).Status | Should -Be 'ReportUnavailable'
    }
    It 'rejects an empty selection even when dotnet returns zero' {
        '<TestRun><Results/><ResultSummary><Counters total="0" executed="0" passed="0" /></ResultSummary></TestRun>' | Set-Content (Join-Path $TestDrive 'empty.trx')
        (Get-CdcQualificationReport (Join-Path $TestDrive 'empty.trx') 0).Status | Should -Be 'ReportIncomplete'
    }
    It 'accepts a complete passing run' {
        Write-Report
        (Get-CdcQualificationReport $script:report 0).Status | Should -Be 'Passed'
    }
    It 'rejects skipped cases instead of counting them as qualification' {
        Write-Report -Outcome NotExecuted
        $result = Get-CdcQualificationReport $script:report 0
        $result.Status | Should -Be 'SkippedQualification'
        $result.Skipped | Should -Be 1
        $result.Passed | Should -Be 0
    }
    It 'rejects incomplete counters and nonzero test process exits' {
        Write-Report -Total 2
        (Get-CdcQualificationReport $script:report 0).Status | Should -Be 'ReportIncomplete'
        Write-Report
        (Get-CdcQualificationReport $script:report 1).Status | Should -Be 'Failed'
        (Get-Content $script:report -Raw).Replace('passed="1"', 'passed="1" aborted="1"') | Set-Content $script:report
        (Get-CdcQualificationReport $script:report 0).Status | Should -Be 'ReportIncomplete'
        Write-Report
        (Get-Content $script:report -Raw).Replace('passed="1"', 'passed="1" failed="1"') | Set-Content $script:report
        (Get-CdcQualificationReport $script:report 0).Status | Should -Be 'ReportIncomplete'
    }
    It 'distinguishes unavailable prerequisites from failed behavior' {
        Write-Report -Outcome Failed -Message 'Controller fixture prerequisite failed. CDC_TEMPLATE_PINNED_IMAGE_DOCKER_PREREQUISITE_FAILURE private-sentinel'
        $result = Get-CdcQualificationReport $script:report 1
        $result.Status | Should -Be 'EnvironmentUnavailable'
        $result.Diagnostics[0].Codes | Should -Be @('CDC_TEMPLATE_PINNED_IMAGE_DOCKER_PREREQUISITE_FAILURE')
        $result | ConvertTo-Json -Depth 10 | Should -Not -Match 'private-sentinel'
        Write-Report -Outcome Failed -Message 'Expected lifecycle Tracking.'
        (Get-CdcQualificationReport $script:report 1).Status | Should -Be 'Failed'
        Write-Report -Outcome Failed -Message 'Connection refused during worker recovery.'
        (Get-CdcQualificationReport $script:report 1).Status | Should -Be 'Failed'
        Write-Report -Outcome Failed -Message 'Connection refused. NUnit.Framework.Internal.Commands.SetUpTearDownItem.RunSetUp'
        (Get-CdcQualificationReport $script:report 1).Status | Should -Be 'EnvironmentUnavailable'
        (Get-Content $script:report -Raw).Replace('</Results>', '<UnitTestResult testName="It_validates_recovery" outcome="Failed"><Output><ErrorInfo><Message>Expected Tracking.</Message></ErrorInfo></Output></UnitTestResult></Results>') |
            Set-Content $script:report
        $mixed = Get-CdcQualificationReport $script:report 1
        $mixed.Status | Should -Be 'Failed'
        $mixed.EnvironmentFailures | Should -Be 1
        $mixed.Failed | Should -Be 2
    }
    It 'publishes outcomes and structured diagnostics without raw errors, credentials or payloads' {
        Write-Report -Outcome Failed -Message 'EdFi_Dms1! public-document-sentinel'
        (Get-Content $script:report -Raw).Replace('</TestRun>', '<RunInfos><RunInfo><Text>public-document-sentinel</Text></RunInfo></RunInfos></TestRun>') |
            Set-Content $script:report
        '{"Password":"secret-sentinel","DocumentBody":{"name":"public-document-sentinel"},"Status":"Tracking","BarrierState":1,"Diagnostics":[{"Code":"CDC_TEST"}],"Raw":"Host=private-source;Password=secret-sentinel","Arbitrary":"private-sentinel"}' |
            Set-Content (Join-Path $TestDrive 'cdc-controller-safe.json')
        '{"secret":"private-sentinel"}' | Set-Content (Join-Path $TestDrive 'appsettings.json')
        $safe = Join-Path $TestDrive 'safe'
        Export-CdcQualificationEvidence $TestDrive $safe
        $text = (Get-ChildItem $safe -File | Get-Content -Raw) -join ''
        $text | Should -Not -Match 'sentinel|EdFi_Dms1|private-source'
        $text | Should -Match 'Tracking|CDC_TEST'
        [xml] $trx = Get-Content (Join-Path $safe 'report.trx') -Raw
        $trx.TestRun.Results.UnitTestResult.outcome | Should -Be 'Failed'
        Test-Path (Join-Path $safe 'appsettings.json') | Should -BeFalse
    }

    It 'preserves generated history attachment links without allowing arbitrary sensitive paths' {
        $raw = New-Item -ItemType Directory (Join-Path $TestDrive 'history-raw')
        $safe = Join-Path $TestDrive 'history-published'
        $name = 'cdc-history-0-1021-44df405b5d374f578a65db5fbc38995a.json'
        "<TestRun><Results><UnitTestResult outcome='Passed'><ResultFiles><ResultFile path='host/$name' /><ResultFile path='host/cdc-history-secret-sentinel.json' /></ResultFiles></UnitTestResult></Results></TestRun>" |
            Set-Content (Join-Path $raw 'history.trx')
        '{"Outcome":"Passed"}' | Set-Content (Join-Path $raw $name)
        Export-CdcQualificationEvidence $raw $safe
        [xml] $trx = Get-Content (Join-Path $safe 'history.trx') -Raw
        $paths = @($trx.TestRun.Results.UnitTestResult.ResultFiles.ResultFile | ForEach-Object path)
        $paths | Should -Contain $name
        Test-Path (Join-Path $safe $name) | Should -BeTrue
        (Get-Content (Join-Path $safe 'history.trx') -Raw) | Should -Not -Match 'secret-sentinel|host/'
    }

    It 'preserves attachment correlation and removes sensitive TestCase arguments' {
        $raw = New-Item -ItemType Directory (Join-Path $TestDrive 'raw')
        $safe = Join-Path $TestDrive 'published'
        '<TestRun><Results><UnitTestResult testName="It_rejects(secret-sentinel)" outcome="Passed" relativeResultsDirectory="private"><ResultFiles><ResultFile path="host/cdc-controller-trace.json" /><ResultFile path="host/provider.log" /></ResultFiles></UnitTestResult></Results></TestRun>' |
            Set-Content (Join-Path $raw 'attachment.trx')
        '[{"Boundary":"Cleanup","Edge":"After"}]' | Set-Content (Join-Path $raw 'cdc-controller-trace.json')
        Export-CdcQualificationEvidence $raw $safe
        [xml] $trx = Get-Content (Join-Path $safe 'attachment.trx') -Raw
        $case = $trx.TestRun.Results.UnitTestResult
        $case.testName | Should -Be '[redacted]'
        $case.relativeResultsDirectory | Should -Be '.'
        @($case.ResultFiles.ResultFile).Count | Should -Be 1
        Test-Path (Join-Path $safe $case.ResultFiles.ResultFile.path) | Should -BeTrue
        $trace = Get-Content (Join-Path $safe $case.ResultFiles.ResultFile.path) -Raw | ConvertFrom-Json -NoEnumerate
        $trace.GetType().IsArray | Should -BeTrue
        $trace.Count | Should -Be 1
        '[]' | Set-Content (Join-Path $raw 'cdc-controller-trace.json')
        Export-CdcQualificationEvidence $raw $safe
        (Get-Content (Join-Path $safe $case.ResultFiles.ResultFile.path) -Raw).Trim() | Should -Be '[]'
    }
}

Describe 'CDC qualification CI scheduling' {
    BeforeAll {
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/on-dms-pullrequest.yml') -Raw
        $script:job = [regex]::Match($workflow, '(?ms)^  run-cdc-qualification:.*?(?=^  [a-z][a-z0-9-]+:|\z)').Value
        $script:gate = [regex]::Match($workflow, '(?ms)^  dms-ci-gate:.*\z').Value
    }
    It 'requires every provider suite and does not cancel peers on failure' {
        $pairs = @([regex]::Matches($script:job, 'lane: (\w+)\s+suite: (\w+)') | ForEach-Object { $_.Groups[1].Value + '/' + $_.Groups[2].Value })
        $expected = @('Contract/All', 'Kafka/All')
        foreach ($provider in @('Postgresql', 'Mssql')) {
            foreach ($suite in @('Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History')) {
                $expected += "$provider/$suite"
            }
        }
        @($pairs | Sort-Object) | Should -Be @($expected | Sort-Object)
        $script:job | Should -Match 'fail-fast: false'
        $script:gate | Should -Match '(?m)^      - run-cdc-qualification$'
    }
    It 'uses the same local runner for relevant ready PRs and merge groups' {
        $script:job | Should -Match "github.event_name != 'pull_request'"
        $script:job | Should -Match "outputs.cdc_relevant == 'true'"
        $script:job | Should -Match 'Invoke-CdcQualification.ps1 -Lane'
        $script:job | Should -Match 'if: always\(\)'
        $script:job | Should -Match 'path: TestResults/cdc-qualification/\*\*'
        $script:job | Should -Match 'name: cdc-qualification-.*github.run_attempt'
    }
}
