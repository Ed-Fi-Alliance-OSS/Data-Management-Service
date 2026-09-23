# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'CDC qualification image pulls' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
        function docker { }
    }
    BeforeEach {
        $caseRoot = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        $script:raw = New-Item -ItemType Directory (Join-Path $caseRoot 'private')
        $script:destination = New-Item -ItemType Directory (Join-Path $caseRoot 'published')
        $script:image = 'registry.example.test/team/image@sha256:' + ('a' * 64)
        $script:exitCodes = [System.Collections.Generic.Queue[int]]::new()
        $script:commands = [System.Collections.Generic.List[string]]::new()
        $script:events = [System.Collections.Generic.List[string]]::new()
        $script:failure = 'Error response from daemon: unexpected EOF https://user:private-token@registry.example.test/path'
        $script:savedExitCode = $global:LASTEXITCODE
        Mock -ModuleName cdc-qualification docker {
            $script:commands.Add(($args -join ' '))
            $script:events.Add('pull')
            $global:LASTEXITCODE = $script:exitCodes.Dequeue()
            if ($global:LASTEXITCODE -ne 0) { Write-Output $script:failure }
            else { Write-Output 'Status: Image is up to date' }
        }
        Mock -ModuleName cdc-qualification Start-Sleep {
            param($Seconds)
            $script:events.Add("sleep:$Seconds")
        }
    }
    AfterEach {
        $global:LASTEXITCODE = $script:savedExitCode
    }
    It 'pulls once on immediate success and records the exact pinned image without sleeping' {
        $script:exitCodes.Enqueue(0)
        Invoke-CdcQualificationImagePull $script:image $script:raw $script:destination
        $script:commands | Should -Be @("pull $script:image")
        $script:events | Should -Be @('pull')
        $records = @(Get-Content (Join-Path $script:destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records.Count | Should -Be 1
        $records[0].Image | Should -Be $script:image
        $records[0].Attempt | Should -Be 1
        $records[0].ExitCode | Should -Be 0
        $records[0].Outcome | Should -Be 'Succeeded'
        $records[0].FailureCategory | Should -Be 'None'
        $records[0].RetryDelaySeconds | Should -Be 0
        [DateTimeOffset]::Parse($records[0].StartedAtUtc) | Should -BeLessOrEqual ([DateTimeOffset]::UtcNow)
        $records[0].DurationMs | Should -BeGreaterOrEqual 0
    }
    It 'recovers on the third attempt with bounded backoff and retains earlier failures' {
        foreach ($code in @(1, 7, 0)) { $script:exitCodes.Enqueue($code) }
        $output = Invoke-CdcQualificationImagePull $script:image $script:raw $script:destination
        $script:commands | Should -Be @("pull $script:image", "pull $script:image", "pull $script:image")
        $script:events | Should -Be @('pull', 'sleep:5', 'pull', 'sleep:15', 'pull')
        $records = @(Get-Content (Join-Path $script:destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records.Attempt | Should -Be @(1, 2, 3)
        $records.MaxAttempts | Should -Be @(3, 3, 3)
        $records.ExitCode | Should -Be @(1, 7, 0)
        $records.Outcome | Should -Be @('Failed', 'Failed', 'Succeeded')
        $records.FailureCategory | Should -Be @('Connection', 'Connection', 'None')
        $records.RetryDelaySeconds | Should -Be @(5, 15, 0)
        @($records.PrivateLog | Select-Object -Unique).Count | Should -Be 3
        (Get-Content (Join-Path $script:raw $records[0].PrivateLog) -Raw) | Should -Match 'private-token'
        ($output -join '') | Should -Not -Match 'private-token|https://'
        (Get-Content (Join-Path $script:destination 'image-pulls.jsonl') -Raw) | Should -Not -Match 'private-token|https://'
        @(Get-ChildItem $script:destination).Count | Should -Be 1
    }
    It 'fails closed after three failed attempts with retained diagnostics and no final sleep' {
        foreach ($code in @(1, 1, 23)) { $script:exitCodes.Enqueue($code) }
        $script:failure = 'toomanyrequests: rate limit exceeded; private-token'
        { Invoke-CdcQualificationImagePull $script:image $script:raw $script:destination } |
            Should -Throw 'EnvironmentUnavailable:*after 3 attempts (exit=23, category=RateLimited)*image-pulls.jsonl*'
        $script:events | Should -Be @('pull', 'sleep:5', 'pull', 'sleep:15', 'pull')
        $records = @(Get-Content (Join-Path $script:destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records.ExitCode | Should -Be @(1, 1, 23)
        $records.Outcome | Should -Be @('Failed', 'Failed', 'Failed')
        $records.RetryDelaySeconds | Should -Be @(5, 15, 0)
        $records.FailureCategory | Should -Be @('RateLimited', 'RateLimited', 'RateLimited')
    }
    It 'appends attempts across images without overwriting evidence or private logs' {
        foreach ($code in @(0, 1, 0)) { $script:exitCodes.Enqueue($code) }
        Invoke-CdcQualificationImagePull $script:image $script:raw $script:destination
        Invoke-CdcQualificationImagePull 'postgres:16' $script:raw $script:destination
        $records = @(Get-Content (Join-Path $script:destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records.Image | Should -Be @($script:image, 'postgres:16', 'postgres:16')
        $records.Attempt | Should -Be @(1, 1, 2)
        @($records.PrivateLog | Select-Object -Unique).Count | Should -Be 3
        @(Get-ChildItem $script:raw).Count | Should -Be 3
    }
    It 'classifies <Category> without publishing raw Docker output' -ForEach @(
        @{ ErrorText = 'unauthorized: authentication required'; Category = 'Authorization' }
        @{ ErrorText = 'manifest unknown'; Category = 'ImageNotFound' }
        @{ ErrorText = 'no space left on device'; Category = 'DiskFull' }
        @{ ErrorText = 'dial tcp: lookup registry: no such host'; Category = 'Dns' }
        @{ ErrorText = 'x509: certificate signed by unknown authority'; Category = 'Tls' }
        @{ ErrorText = 'net/http: request canceled (Client.Timeout exceeded while awaiting headers)'; Category = 'Timeout' }
        @{ ErrorText = 'received unexpected HTTP status: 503 Service Unavailable'; Category = 'RegistryUnavailable' }
        @{ ErrorText = 'unrecognized private-token'; Category = 'Unknown' }
        @{ ErrorText = ''; Category = 'Unknown' }
    ) {
        foreach ($code in @(1, 0)) { $script:exitCodes.Enqueue($code) }
        $script:failure = $ErrorText
        Invoke-CdcQualificationImagePull $script:image $script:raw $script:destination
        $records = @(Get-Content (Join-Path $script:destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records[0].FailureCategory | Should -Be $Category
        (Get-Content (Join-Path $script:destination 'image-pulls.jsonl') -Raw) | Should -Not -Match 'private-token'
    }
    It 'redacts unsafe image identities from both evidence and the failure message' -ForEach @(
        @{ UnsafeImage = "registry/image:tag`n::error::injected" }
        @{ UnsafeImage = 'user:private-token@registry.example.test/image' }
        @{ UnsafeImage = 'https://registry/image?token=private-token' }
    ) {
        foreach ($code in @(1, 1, 1)) { $script:exitCodes.Enqueue($code) }
        { Invoke-CdcQualificationImagePull $UnsafeImage $script:raw $script:destination } |
            Should -Throw 'EnvironmentUnavailable:*for `[redacted`] after 3 attempts*'
        $records = @(Get-Content (Join-Path $script:destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records.Image | Should -Be @('[redacted]', '[redacted]', '[redacted]')
        (Get-Content (Join-Path $script:destination 'image-pulls.jsonl') -Raw) | Should -Not -Match 'private-token|::error::'
    }
}

Describe 'CDC qualification image-pull runner boundary' {
    It 'retains pull evidence and reports unavailable prerequisites without starting tests after retries are exhausted' {
        # Run the real entry point in a child process because it deliberately exits nonzero.
        # Docker and sleep shims keep this deterministic and independent of a live registry.
        $harness = Join-Path $TestDrive 'runner-harness.ps1'
        $destination = Join-Path $TestDrive 'runner-evidence'
        $runner = Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1'
        Set-Content -LiteralPath $harness -Value @'
param([string] $Runner, [string] $Destination)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path (Split-Path $Runner) '../..'))
$qualified = Get-Content (Join-Path $repo 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json') -Raw | ConvertFrom-Json
$env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE = $qualified.image
function global:docker {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'pull' -and $args[-1] -eq $env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE) {
        $global:LASTEXITCODE = 17
        'Error response from daemon: toomanyrequests: private-token'
    }
    else { 'fixture-docker-success' }
}
function global:Start-Sleep { }
function global:dotnet { throw 'The test suite must not start after a failed pull.' }
& $Runner -Lane Kafka -ResultsDirectory $Destination -PullImages
exit $LASTEXITCODE
'@
        $output = & pwsh -NoProfile -File $harness -Runner $runner -Destination $destination 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Not -Match 'private-token|The test suite must not start'
        $report = Get-Content (Join-Path $destination 'qualification.json') -Raw | ConvertFrom-Json -NoEnumerate
        $report.Count | Should -Be 1
        $report[0].Status | Should -Be 'EnvironmentUnavailable'
        $report[0].Total | Should -Be 0
        $report[0].Reason | Should -Match 'after 3 attempts \(exit=17, category=RateLimited\)'
        $records = @(Get-Content (Join-Path $destination 'image-pulls.jsonl') | ConvertFrom-Json)
        $records.ExitCode | Should -Be @(0, 17, 17, 17)
        $records.Attempt | Should -Be @(1, 1, 2, 3)
        @($records.PrivateLog | Select-Object -Unique).Count | Should -Be 4
        @(Get-ChildItem $destination -Name | Sort-Object) | Should -Be @('image-pulls.jsonl', 'qualification.json')
        (Get-ChildItem $destination -File | Get-Content -Raw) -join '' | Should -Not -Match 'private-token'
    }
}

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
    It 'reports recovered startup failures without hiding them or counting injected failures as flakes' {
        try {
            Write-Report
            '{"Stage":"unprovisioned-sql-startup","Signature":"ReasonTwoErrnoEleven","Container":{"Logs":{"InjectedFailure":false}}}' |
                Set-Content (Join-Path $TestDrive 'admission-evidence-sql-startup-real.json')
            '{"Stage":"unprovisioned-sql-recovery","Outcome":"Ready","Injected":false}' |
                Set-Content (Join-Path $TestDrive 'admission-evidence-sql-recovery-real.json')
            '{"Stage":"unprovisioned-sql-startup","Signature":"LsaInitializationTimeout","Container":{"Logs":{"InjectedFailure":true}}}' |
                Set-Content (Join-Path $TestDrive 'admission-evidence-sql-startup-injected.json')
            '{"Stage":"unprovisioned-sql-recovery","Outcome":"Ready","Injected":true}' |
                Set-Content (Join-Path $TestDrive 'admission-evidence-sql-recovery-injected.json')
            $result = Get-CdcQualificationReport $script:report 0
            $result.Status | Should -Be 'Passed'
            $result.SqlStartupFailures | Should -Be 1
            $result.SqlStartupRecoveries | Should -Be 1
            $result.SqlStartupInjectedFailures | Should -Be 1
            $result.SqlStartupInjectedRecoveries | Should -Be 1
            $result.SqlStartupFailureSignatures.ReasonTwoErrnoEleven | Should -Be 1
            Write-Report -Outcome Failed -Message 'Expected ready CDC evidence'
            (Get-CdcQualificationReport $script:report 1).Status | Should -Be 'Failed'
        }
        finally {
            Remove-Item (Join-Path $TestDrive 'admission-evidence-sql-*.json')
        }
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
        $weekend = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/dms-weekend-build.yml') -Raw
        $weekend | Should -Not -Match 'Invoke-CdcQualification|run-cdc-heavy-qualification|nightly-cdc-qualification'
    }
    It 'retains fail-closed prerequisites and sanitized artifacts in the nightly jobs' {
        $script:scheduledJob | Should -Match 'runs-on: ubuntu-latest'
        $script:scheduledJob | Should -Match 'timeout-minutes: 120'
        $script:scheduledJob | Should -Match 'fail-fast: false'
        $script:scheduledJob | Should -Not -Match 'continue-on-error:'
        foreach ($image in @('REDPANDA', 'POSTGRES', 'SQLSERVER_2025')) {
            $script:scheduledJob | Should -Match "CDC_CONNECTOR_TEMPLATE_$($image)_IMAGE:"
        }
        $script:scheduledJob | Should -Match 'Invoke-CdcQualification.ps1 -Lane.*-Suite.*-PullImages'
        $script:scheduledJob | Should -Match "Status = 'EnvironmentUnavailable'"
        $script:scheduledJob | Should -Match 'if: always\(\)'
        $script:scheduledJob | Should -Match 'path: TestResults/cdc-qualification/\*\*'
        $script:scheduledJob | Should -Match 'name: cdc-qualification-.*github.run_attempt'
        $script:scheduledJob | Should -Match 'if-no-files-found: error'
    }
    It 'configures the nightly Connect image from the checked-out qualification record' {
        $script:scheduledJob | Should -Not -Match 'vars.CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE'
        $configure = [regex]::Match($script:scheduledJob, '(?ms)^      - name: Configure qualified Connect image\r?\n        run: \|\r?\n(?<run>(?:          [^\r\n]*\r?\n)+)').Groups['run'].Value
        $configure | Should -Not -BeNullOrEmpty
        $savedGithubEnv = $env:GITHUB_ENV
        try {
            $env:GITHUB_ENV = Join-Path $TestDrive 'github-env'
            Push-Location (Join-Path $PSScriptRoot '../../..')
            $qualified = Get-Content 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json' -Raw | ConvertFrom-Json
            & ([scriptblock]::Create($configure))
            (Get-Content $env:GITHUB_ENV) | Should -Be "CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE=$($qualified.image)"
        }
        finally {
            Pop-Location
            $env:GITHUB_ENV = $savedGithubEnv
        }
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

Describe 'CDC documentation qualification boundary' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
    }
    It 'requires both live PostgreSQL setup invocations independently of controller admission (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'notrun' }, @{ Fault = 'duplicate' }
    ) {
        $required = (Get-CdcRunbookPesterReport -Tests @() -QualificationProfile PostgresqlSetup).Cases
        $required.SnippetId | Should -Be @('cdc-pg-bootstrap-local', 'cdc-pg-e2e-setup')
        $tests = @($required | ForEach-Object { [pscustomobject]@{ ExpandedName = $_.TestId; Result = 'Passed' } })
        if ($Fault -eq 'excluded') { $tests = @($tests[0]) }
        if ($Fault -eq 'skipped') { $tests[0].Result = 'Skipped' }
        if ($Fault -eq 'notrun') { $tests[0].Result = 'NotRun' }
        if ($Fault -eq 'duplicate') { $tests += $tests[0] }
        $report = Get-CdcRunbookPesterReport -Tests $tests -QualificationProfile PostgresqlSetup
        $report.Name | Should -Be 'Postgresql-runbook-setup'
        $report.Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'requires all three live SQL Server setup invocations (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'notrun' }, @{ Fault = 'duplicate' }
    ) {
        $required = (Get-CdcRunbookPesterReport -Tests @() -QualificationProfile MssqlSetup).Cases
        $required.SnippetId | Should -Be @('cdc-sqlserver-bootstrap-local', 'cdc-sqlserver-bootstrap-published', 'cdc-sqlserver-e2e-setup')
        $tests = @($required | ForEach-Object { [pscustomobject]@{ ExpandedName = $_.TestId; Result = 'Passed' } })
        if ($Fault -eq 'excluded') { $tests = @($tests[0], $tests[2]) }
        if ($Fault -eq 'skipped') { $tests[1].Result = 'Skipped' }
        if ($Fault -eq 'notrun') { $tests[1].Result = 'NotRun' }
        if ($Fault -eq 'duplicate') { $tests += $tests[1] }
        $report = Get-CdcRunbookPesterReport -Tests $tests -QualificationProfile MssqlSetup
        $report.Name | Should -Be 'Mssql-runbook-setup'
        $report.Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'selects the shared live setup fixture for each owned provider Admission path' {
        $runner = Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw
        $runner | Should -Match "if \(\`$phase -eq 'Admission' -or"
        $runner | Should -Match 'New-PesterContainer.*RunbookSetup.Live.Tests.ps1.*Provider = \$selected'
        $runner | Should -Match 'Mssql Admission/Lifecycle requires CDC_RUNBOOK_OWNED_STACK=1'
        $runner | Should -Match 'Get-CdcRunbookPesterReport.*-QualificationProfile "\$selected\$procedure"'
    }
    It 'requires the live <Provider> lifecycle case (<Fault>)' -ForEach @(
        foreach ($provider in @('Postgresql', 'Mssql')) {
            foreach ($fault in @('none', 'excluded', 'skipped', 'notrun', 'duplicate')) {
                @{ Provider = $provider; Fault = $fault }
            }
        }
    ) {
        $tests = @([pscustomobject]@{ ExpandedName = 'CDC-DOC cdc-managed-start'; Result = 'Passed' })
        if ($Fault -eq 'excluded') { $tests = @() }
        if ($Fault -eq 'skipped') { $tests[0].Result = 'Skipped' }
        if ($Fault -eq 'notrun') { $tests[0].Result = 'NotRun' }
        if ($Fault -eq 'duplicate') { $tests += $tests[0] }
        $report = Get-CdcRunbookPesterReport -Tests $tests -QualificationProfile "${Provider}Lifecycle"
        $report.Name | Should -Be "$Provider-runbook-lifecycle"
        $report.Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'selects both owned provider Lifecycle commands and their required behavior report' {
        $runner = Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw
        $runner | Should -Match "if \(\`$phase -eq 'Lifecycle'\)"
        $runner | Should -Match 'Get-CdcRunbookLifecycleReport -Path'
        $runner | Should -Match 'New-PesterContainer.*Procedure = \$procedure'
        $runner | Should -Match 'PostgreSQL Admission/Lifecycle requires CDC_RUNBOOK_OWNED_STACK=1'
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/nightly-cdc-qualification.yml') -Raw
        $workflow | Should -Match "matrix.suite == 'Admission' \|\| matrix.suite == 'Lifecycle'"
    }
    It 'requires all lifecycle persistence and rejection cases (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'partial' }, @{ Fault = 'duplicate' }
    ) {
        $path = Join-Path $TestDrive 'lifecycle.trx'
        $required = (Get-CdcRunbookLifecycleReport (Join-Path $TestDrive 'missing.trx')).Cases
        $nodes = @($required | ForEach-Object {
            $case = $_
            for ($i = 0; $i -lt $case.Required; $i++) {
                "<UnitTestResult testName='$($case.TestId)($i)' outcome='Passed'/>"
            }
        })
        if ($Fault -eq 'excluded') { $nodes = @() }
        if ($Fault -eq 'skipped') { $nodes[0] = $nodes[0].Replace('Passed', 'NotExecuted') }
        if ($Fault -eq 'partial') { $nodes = $nodes[1..($nodes.Count - 1)] }
        if ($Fault -eq 'duplicate') { $nodes += $nodes[0] }
        "<TestRun><Results>$($nodes -join '')</Results></TestRun>" | Set-Content $path
        (Get-CdcRunbookLifecycleReport $path).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'requires all native recovery and live snippet cases (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'partial' }, @{ Fault = 'duplicate' }
    ) {
        $path = Join-Path $TestDrive 'recovery.trx'
        $required = (Get-CdcRunbookRecoveryReport (Join-Path $TestDrive 'missing.trx')).Cases
        $nodes = @($required | ForEach-Object {
            $case = $_
            for ($i = 0; $i -lt $case.Required; $i++) {
                "<UnitTestResult testName='$($case.TestId)($i)' outcome='Passed'/>"
            }
        })
        if ($Fault -eq 'excluded') { $nodes = @() }
        if ($Fault -eq 'skipped') { $nodes[0] = $nodes[0].Replace('Passed', 'NotExecuted') }
        if ($Fault -eq 'partial') { $nodes = $nodes[1..($nodes.Count - 1)] }
        if ($Fault -eq 'duplicate') { $nodes += $nodes[0] }
        "<TestRun><Results>$($nodes -join '')</Results></TestRun>" | Set-Content $path
        (Get-CdcRunbookRecoveryReport $path).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'requires all record-size rollout and live snippet cases (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'partial' }, @{ Fault = 'duplicate' }
    ) {
        $path = Join-Path $TestDrive 'record-size.trx'
        $required = (Get-CdcRunbookRecordSizeReport (Join-Path $TestDrive 'missing.trx')).Cases
        $nodes = @($required | ForEach-Object {
            $case = $_
            for ($i = 0; $i -lt $case.Required; $i++) {
                "<UnitTestResult testName='$($case.TestId)($i)' outcome='Passed'/>"
            }
        })
        if ($Fault -eq 'excluded') { $nodes = @() }
        if ($Fault -eq 'skipped') { $nodes[0] = $nodes[0].Replace('Passed', 'NotExecuted') }
        if ($Fault -eq 'partial') { $nodes = $nodes[1..($nodes.Count - 1)] }
        if ($Fault -eq 'duplicate') { $nodes += $nodes[0] }
        "<TestRun><Results>$($nodes -join '')</Results></TestRun>" | Set-Content $path
        (Get-CdcRunbookRecordSizeReport $path).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'requires every named wrapper case to execute once and pass (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'notrun' }, @{ Fault = 'duplicate' }
    ) {
        $required = (Get-CdcRunbookPesterReport -Tests @()).Cases
        $tests = @($required | ForEach-Object { [pscustomobject]@{ ExpandedName = $_.TestId; Result = 'Passed' } })
        if ($Fault -eq 'excluded') { $tests = $tests[1..($tests.Count - 1)] }
        if ($Fault -eq 'skipped') { $tests[0].Result = 'Skipped' }
        if ($Fault -eq 'notrun') { $tests[0].Result = 'NotRun' }
        if ($Fault -eq 'duplicate') { $tests += $tests[0] }
        (Get-CdcRunbookPesterReport -Tests $tests).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'requires the command settings output packaged and link cases (<Fault>)' -ForEach @(
        @{ Fault = 'none' }, @{ Fault = 'excluded' }, @{ Fault = 'skipped' }, @{ Fault = 'partial' }
    ) {
        $path = Join-Path $TestDrive 'docs.trx'
        $required = (Get-CdcRunbookCliReport (Join-Path $TestDrive 'absent.trx')).Cases
        $nodes = @($required | ForEach-Object {
            $case = $_
            for ($i = 0; $i -lt $case.Required; $i++) {
                "<UnitTestResult testName='$($case.TestId)($i)' outcome='Passed'/>"
            }
        })
        if ($Fault -eq 'excluded') { $nodes = @() }
        if ($Fault -eq 'partial') { $nodes = $nodes[1..($nodes.Count - 1)] }
        if ($Fault -eq 'skipped') { $nodes[0] = $nodes[0].Replace('Passed', 'NotExecuted') }
        "<TestRun><Results>$($nodes -join '')</Results></TestRun>" | Set-Content $path
        (Get-CdcRunbookCliReport $path).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'exports only stable snippet test IDs and outcomes, including attachment links' {
        $raw = New-Item -ItemType Directory (Join-Path $TestDrive 'docs-raw')
        $safe = Join-Path $TestDrive 'docs-safe'
        '{"Cases":[{"TestId":"CDC-DOC cdc-managed-stop","SnippetId":"cdc-managed-stop","Outcome":"Passed","Settings":{"key":"unlabelled private value"},"RawOutput":"private output","Credential":"opaque"},{"TestId":"private output","SnippetId":"cdc-managed-start","Outcome":"Passed"}],"Payload":"body"}' |
            Set-Content (Join-Path $raw 'cdc-runbook-wrappers.json')
        '<TestRun><Results><UnitTestResult outcome="Passed"><ResultFiles><ResultFile path="private/cdc-runbook-wrappers.json"/></ResultFiles><Output>private output</Output></UnitTestResult></Results></TestRun>' |
            Set-Content (Join-Path $raw 'docs.trx')
        '{"key":"opaque"}' | Set-Content (Join-Path $raw 'settings.json')
        Export-CdcQualificationEvidence $raw $safe
        $cases = Get-Content (Join-Path $safe 'cdc-runbook-wrappers.json') -Raw | ConvertFrom-Json -NoEnumerate
        $cases.Count | Should -Be 1
        $cases[0].TestId | Should -Be 'CDC-DOC cdc-managed-stop'
        $cases[0].SnippetId | Should -Be 'cdc-managed-stop'
        $cases[0].Outcome | Should -Be 'Passed'
        @($cases[0].PSObject.Properties.Name | Sort-Object) | Should -Be @('Outcome', 'SnippetId', 'TestId')
        [xml] $trx = Get-Content (Join-Path $safe 'docs.trx') -Raw
        Test-Path (Join-Path $safe $trx.TestRun.Results.UnitTestResult.ResultFiles.ResultFile.path) | Should -BeTrue
        (Get-ChildItem $safe -File | Get-Content -Raw) -join '' | Should -Not -Match 'private|opaque|body|Settings|RawOutput|Credential|Payload'
        Test-Path (Join-Path $safe 'settings.json') | Should -BeFalse
    }
    It 'keeps wrapper checks in Contract and the existing PR Pester selection' {
        $runner = Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/on-dms-pullrequest.yml') -Raw
        foreach ($text in @($runner, $workflow)) {
            $text | Should -Match 'eng/docker-compose/tests/Cdc\*\.Tests.ps1'
            $text | Should -Match 'Get-CdcRunbookPesterReport'
        }
        $runner | Should -Match 'Get-CdcRunbookCliReport'
    }
    It 'preserves provider and suite filters for shared helper consumers on <Provider>' -ForEach @(
        @{ Provider = 'Postgresql' }, @{ Provider = 'Mssql' }
    ) {
        $suites = Get-CdcQualificationProviderSuite $Provider
        $suites.History | Should -Be "Category=CdcPublicationHistory&Category=$($Provider)Integration"
        $source = Get-Content (Join-Path $PSScriptRoot '../../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/CdcPublicationHistoryTests.cs') -Raw
        $source | Should -Match ('Category = "' + $Provider + 'Integration"')
        $source | Should -Match '\[Category\("CdcPublicationHistory"\)\]'
        $source | Should -Match '\[Category\("DatabaseIntegration"\)\]'
        $source | Should -Match 'CdcRunbookExamples.AssertExcerpt'
    }
}
