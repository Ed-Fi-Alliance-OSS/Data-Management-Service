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
        $script:scheduledWorkflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/nightly-cdc-qualification.yml') -Raw
        $script:scheduledJob = [regex]::Match($script:scheduledWorkflow, '(?ms)^  run-cdc-qualification:.*?(?=^  [a-z][a-z0-9-]+:|\z)').Value
        $script:scheduledNotification = [regex]::Match($script:scheduledWorkflow, '(?ms)^  notify-results:.*\z').Value
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
        $script:matrixScript = Join-Path $PSScriptRoot '../Get-CdcQualificationMatrix.ps1'
    }
    It 'requires only Contract without live Docker prerequisites in PR and merge-queue qualification' {
        $script:job | Should -Match 'Invoke-CdcQualification.ps1 -Lane Contract -ResultsDirectory'
        $script:job | Should -Not -Match 'matrix:|matrix\.|-PullImages|CDC_CONNECTOR_TEMPLATE_|Start packaged-history'
        $script:gate | Should -Match '(?m)^      - run-cdc-qualification$'
        $script:gate | Should -Not -Match 'nightly-cdc|run-cdc-heavy-qualification'
    }
    It 'selects all fifteen live jobs by default without Contract or duplicates' {
        $matrix = & $script:matrixScript | ConvertFrom-Json
        $pairs = @($matrix.include | ForEach-Object { $_.lane + '/' + $_.suite })
        $expected = @('Kafka/All')
        foreach ($provider in @('Postgresql', 'Mssql')) {
            foreach ($suite in @('Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History', 'MessageContract')) {
                $expected += "$provider/$suite"
            }
        }
        @($pairs | Sort-Object) | Should -Be @($expected | Sort-Object)
    }
    It 'selects <Lane>/<Suite> for a targeted manual run' -ForEach @(
        @{ Lane = 'Kafka'; Suite = 'All'; Count = 1 }
        @{ Lane = 'Postgresql'; Suite = 'All'; Count = 7 }
        @{ Lane = 'Mssql'; Suite = 'All'; Count = 7 }
        @{ Lane = 'Postgresql'; Suite = 'RecordSize'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'Admission'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'History'; Count = 1 }
        @{ Lane = 'Postgresql'; Suite = 'MessageContract'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'MessageContract'; Count = 1 }
    ) {
        $matrix = & $script:matrixScript -Lane $Lane -Suite $Suite | ConvertFrom-Json
        $matrix.include.GetType().IsArray | Should -BeTrue
        $matrix.include.Count | Should -Be $Count
        @($matrix.include | ForEach-Object { $_.lane + '/' + $_.suite } | Select-Object -Unique).Count | Should -Be $Count
        if ($Suite -eq 'All' -and $Lane -ne 'Kafka') {
            @($matrix.include.suite | Sort-Object) | Should -Be @('Admission', 'History', 'Lifecycle', 'MessageContract', 'RecordSize', 'Recovery', 'Telemetry')
        }
        @($matrix.include | Where-Object lane -ne $Lane).Count | Should -Be 0
        if ($Suite -ne 'All') { @($matrix.include | Where-Object suite -ne $Suite).Count | Should -Be 0 }
    }
    It 'rejects ambiguous <Lane> suite selection instead of silently selecting different tests' -ForEach @(
        @{ Lane = 'All' }
        @{ Lane = 'Kafka' }
    ) {
        { & $script:matrixScript -Lane $Lane -Suite Admission } | Should -Throw '*requires one provider lane*'
    }
    It 'runs nightly including Saturday and supports manual lane and suite selection' {
        $script:scheduledWorkflow | Should -Match '(?m)^  workflow_dispatch:'
        $script:scheduledWorkflow | Should -Match 'cron: "17 8 \* \* \*"'
        $script:scheduledWorkflow | Should -Not -Match '(?m)^  (pull_request|merge_group):'
        $script:scheduledWorkflow | Should -Match 'Get-CdcQualificationMatrix.ps1 -Lane \$env:SELECTED_LANE -Suite \$env:SELECTED_SUITE'
        $script:scheduledWorkflow | Should -Match "inputs.lane \|\| 'All'"
        $script:scheduledWorkflow | Should -Match "inputs.suite \|\| 'All'"
        $script:scheduledJob | Should -Match 'matrix: \$\{\{ fromJSON\(needs.select-suites.outputs.matrix\) \}\}'
        $script:scheduledJob | Should -Not -Match '(?m)^    if:'
        $weekend = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/scheduled-build.yml') -Raw
        $weekend | Should -Not -Match 'Invoke-CdcQualification|run-cdc-heavy-qualification|nightly-cdc-qualification'
    }
    It 'retains fail-closed prerequisites and sanitized artifacts in the nightly jobs' {
        $script:scheduledJob | Should -Match 'runs-on: ubuntu-latest'
        $script:scheduledJob | Should -Match 'timeout-minutes: 120'
        $script:scheduledJob | Should -Match 'fail-fast: false'
        $script:scheduledJob | Should -Not -Match 'continue-on-error:'
        foreach ($image in @('CONNECT', 'REDPANDA', 'POSTGRES', 'SQLSERVER_2025')) {
            $script:scheduledJob | Should -Match "CDC_CONNECTOR_TEMPLATE_$($image)_IMAGE:"
        }
        $script:scheduledJob | Should -Match 'Invoke-CdcQualification.ps1 -Lane.*-Suite.*-PullImages'
        $script:scheduledJob | Should -Match "Status = 'EnvironmentUnavailable'"
        $script:scheduledJob | Should -Match 'if: always\(\)'
        $script:scheduledJob | Should -Match 'path: TestResults/cdc-qualification/\*\*'
        $script:scheduledJob | Should -Match 'name: cdc-qualification-.*github.run_attempt'
        $script:scheduledJob | Should -Match 'if-no-files-found: error'
    }
    It 'provisions and cleans up both packaged-history admin servers in their nightly jobs' {
        foreach ($provider in @('Postgresql', 'Mssql')) {
            $script:scheduledJob | Should -Match "if: matrix.lane == '$provider' && matrix.suite == 'History'"
        }
        $script:scheduledJob | Should -Match 'uses: ./.github/actions/start-postgresql-test-container'
        $script:scheduledJob | Should -Match 'uses: ./.github/actions/start-mssql-test-container'
        $script:scheduledJob | Should -Match 'ConnectionStrings__DatabaseConnection='
        $script:scheduledJob | Should -Match "if: always\(\) && matrix.suite == 'History'"
        $script:scheduledJob | Should -Match 'docker rm --force --volumes \$container'
    }
    It 'reports nightly selection and qualification failures without notifying for manual runs' {
        $script:scheduledNotification | Should -Match 'needs: \[select-suites, run-cdc-qualification\]'
        $script:scheduledNotification | Should -Match "if: always\(\) && github.event_name == 'schedule'"
        foreach ($job in @('select-suites', 'run-cdc-qualification')) {
            $script:scheduledNotification | Should -Match "needs.$job.result == 'success'"
            $script:scheduledNotification | Should -Match "needs.$job.result != 'success'"
        }
    }
    It 'keeps both nightly Slack messages to a single-line summary' {
        $messages = @([regex]::Matches($script:scheduledNotification, '(?m)^\s+(\{"text":.*\})$') | ForEach-Object {
            ($_.Groups[1].Value | ConvertFrom-Json).text
        })
        $messages.Count | Should -Be 2
        $messages[0] | Should -Be ':heavy_check_mark: DMS CI nightly CDC qualification passed, all 15 live suites verified'
        $messages[1] | Should -Be ':x: DMS CI nightly CDC qualification failed (selection: ${{ needs.select-suites.result }}, qualification: ${{ needs.run-cdc-qualification.result }})'
        foreach ($message in $messages) {
            $message | Should -Not -Match '[\r\n]'
        }
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

Describe 'Message contract qualification selection and attachments' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
    }
    It 'selects serialized and broker cases for <Provider> without selecting offline cases' -ForEach @(
        @{ Provider = 'Postgresql' }
        @{ Provider = 'Mssql' }
    ) {
        $suites = Get-CdcQualificationProviderSuite -Provider $Provider
        $suites.MessageContract | Should -Be "(Category=CdcMessageContractSerialized|Category=CdcMessageContractKafka)&Category=$($Provider)Integration"
        $suites.Count | Should -Be 7
    }
    It 'publishes contract attachments and their links while excluding document bodies and private logs' {
        $raw = New-Item -ItemType Directory (Join-Path $TestDrive 'contract-raw')
        $safe = Join-Path $TestDrive 'contract-safe'
        $name = 'cdc-message-contract-MC-PG-1234567890abcdef.json'
        "<TestRun><Results><UnitTestResult outcome='Passed'><ResultFiles><ResultFile path='host/$name'/><ResultFile path='host/input.json'/></ResultFiles></UnitTestResult></Results></TestRun>" | Set-Content (Join-Path $raw 'contract.trx')
        '{"ScenarioIds":["MC-PG-BOUNDARY"],"KeyBytes":36,"ValueBytes":16300,"DocumentBody":{"name":"private-sentinel"},"Password":"private-sentinel"}' | Set-Content (Join-Path $raw $name)
        '{"document":"private-sentinel"}' | Set-Content (Join-Path $raw 'input.json')
        'private-sentinel' | Set-Content (Join-Path $raw 'private.log')
        Export-CdcQualificationEvidence $raw $safe
        [xml] $trx = Get-Content (Join-Path $safe 'contract.trx') -Raw
        @($trx.TestRun.Results.UnitTestResult.ResultFiles.ResultFile).Count | Should -Be 1
        $trx.TestRun.Results.UnitTestResult.ResultFiles.ResultFile.path | Should -Be $name
        $evidence = Get-Content (Join-Path $safe $name) -Raw | ConvertFrom-Json
        $evidence.ScenarioIds | Should -Contain 'MC-PG-BOUNDARY'
        $evidence.ValueBytes | Should -Be 16300
        (Get-ChildItem $safe -File | Get-Content -Raw) -join '' | Should -Not -Match 'private-sentinel'
        Test-Path (Join-Path $safe 'input.json') | Should -BeFalse
        Test-Path (Join-Path $safe 'private.log') | Should -BeFalse
    }
}

Describe 'CDC fixture-level evidence retention' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
    }
    It 'exports allowlisted fixture evidence even when VSTest omits the attachment link' {
        $raw = Join-Path $TestDrive 'raw'
        $fixture = Join-Path $raw 'TestResults/MessageContractProgressAcknowledgement'
        $destination = Join-Path $TestDrive 'published'
        New-Item -ItemType Directory -Force $fixture | Out-Null
        @{ ScenarioIds = @('MC-PROGRESS-ACK-PG-GATING'); Payload = 'private-document' } |
            ConvertTo-Json | Set-Content (Join-Path $fixture 'cdc-message-contract-ack.json')
        '{"private":"not-exported"}' | Set-Content (Join-Path $fixture 'runtime-settings.json')
        Export-CdcQualificationEvidence -RawDirectory $raw -Destination $destination
        $result = Get-Content (Join-Path $destination 'cdc-message-contract-ack.json') -Raw | ConvertFrom-Json
        $result.ScenarioIds | Should -Be @('MC-PROGRESS-ACK-PG-GATING')
        $result.Payload | Should -Be '[redacted]'
        Test-Path (Join-Path $destination 'runtime-settings.json') | Should -BeFalse
    }
}
