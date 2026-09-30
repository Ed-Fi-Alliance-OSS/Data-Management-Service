# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7.5

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
$env:CDC_CONNECTOR_TEMPLATE_REDPANDA_IMAGE = 'fixture-redpanda:latest'
$env:CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE = 'fixture-postgres:latest'
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
        @(Get-ChildItem $destination -Name | Sort-Object) | Should -Be @('image-pulls.jsonl', 'qualification.json', 'runtime-inputs.json')
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

Describe 'CDC runbook image evidence export' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
    }
    BeforeEach {
        $caseRoot = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        $script:imageRaw = New-Item -ItemType Directory (Join-Path $caseRoot 'raw')
        $script:imageSafe = Join-Path $caseRoot 'safe'
        $script:manifest = [ordered]@{
            SnippetId = 'cdc-pg-bootstrap-local'
            Provider = 'postgresql'
            ProviderImageId = 'sha256:' + ('a' * 64)
            ApplicationImage = 'sha256:' + ('b' * 64)
            ConfigurationImage = 'sha256:' + ('c' * 64)
        }
    }
    It 'preserves both cases and exact image IDs while omitting every unexpected field' {
        $second = [ordered]@{
            SnippetId = 'cdc-sqlserver-e2e-setup'
            Provider = 'sqlserver'
            ProviderImageId = 'sha256:' + ('d' * 64)
            ApplicationImage = 'sha256:' + ('e' * 64)
            ConfigurationImage = 'sha256:' + ('f' * 64)
        }
        foreach ($expected in @($script:manifest, $second)) {
            $inputValue = @{} + $expected
            $inputValue.PrivateSettings = @{ Value = 'opaque private value' }
            $inputValue.RawOutput = 'private output'
            $inputValue | ConvertTo-Json | Set-Content (Join-Path $script:imageRaw "cdc-runbook-images-$($expected.SnippetId).json")
        }

        Export-CdcQualificationEvidence $script:imageRaw $script:imageSafe

        @(Get-ChildItem $script:imageSafe -File).Count | Should -Be 2
        foreach ($expected in @($script:manifest, $second)) {
            $actual = Get-Content (Join-Path $script:imageSafe "cdc-runbook-images-$($expected.SnippetId).json") -Raw |
                ConvertFrom-Json -AsHashtable
            @($actual.Keys | Sort-Object) | Should -Be @($expected.Keys | Sort-Object)
            foreach ($field in $expected.Keys) { $actual[$field] | Should -BeExactly $expected[$field] }
        }
    }
    It 'skips a manifest with invalid <Field> identity <Value>' -ForEach @(
        @{ Field = 'SnippetId'; Value = '../private' }
        @{ Field = 'SnippetId'; Value = 'cdc-' + ('a' * 125) }
        @{ Field = 'SnippetId'; Value = @('cdc-pg-bootstrap-local') }
        @{ Field = 'SnippetId'; Value = "cdc-pg-bootstrap-local`n" }
        @{ Field = 'Provider'; Value = 'mssql' }
        @{ Field = 'Provider'; Value = 'Postgresql' }
        @{ Field = 'Provider'; Value = @('postgresql') }
        @{ Field = 'ProviderImageId'; Value = 'sha256:' + ('A' * 64) }
        @{ Field = 'ProviderImageId'; Value = 'sha256:' + ('a' * 63) }
        @{ Field = 'ApplicationImage'; Value = 'image:latest' }
        @{ Field = 'ApplicationImage'; Value = @('sha256:' + ('a' * 64)) }
        @{ Field = 'ConfigurationImage'; Value = $null }
        @{ Field = 'ConfigurationImage'; Value = 'sha256:' + ('a' * 64) + "`n" }
    ) {
        $script:manifest[$Field] = $Value
        $script:manifest | ConvertTo-Json | Set-Content (Join-Path $script:imageRaw 'cdc-runbook-images-invalid.json')
        Export-CdcQualificationEvidence $script:imageRaw $script:imageSafe
        @(Get-ChildItem $script:imageSafe -File).Count | Should -Be 0
    }
    It 'skips a manifest missing <Field>' -ForEach @(
        @{ Field = 'SnippetId' }, @{ Field = 'Provider' }, @{ Field = 'ProviderImageId' }
        @{ Field = 'ApplicationImage' }, @{ Field = 'ConfigurationImage' }
    ) {
        $script:manifest.Remove($Field)
        $script:manifest | ConvertTo-Json | Set-Content (Join-Path $script:imageRaw 'cdc-runbook-images-invalid.json')
        Export-CdcQualificationEvidence $script:imageRaw $script:imageSafe
        @(Get-ChildItem $script:imageSafe -File).Count | Should -Be 0
    }
    It 'skips malformed JSON and non-object manifests' -ForEach @(
        @{ Json = '{broken' }, @{ Json = 'null' }, @{ Json = '[]' }, @{ Json = '"text"' }
    ) {
        $Json | Set-Content (Join-Path $script:imageRaw 'cdc-runbook-images-invalid.json')
        Export-CdcQualificationEvidence $script:imageRaw $script:imageSafe
        @(Get-ChildItem $script:imageSafe -File).Count | Should -Be 0
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
    It 'selects all seventeen live jobs by default without Contract or duplicates' {
        $matrix = & $script:matrixScript | ConvertFrom-Json
        $pairs = @($matrix.include | ForEach-Object { $_.lane + '/' + $_.suite })
        $expected = @('Kafka/All')
        foreach ($provider in @('Postgresql', 'Mssql')) {
            foreach ($suite in @('Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History', 'MessageContract', 'ApiE2E')) {
                $expected += "$provider/$suite"
            }
        }
        @($pairs | Sort-Object) | Should -Be @($expected | Sort-Object)
    }
    It 'selects <Lane>/<Suite> for a targeted manual run' -ForEach @(
        @{ Lane = 'Kafka'; Suite = 'All'; Count = 1 }
        @{ Lane = 'Postgresql'; Suite = 'All'; Count = 8 }
        @{ Lane = 'Mssql'; Suite = 'All'; Count = 8 }
        @{ Lane = 'Postgresql'; Suite = 'RecordSize'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'Admission'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'History'; Count = 1 }
        @{ Lane = 'Postgresql'; Suite = 'MessageContract'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'MessageContract'; Count = 1 }
        @{ Lane = 'Postgresql'; Suite = 'ApiE2E'; Count = 1 }
        @{ Lane = 'Mssql'; Suite = 'ApiE2E'; Count = 1 }
    ) {
        $matrix = & $script:matrixScript -Lane $Lane -Suite $Suite | ConvertFrom-Json
        $matrix.include.GetType().IsArray | Should -BeTrue
        $matrix.include.Count | Should -Be $Count
        @($matrix.include | ForEach-Object { $_.lane + '/' + $_.suite } | Select-Object -Unique).Count | Should -Be $Count
        if ($Suite -eq 'All' -and $Lane -ne 'Kafka') {
            @($matrix.include.suite | Sort-Object) | Should -Be @('Admission', 'ApiE2E', 'History', 'Lifecycle', 'MessageContract', 'RecordSize', 'Recovery', 'Telemetry')
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
    It 'offers every provider suite in manual dispatch and reports the selected matrix count' {
        $options = [regex]::Match($script:scheduledWorkflow, '(?s)      suite:.*?options: \[([^\]]+)\]').Groups[1].Value.Split(',').Trim()
        $options | Should -Be (@('All') + @(Get-CdcQualificationProviderSuite -Provider Postgresql).Keys)
        $script:scheduledWorkflow | Should -Match 'suite-count: \$\{\{ steps.select.outputs.suite-count \}\}'
        $selection = [regex]::Match($script:scheduledWorkflow, '(?ms)^      - name: Select suites\r?\n.*?        run: \|\r?\n(?<run>(?:          [^\r\n]*\r?\n)+)').Groups['run'].Value
        $selection | Should -Not -BeNullOrEmpty
        $saved = @{}
        foreach ($name in @('GITHUB_OUTPUT', 'SELECTED_LANE', 'SELECTED_SUITE')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
        Push-Location (Join-Path $PSScriptRoot '../../..')
        try {
            foreach ($lane in @('All', 'Postgresql', 'Mssql')) {
                $env:SELECTED_LANE = $lane
                $env:SELECTED_SUITE = if ($lane -eq 'All') { 'All' } else { 'ApiE2E' }
                $env:GITHUB_OUTPUT = Join-Path $TestDrive "selection-$lane"
                & ([scriptblock]::Create($selection))
                $output = Get-Content $env:GITHUB_OUTPUT
                $selected = $output[0].Substring('matrix='.Length) | ConvertFrom-Json
                $selected.include.Count | Should -Be $(if ($lane -eq 'All') { 17 } else { 1 })
                $output[1] | Should -Be "suite-count=$($selected.include.Count)"
            }
        } finally {
            Pop-Location
            foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }
    }
    It 'supplies ApiE2E build prerequisites and delegates auth and schema preparation to the runner' {
        $script:scheduledJob | Should -Match 'uses: actions/setup-dotnet@[^\r\n]+\r?\n        with:\r?\n          dotnet-version: "10.0.x"'
        $script:scheduledJob | Should -Match "name: Set up Docker Buildx\r?\n        if: matrix.suite == 'ApiE2E'\r?\n        uses: docker/setup-buildx-action@"
        $script:scheduledJob | Should -Match "CDC_RUNBOOK_OWNED_STACK: '1'"
        $script:scheduledJob | Should -Not -Match 'run:.*(?:prepare-cdc-api-e2e|setup-local-dms|Initialize-CdcFixturePrincipal|dotnet build)'
        $budget = [int][regex]::Match($script:scheduledJob, 'timeout-minutes: (\d+)').Groups[1].Value * 60
        $runner = Get-Content (Join-Path $PSScriptRoot '../cdc-api-e2e.ps1') -Raw
        $setupSeconds = [int][regex]::Match($runner, '\$deadline = \[DateTimeOffset\]::UtcNow.AddMinutes\((\d+)\)').Groups[1].Value * 60
        $phaseSeconds = @([regex]::Matches($runner, '-LogPath \(Join-Path \$private ''(test|cleanup|export)''\) -TimeoutSeconds (\d+)') | ForEach-Object { [int]$_.Groups[2].Value })
        $setupSeconds | Should -BeGreaterThan 0
        $phaseSeconds.Count | Should -Be 3
        # Preserve at least twenty minutes outside all bounded runner phases.
        $budget | Should -BeGreaterOrEqual ($setupSeconds + ($phaseSeconds | Measure-Object -Sum).Sum + 1200)
    }
    It 'retains fail-closed prerequisites and sanitized artifacts in the nightly jobs' {
        $script:scheduledJob | Should -Match 'runs-on: ubuntu-latest'
        $script:scheduledJob | Should -Match 'timeout-minutes: 130'
        $script:scheduledJob | Should -Match 'fail-fast: false'
        $script:scheduledJob | Should -Not -Match 'continue-on-error:'
        foreach ($image in @('REDPANDA', 'POSTGRES', 'SQLSERVER_2025')) {
            $script:scheduledJob | Should -Match "CDC_CONNECTOR_TEMPLATE_$($image)_IMAGE:"
        }
        $script:scheduledJob | Should -Match 'Invoke-CdcQualification.ps1 -Lane.*-Suite.*-PullImages'
        $script:scheduledJob | Should -Match "Status = 'EnvironmentUnavailable'"
        $script:scheduledJob | Should -Match 'if: always\(\)'
        $script:scheduledJob | Should -Match 'path: \$\{\{ runner.temp \}\}/cdc-qualification/\*\*'
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
        $messages[0] | Should -Be ':heavy_check_mark: DMS CI nightly CDC qualification passed, all ${{ needs.select-suites.outputs.suite-count }} live suites verified'
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
        $script:job | Should -Match 'path: \$\{\{ runner.temp \}\}/cdc-qualification/\*\*'
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
        $suites.Count | Should -Be 8
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
        $runner | Should -Match 'Mssql Admission/Lifecycle/ApiE2E requires CDC_RUNBOOK_OWNED_STACK=1'
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
        $runner | Should -Match 'PostgreSQL Admission/Lifecycle/ApiE2E requires CDC_RUNBOOK_OWNED_STACK=1'
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
    It 'requires all <Provider> telemetry and live inspection cases (<Fault>)' -ForEach @(
        foreach ($provider in @('Postgresql', 'Mssql')) {
            foreach ($fault in @('none', 'excluded', 'skipped', 'partial', 'duplicate')) {
                @{ Provider = $provider; Fault = $fault }
            }
        }
    ) {
        $path = Join-Path $TestDrive 'telemetry.trx'
        $required = (Get-CdcRunbookTelemetryReport (Join-Path $TestDrive 'missing.trx') -Provider $Provider).Cases
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
        (Get-CdcRunbookTelemetryReport $path -Provider $Provider).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'selects only the matching provider inspection and retains the telemetry report guard' {
        $pg = (Get-CdcRunbookTelemetryReport (Join-Path $TestDrive 'missing.trx') -Provider Postgresql).Cases.TestId
        $sql = (Get-CdcRunbookTelemetryReport (Join-Path $TestDrive 'missing.trx') -Provider Mssql).Cases.TestId
        $pg | Should -Contain 'It_executes_marked_slot_disk_and_progress_inspections_with_unavailable_actions'
        $sql | Should -Contain 'It_executes_marked_capture_retention_version_store_and_disk_inspections_with_unavailable_actions'
        $pg | Should -Not -Contain $sql[1]
        $sql | Should -Not -Contain $pg[1]
        Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw |
            Should -Match 'Get-CdcRunbookTelemetryReport -Path.*-Provider \$selected'
    }
    It 'requires exact Kafka and consumer inspection cases (<Selection>/<Fault>)' -ForEach @(
        foreach ($selection in @('Secured', 'Local', 'Consumer')) {
            foreach ($fault in @('none', 'excluded', 'skipped', 'partial', 'duplicate')) {
                @{ Selection = $selection; Fault = $fault }
            }
        }
    ) {
        $path = Join-Path $TestDrive 'kafka.trx'
        $read = { param($p)
            if ($Selection -eq 'Consumer') { Get-CdcRunbookConsumerReport -Path $p }
            else { Get-CdcRunbookKafkaReport -Path $p -KafkaProfile $Selection }
        }
        $required = (& $read (Join-Path $TestDrive 'missing.trx')).Cases
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
        (& $read $path).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'keeps secured and local Kafka filters and the provider-neutral consumer guard distinct' {
        $runner = Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw
        $runner | Should -Match "Category=CdcControllerKafkaPolicy&Category=CdcAuthorizationEnabled"
        $runner | Should -Match "Category=CdcControllerKafkaPolicy&Category=CdcAuthorizationDisabledLocal"
        $runner | Should -Match 'Get-CdcRunbookKafkaReport -Path.*-KafkaProfile Secured'
        $runner | Should -Match 'Get-CdcRunbookKafkaReport -Path.*-KafkaProfile Local'
        $runner | Should -Match '\$phase -eq ''MessageContract'' -and \$selected -eq ''Postgresql'''
        $runner | Should -Match 'Get-CdcRunbookConsumerReport'
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
    It 'requires all packaged history and retirement cases (<Selection>, <Fault>)' -ForEach @(
        foreach ($selection in @('History', 'Cleanup')) {
            foreach ($fault in @('none', 'excluded', 'skipped', 'partial', 'duplicate')) {
                @{ Selection = $selection; Fault = $fault }
            }
        }
    ) {
        $path = Join-Path $TestDrive 'history.trx'
        $required = (Get-CdcRunbookHistoryReport (Join-Path $TestDrive 'missing.trx') -Selection $Selection).Cases
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
        (Get-CdcRunbookHistoryReport $path -Selection $Selection).Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
    }
    It 'selects the marked retirement retry with provider cleanup in both History lanes' {
        $runner = Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw
        $runner | Should -Match 'Category=CdcArtifactCleanup\|Category=CdcRunbookRetirement'
        $runner | Should -Match 'Get-CdcRunbookHistoryReport -Path.*-Selection Cleanup'
        $runner | Should -Match 'Get-CdcRunbookHistoryReport -Path.*\$name/\$name.trx'
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
    Context 'Contract link-check inventory' {
        BeforeAll {
            $linkTestsPath = Join-Path $PSScriptRoot '../../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookLinkTests.cs'
            $source = Get-Content -LiteralPath $linkTestsPath -Raw
            $inventory = [regex]::Match($source, '(?s)string\[\] Documents =\s*\[(.*?)\];')
            if (-not $inventory.Success) {
                throw 'Could not read CdcRunbookLinkTests.Documents; keep the checked-input guard aligned.'
            }
            $script:linkDocuments = @([regex]::Matches($inventory.Groups[1].Value, '"([^"]+)"') | ForEach-Object {
                $_.Groups[1].Value
            })
            if ($script:linkDocuments.Count -eq 0) {
                throw 'CdcRunbookLinkTests.Documents must not be empty.'
            }
            $script:linkTestId = 'It_resolves_relative_links_and_explicit_or_generated_anchors'
            $script:cliRequired = (Get-CdcRunbookCliReport (Join-Path $TestDrive 'absent.trx')).Cases
        }
        It 'requires the actual document inventory count' {
            ($script:cliRequired | Where-Object TestId -eq $script:linkTestId).Required | Should -Be $script:linkDocuments.Count
        }
        It 'evaluates document link coverage with every other method passing (<Fault>)' -ForEach @(
            @{ Fault = 'none' }, @{ Fault = 'missing-one-link' }
        ) {
            $path = Join-Path $TestDrive 'document-inventory.trx'
            $otherNodes = @($script:cliRequired | Where-Object TestId -ne $script:linkTestId | ForEach-Object {
                $case = $_
                for ($i = 0; $i -lt $case.Required; $i++) {
                    "<UnitTestResult testName='$($case.TestId)($i)' outcome='Passed'/>"
                }
            })
            $linkNodes = @($script:linkDocuments | ForEach-Object {
                $testName = [System.Security.SecurityElement]::Escape("$script:linkTestId(`"$_`")")
                "<UnitTestResult testName='$testName' outcome='Passed'/>"
            })
            if ($Fault -eq 'missing-one-link') { $linkNodes = @($linkNodes | Select-Object -Skip 1) }
            "<TestRun><Results>$(($otherNodes + $linkNodes) -join '')</Results></TestRun>" | Set-Content $path
            $report = Get-CdcRunbookCliReport $path
            $linkCase = $report.Cases | Where-Object TestId -eq $script:linkTestId
            $linkCase.Total | Should -Be $linkNodes.Count
            $linkCase.Passed | Should -Be $linkNodes.Count
            @($report.Cases | Where-Object { $_.TestId -ne $script:linkTestId -and $_.Outcome -ne 'Passed' }).Count | Should -Be 0
            $linkCase.Outcome | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'NotPassed' })
            $report.Status | Should -Be $(if ($Fault -eq 'none') { 'Passed' } else { 'Failed' })
        }
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
    It 'keeps wrapper checks in Contract without duplicating them in Bootstrap Pester' {
        $runner = Get-Content (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Raw
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/on-dms-pullrequest.yml') -Raw
        $bootstrap = [regex]::Match($workflow, '(?ms)^  run-bootstrap-pester-tests:.*?(?=^  [a-z][a-z0-9-]+:|\z)').Value
        $bootstrap | Should -Not -BeNullOrEmpty
        foreach ($pattern in @('eng/docker-compose/tests/Cdc\*\.Tests.ps1', 'eng/ci/tests/CdcQualification\.Tests.ps1', 'Get-CdcRunbookPesterReport')) {
            $runner | Should -Match $pattern
            $bootstrap | Should -Not -Match $pattern
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

Describe 'CDC API E2E accounting' {
    BeforeAll { Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force }
    BeforeEach {
        $runner = New-CdcApiRunnerReport Postgresql
        $runner.BindingId = 'a' * 64; $runner.Generation = 7; $runner.ProcessExit = 0
        foreach ($name in @($runner.Stages.Keys)) { $runner.Stages[$name] = 'Passed' }
        $script:process = @{ Status = 'Passed'; Total = 1; Passed = 1; Failed = 0; Skipped = 0 }
        $script:fixture = @{ Version = 1; InvocationId = $runner.InvocationId
            Identity = @{ Provider = 'Postgresql'; BindingId = $runner.BindingId; Generation = 7 }
            Attachment = @{ Id = 'Attachment'; Outcome = 'Passed'; Failure = 'None' }
            Disposal = @{ Id = 'Disposal'; Outcome = 'Passed'; Failure = 'None' }
            Scenarios = @(1..8 | ForEach-Object { @{ Id = ('CDC-E2E-{0:00}' -f $_); Outcome = 'Passed'; Failure = 'None' } }) }
        $path = Join-Path $TestDrive 'scenario.json'
        Remove-Item $path -ErrorAction SilentlyContinue
    }
    It 'accepts exactly eight matching phases with all independent outcomes' {
        $script:fixture | ConvertTo-Json -Depth 8 | Set-Content $path
        (Get-CdcApiScenarioReport $path $runner $script:process).Status | Should -Be Passed
    }
    It 'rejects <Fault> without accepting aggregate NUnit success' -ForEach @(
        @{ Fault = 'Invocation' }; @{ Fault = 'Provider' }; @{ Fault = 'Binding' }; @{ Fault = 'Generation' }
        @{ Fault = 'Missing' }; @{ Fault = 'Duplicate' }; @{ Fault = 'Substitute' }; @{ Fault = 'Order' }
        @{ Fault = 'NotRun' }; @{ Fault = 'Running' }; @{ Fault = 'Failed' }; @{ Fault = 'Skipped' }; @{ Fault = 'Aborted' }
        @{ Fault = 'Attachment' }; @{ Fault = 'Disposal' }; @{ Fault = 'TimedOut' }; @{ Fault = 'Cancelled' }
        @{ Fault = 'Unimplemented' }; @{ Fault = 'ZeroTests' }; @{ Fault = 'Exit' }; @{ Fault = 'Trx' }
        @{ Fault = 'Setup' }; @{ Fault = 'Test' }; @{ Fault = 'Teardown' }; @{ Fault = 'Export' }
        @{ Fault = 'Malformed' }; @{ Fault = 'Absent' }; @{ Fault = 'Oversized' }
    ) {
        switch ($Fault) {
            Invocation { $script:fixture.InvocationId = [guid]::NewGuid().ToString() }
            Provider { $script:fixture.Identity.Provider = 'Mssql' }
            Binding { $script:fixture.Identity.BindingId = 'b' * 64 }
            Generation { $script:fixture.Identity.Generation++ }
            Missing { $script:fixture.Scenarios = $script:fixture.Scenarios[0..6] }
            Duplicate { $script:fixture.Scenarios[7].Id = 'CDC-E2E-01' }
            Substitute { $script:fixture.Scenarios[7].Id = 'CdcSetupSmoke' }
            Order { $script:fixture.Scenarios = $script:fixture.Scenarios[7..0] }
            { $_ -in @('NotRun', 'Running', 'Failed', 'Skipped', 'Aborted') } { $script:fixture.Scenarios[2].Outcome = $Fault }
            { $_ -in @('Attachment', 'Disposal') } { $script:fixture[$Fault].Outcome = 'Failed' }
            { $_ -in @('TimedOut', 'Cancelled', 'Unimplemented') } { $script:fixture.Scenarios[2].Failure = $Fault }
            ZeroTests { $script:process.Total = 0; $script:process.Passed = 0 }
            Exit { $runner.ProcessExit = 1 }
            Trx { $script:process.Status = 'ReportIncomplete' }
            { $_ -in @('Setup', 'Test', 'Teardown', 'Export') } { $runner.Stages[$Fault] = 'Failed' }
        }
        $script:fixture | ConvertTo-Json -Depth 8 | Set-Content $path
        if ($Fault -eq 'Malformed') { '{' | Set-Content $path }
        if ($Fault -eq 'Absent') { Remove-Item $path }
        if ($Fault -eq 'Oversized') { ('x' * 32769) | Set-Content $path }
        (Get-CdcApiScenarioReport $path $runner $script:process).Status | Should -Be Failed
    }
    It 'exports bounded partial phases and stages without private inputs or changing the fixture report' {
        $script:fixture.Scenarios[2].Outcome = 'Failed'; $script:fixture.Scenarios[2].Failure = 'Unimplemented'
        $script:fixture.Scenarios[3..7] | ForEach-Object { $_.Outcome = 'NotRun' }
        $script:fixture.Password = 'planted-private-value'; $script:fixture.Scenarios[0].Body = @{ firstName = 'private-student' }
        $script:fixture | ConvertTo-Json -Depth 8 | Set-Content $path
        $hash = (Get-FileHash $path).Hash
        $out = Join-Path $TestDrive 'published'
        Export-CdcQualificationEvidence -RawDirectory $TestDrive -Destination $out -ApiRunner $runner
        $json = Get-Content (Join-Path $out 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'planted-private-value|private-student|Password|Body'
        ($json | ConvertFrom-Json).Scenarios.Count | Should -Be 8
        ($json | ConvertFrom-Json).Scenarios[2].Failure | Should -Be Unimplemented
        (Get-FileHash $path).Hash | Should -Be $hash
    }
    It 'exports failed attachment boundary <Boundary> with unavailable identity and independent disposal failure' -ForEach @(
        @{ Boundary = 'Handoff'; Failure = 'Error' }; @{ Boundary = 'ProvenanceOrHttpConfiguration'; Failure = 'Error' }
        @{ Boundary = 'RetainedBinding'; Failure = 'Error' }; @{ Boundary = 'HttpEndpoints'; Failure = 'Error' }
        @{ Boundary = 'Provider'; Failure = 'Error' }; @{ Boundary = 'KafkaAdvertisedEndpoints'; Failure = 'Error' }
        @{ Boundary = 'Connect'; Failure = 'TimedOut' }; @{ Boundary = 'Metrics'; Failure = 'Cancelled' }
        @{ Boundary = 'RuntimeIdentitySchema'; Failure = 'Error' }; @{ Boundary = 'ApiAuthentication'; Failure = 'Error' }
        @{ Boundary = 'None'; Failure = 'Error' }
    ) {
        $script:fixture.Identity = @{ Provider = ''; BindingId = ''; Generation = 0 }
        $script:fixture.Attachment.Outcome = 'Failed'; $script:fixture.Attachment.Failure = $Failure
        $script:fixture.AttachmentBoundary = $Boundary
        $script:fixture.Attachment.Message = 'credential-sentinel body-sentinel'
        $script:fixture.Disposal.Outcome = 'Failed'; $script:fixture.Disposal.Failure = 'Error'
        $script:fixture.Scenarios | ForEach-Object { $_.Outcome = 'NotRun' }
        $script:fixture | ConvertTo-Json -Depth 8 | Set-Content $path
        $validated = Get-CdcApiScenarioReport $path $runner $script:process
        $validated.Status | Should -Be Failed
        $validated.AttachmentBoundary | Should -BeExactly $Boundary
        $validated.AttachmentFailure | Should -BeExactly $Failure
        $out = Join-Path $TestDrive 'attachment-published'
        Export-CdcQualificationEvidence -RawDirectory $TestDrive -Destination $out -ApiRunner $runner
        $json = Get-Content (Join-Path $out 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'credential-sentinel|body-sentinel|Message'
        $safe = $json | ConvertFrom-Json
        $safe.AttachmentBoundary | Should -BeExactly $Boundary
        $safe.AttachmentFailure | Should -BeExactly $Failure
        $safe.Attachment | Should -Be Failed
        $safe.Disposal | Should -Be Failed
        @($safe.Scenarios | Where-Object Outcome -ne NotRun).Count | Should -Be 0
    }
    It 'rejects <Fault> attachment boundary without exporting untrusted values' -ForEach @(
        @{ Fault = 'Unknown' }; @{ Fault = 'Sensitive' }; @{ Fault = 'Array' }; @{ Fault = 'Object' }
        @{ Fault = 'Number' }; @{ Fault = 'Null' }; @{ Fault = 'Case' }; @{ Fault = 'Passed' }
        @{ Fault = 'NoFailure' }; @{ Fault = 'WrongInvocation' }; @{ Fault = 'FailureArray' }
    ) {
        $script:fixture.Attachment.Outcome = 'Failed'; $script:fixture.Attachment.Failure = 'Error'
        $script:fixture.AttachmentBoundary = 'Provider'
        switch ($Fault) {
            Unknown { $script:fixture.AttachmentBoundary = 'UnknownBoundary' }
            Sensitive { $script:fixture.AttachmentBoundary = 'Password=credential-sentinel; body-sentinel /private/path' }
            Array { $script:fixture.AttachmentBoundary = @('Provider', 'credential-sentinel') }
            Object { $script:fixture.AttachmentBoundary = @{ Provider = 'credential-sentinel' } }
            Number { $script:fixture.AttachmentBoundary = 999 }
            Null { $script:fixture.AttachmentBoundary = $null }
            Case { $script:fixture.AttachmentBoundary = 'provider' }
            Passed { $script:fixture.Attachment.Outcome = 'Passed' }
            NoFailure { $script:fixture.Attachment.Failure = 'None' }
            WrongInvocation { $script:fixture.InvocationId = [guid]::NewGuid().ToString() }
            FailureArray { $script:fixture.Attachment.Failure = @('Error', 'credential-sentinel') }
        }
        $script:fixture | ConvertTo-Json -Depth 8 | Set-Content $path
        $validated = Get-CdcApiScenarioReport $path $runner $script:process
        $validated.Status | Should -Be Failed
        $validated.Reason | Should -Be $(if ($Fault -eq 'WrongInvocation') { 'ScenarioIdentityMismatch' } else { 'ScenarioReportInvalid' })
        $out = Join-Path $TestDrive 'rejected-attachment'
        Export-CdcQualificationEvidence -RawDirectory $TestDrive -Destination $out -ApiRunner $runner
        $json = Get-Content (Join-Path $out 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'credential-sentinel|body-sentinel|UnknownBoundary|/private/path'
        ($json | ConvertFrom-Json).AttachmentBoundary | Should -Be None
        ($json | ConvertFrom-Json).AttachmentFailure | Should -Be None
    }
    It 'exports setup failure with no fixture report' {
        $runner.Stages.Setup = 'Failed'; $runner.BindingId = ''; $runner.Generation = 0
        Export-CdcQualificationEvidence -RawDirectory $TestDrive -Destination (Join-Path $TestDrive 'out') -ApiRunner $runner
        $evidence = Get-Content (Join-Path $TestDrive 'out/cdc-api-e2e.json') -Raw | ConvertFrom-Json
        $evidence.Stages.Setup | Should -Be Failed
        $evidence.Scenarios.Count | Should -Be 0
    }
}

Describe 'CDC API E2E native process budgets' {
    BeforeDiscovery { Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force }
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
    }
    InModuleScope cdc-qualification {
        It 'accepts the actual <Seconds>-second stage budget through the real transport' -ForEach @(
            @{ Seconds = 1800 }, @{ Seconds = 3600 }, @{ Seconds = 900 }
        ) {
            $result = Invoke-CdcApiProcess -FilePath pwsh -Arguments @('-NoProfile', '-Command', 'exit 0') `
                -LogPath (Join-Path $TestDrive "budget-$Seconds") -TimeoutSeconds $Seconds
            $result.FailureKind | Should -Be None
            $result.ExitCode | Should -Be 0
            $result.TimedOut | Should -BeFalse
        }
    }
}

Describe 'CDC API E2E runner stages' {
    BeforeDiscovery { Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force }
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
        Import-Module (Join-Path $PSScriptRoot '../../docker-compose/bootstrap-schema-tool.psm1')
    }
    InModuleScope cdc-qualification {
        BeforeEach {
            $script:repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $script:events = [Collections.Generic.List[string]]::new()
            $script:fault = ''
            $script:runner = New-CdcApiRunnerReport Postgresql
            $script:root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path $script:root
            $script:destination = Join-Path $script:root 'out'
            $null = New-Item -ItemType Directory -Path $script:destination
            Mock Assert-CdcApiWorkspaceAbsent { if ($script:fault -eq 'Ownership') { throw 'Occupied' } }
            Mock Resolve-DmsSchemaTool { $env:DMS_SCHEMA_TOOL_PATH }
            Mock Invoke-CdcApiProcess {
                $name = Split-Path $LogPath -Leaf
                $script:events.Add($name)
                if ($name -eq 'setup') {
                    Test-Path (Join-Path $script:root "$($script:runner.Name)/ownership.json") | Should -BeTrue
                    $private = Split-Path $LogPath -Parent
                    'private-handoff' | Set-Content (Join-Path $private 'handoff-path.txt')
                }
                if ($name -eq 'inputs') {
                    @{ Provider = $script:runner.Provider; BindingId = ('a' * 64); Generation = 3; Database = 'custom_e2e' } |
                        ConvertTo-Json | Set-Content (Join-Path (Split-Path $LogPath -Parent) 'admitted.json')
                }
                if ($name -eq 'test') {
                    $env:AppSettings__DataStoreDatabaseName | Should -Be custom_e2e
                    $env:NODE_OPTIONS | Should -BeNullOrEmpty
                    $Arguments | Should -Contain 'FullyQualifiedName~Given_CdcApiE2E'
                    $env:CDC_API_E2E_INVOCATION_ID | Should -Be $script:runner.InvocationId
                    @{ Version = 1; InvocationId = $script:runner.InvocationId
                        Identity = @{ Provider = $script:runner.Provider; BindingId = ('a' * 64); Generation = 3 }
                        Attachment = @{ Id = 'Attachment'; Outcome = 'Passed'; Failure = 'None' }
                        Disposal = @{ Id = 'Disposal'; Outcome = 'Passed'; Failure = 'None' }
                        Scenarios = @(1..8 | ForEach-Object { @{ Id = ('CDC-E2E-{0:00}' -f $_); Outcome = 'Passed'; Failure = 'None' } }) } |
                        ConvertTo-Json -Depth 8 | Set-Content $env:CDC_API_E2E_REPORT_PATH
                    $script:fixtureHash = (Get-FileHash $env:CDC_API_E2E_REPORT_PATH).Hash
                }
                $failed = $script:fault -ieq $name -or ($script:fault -in @('TimeoutAndCleanup', 'TimeoutAndCleanupAndExport') -and $name -in @('test', 'cleanup')) -or ($script:fault -eq 'TimeoutAndCleanupAndExport' -and $name -eq 'export')
                if ($name -eq 'cleanup') { $TimeoutSeconds | Should -Be 900 }
                if ($name -eq 'export') {
                    $TimeoutSeconds | Should -Be 120
                    if (-not $failed) {
                        & pwsh @Arguments *> (Join-Path (Split-Path $LogPath -Parent) 'export-test.log')
                        $LASTEXITCODE | Should -Be 0
                    }
                }
                @{ ExitCode = $(if ($failed) { 1 } else { 0 }); FailureKind = $(if ($failed) { 'TimedOut' } else { 'None' }) }
            }
            Mock Get-CdcQualificationReport { @{ Status = 'Passed'; Total = 1; Passed = 1; Failed = 0; Skipped = 0 } }

        }
        It 'builds current selected and Debug tools before one setup, eight phases and independent cleanup for <Provider>' -ForEach @(
            @{ Provider = 'Postgresql' }; @{ Provider = 'Mssql' }
        ) {
            $script:runner.Provider = $Provider
            Invoke-CdcApiQualification -Repo $script:repo -RawDirectory $script:root -Destination $script:destination -Configuration Release -Report $script:runner -Persist { }
            $script:events | Should -Be @('build-Release', 'build-Debug', 'setup', 'inputs', 'test', 'cleanup', 'export')
            $script:runner.Status | Should -Be Passed
            (Get-FileHash (Join-Path $script:root "$($script:runner.Name)/scenario.json")).Hash | Should -Be $script:fixtureHash
        }
        It 'still tears down and exports when incremental summary persistence fails' {
            Invoke-CdcApiQualification -Repo $script:repo -RawDirectory $script:root -Destination $script:destination -Configuration Release -Report $script:runner -Persist {
                if ($script:runner.Stages.Teardown -ne 'NotRun' -and $script:runner.Status -eq 'Running') { throw 'summary write failed' }
            }
            $script:events | Should -Contain cleanup
            $script:events | Should -Contain export
            $script:runner.ReportFailure | Should -Be ReportPersistenceFailed
            $script:runner.Status | Should -Be Failed
        }
        It 'refreshes a stale Debug tool before resolving either wrapper selection' {
            $staleTool = Join-Path $script:root 'api-schema-tools'
            'stale' | Set-Content $staleTool
            Mock Invoke-CdcApiProcess { 'current' | Set-Content (Join-Path $script:root 'api-schema-tools'); @{ ExitCode = 0; FailureKind = 'None' } } -ParameterFilter { $LogPath -like '*build-Debug' }
            Mock Resolve-DmsSchemaTool {
                (Get-Content (Join-Path $script:root 'api-schema-tools') -Raw).Trim() | Should -Be current
                $env:DMS_SCHEMA_TOOL_PATH | Should -Be (Join-Path $script:repo 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Debug/net10.0/api-schema-tools')
                $env:DMS_SCHEMA_TOOL_PATH
            }
            Invoke-CdcApiQualification -Repo $script:repo -RawDirectory $script:root -Destination $script:destination -Configuration Release -Report $script:runner -Persist { }
            $script:runner.Status | Should -Be Passed
            Should -Invoke Resolve-DmsSchemaTool -Times 2 -Exactly
        }
        It 'retains the original failure and attempts only owned cleanup after <Fault>' -ForEach @(
            @{ Fault = 'Ownership'; Cleanup = $false }; @{ Fault = 'build-Release'; Cleanup = $false }
            @{ Fault = 'build-Debug'; Cleanup = $false }; @{ Fault = 'setup'; Cleanup = $true }
            @{ Fault = 'inputs'; Cleanup = $true }; @{ Fault = 'test'; Cleanup = $true }
            @{ Fault = 'cleanup'; Cleanup = $true }; @{ Fault = 'Export'; Cleanup = $true }
            @{ Fault = 'TimeoutAndCleanup'; Cleanup = $true }; @{ Fault = 'TimeoutAndCleanupAndExport'; Cleanup = $true }
        ) {
            $script:fault = $Fault
            Invoke-CdcApiQualification -Repo $script:repo -RawDirectory $script:root -Destination $script:destination -Configuration Release -Report $script:runner -Persist { }
            $script:runner.Status | Should -Not -Be Passed
            ($script:events -contains 'cleanup') | Should -Be $Cleanup
            if ($Fault -in @('TimeoutAndCleanup', 'TimeoutAndCleanupAndExport')) {
                $script:runner.Reason | Should -Be ScenarioExecutionFailed
                $script:runner.CleanupFailure | Should -Be CleanupFailed
            }
            if ($Fault -in @('Export', 'TimeoutAndCleanupAndExport')) { $script:runner.ExportFailure | Should -Be ExportFailed }
            if ($Fault -notin @('Export', 'TimeoutAndCleanupAndExport')) {
                $evidence = Get-Content (Join-Path $script:destination "$($script:runner.Name)/cdc-api-e2e.json") -Raw | ConvertFrom-Json
                $evidence.InvocationId | Should -Be $script:runner.InvocationId
                $evidence.Stages.Teardown | Should -Be $script:runner.Stages.Teardown
                $evidence.Failures.Reason | Should -Be $(if ($Fault -eq 'cleanup') { 'None' } else { $script:runner.Reason })
                if ($Fault -eq 'cleanup') { $evidence.ReportValidation | Should -Be RunnerStageFailed }
                $evidence.Failures.CleanupFailure | Should -Be $script:runner.CleanupFailure
            }
            if ($Fault -like 'build-*' -or $Fault -eq 'Ownership') { $script:events | Should -Not -Contain 'setup' }
        }
    }
}

Describe 'CDC API E2E private preparation and ownership' {
    BeforeDiscovery { Import-Module (Join-Path $PSScriptRoot '../../docker-compose/tests/cdc-fixture-inputs.psm1') -Force }
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../../docker-compose/tests/cdc-fixture-inputs.psm1') -Force
        $script:repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    }
    It 'creates matching private <Provider> inputs without a database or final CDC settings' -ForEach @(
        @{ Provider = 'Postgresql'; SqlServer = $false }; @{ Provider = 'Mssql'; SqlServer = $true }
    ) {
        $private = Join-Path $TestDrive $Provider
        $inputs = New-CdcFixtureInput -Repo $script:repo -FixtureRoot $private -SqlServer $SqlServer -E2e $true -ProviderImage 'pinned-provider' -LargeWorker $true
        Test-Path $inputs.SettingsPath | Should -BeFalse
        Test-Path $inputs.StatePath | Should -BeFalse
        $inputs.Values[$inputs.ImageVariable] | Should -Be pinned-provider
        $inputs.Values.CDC_DATABASE_PASSWORD | Should -Be $inputs.ConnectorPassword
        $inputs.Values.CDC_WORKER_HEAP_MIB | Should -Be 1024
        $base = Get-Content (Join-Path $inputs.Local 'dms-base.json') -Raw | ConvertFrom-Json
        $base.ConfigurationServiceSettings.ClientSecret | Should -Be $inputs.Values.CONFIG_SERVICE_CLIENT_SECRET
        $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connection.set_ConnectionString((Get-Content $inputs.InputPath -Raw).Trim())
        $connection['database'] | Should -Be $inputs.Database
        $connection['password'] | Should -Be $inputs.Password
        [int][IO.File]::GetUnixFileMode($inputs.EnvironmentFile) | Should -Be 384
        [int][IO.File]::GetUnixFileMode($private) | Should -Be 448
    }
    InModuleScope cdc-fixture-inputs {
        BeforeEach {
            $script:sql = ''
            Mock Invoke-NativeCommandWithInput {
                $script:sql = $InputText
                $ArgumentList | Should -Not -Contain 'private-password'
                @{ ExitCode = 0; FailureKind = 'None' }
            }
            Mock docker { $global:LASTEXITCODE = 0; '[{"Config":{"Env":["MSSQL_SA_PASSWORD=admin-password"]}}]' }
        }
        It 'creates a restricted PostgreSQL role and checks absence before admission' {
            Initialize-CdcFixturePrincipal -SqlServer $false -Inputs @{ Database = 'custom_e2e'; ConnectorPassword = 'private-password' }
            $script:sql | Should -Match "pg_database WHERE datname = 'custom_e2e'"
            $script:sql | Should -Match 'LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE'
            $script:sql | Should -Not -Match 'CREATE DATABASE|CREATE PUBLICATION|GRANT'
        }
        It 'creates only the SQL Server login, using the container secret over stdin' {
            Initialize-CdcFixturePrincipal -SqlServer $true -Inputs @{ Database = 'custom_e2e'; ConnectorPassword = 'private-password'; Password = 'admin-password' }
            $script:sql | Should -Match "DB_ID\(N'custom_e2e'\)"
            $script:sql | Should -Match 'CREATE LOGIN cdc_reader'
            $script:sql | Should -Not -Match 'CREATE DATABASE|sp_cdc|GRANT'
        }
        It 'rejects a failed role or login operation for <SqlServer>' -ForEach @(@{ SqlServer = $false }; @{ SqlServer = $true }) {
            Mock Invoke-NativeCommandWithInput { @{ ExitCode = 1; FailureKind = 'None' } }
            { Initialize-CdcFixturePrincipal -SqlServer $SqlServer -Inputs @{ Database = 'custom_e2e'; ConnectorPassword = 'p'; Password = 'admin-password' } } | Should -Throw '*principal preparation failed*'
        }
    }
    It 'preserves marked runbook infrastructure and settings while both callers share private preparation' {
        $runbook = Get-Content (Join-Path $script:repo 'eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1') -Raw
        $api = Get-Content (Join-Path $script:repo 'eng/ci/prepare-cdc-api-e2e.ps1') -Raw
        foreach ($code in @($runbook, $api)) {
            $code | Should -Match 'New-CdcFixtureInput'
            $code | Should -Match 'Initialize-CdcFixturePrincipal'
        }
        $runbook | Should -Match 'Get-CdcRunbookCode "\$prefix-settings"'
        $runbook | Should -Match 'Get-CdcRunbookCode "\$prefix-infrastructure"'
        $api | Should -Not -Match 'cdc-runbook-snippets|Get-CdcRunbookCode'
    }
}

Describe 'CDC API E2E absence gate' {
    BeforeDiscovery { Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force }
    InModuleScope cdc-qualification {
        BeforeEach {
            $script:owned = $env:CDC_RUNBOOK_OWNED_STACK
            $env:CDC_RUNBOOK_OWNED_STACK = '1'
            $script:resources = ''
            Mock Invoke-CdcApiProcess { @{ ExitCode = 0; FailureKind = 'None'; StandardOutput = $script:resources } }
        }
        AfterEach { $env:CDC_RUNBOOK_OWNED_STACK = $script:owned }
        It 'rejects absent opt-in before querying resources' {
            $env:CDC_RUNBOOK_OWNED_STACK = ''
            { Assert-CdcApiWorkspaceAbsent $TestDrive $TestDrive } | Should -Throw '*OWNERSHIP_REQUIRED*'
            Should -Invoke Invoke-CdcApiProcess -Times 0
        }
        It 'rejects any existing project resource without authorizing cleanup' {
            $script:resources = 'pre-existing'
            { Assert-CdcApiWorkspaceAbsent $TestDrive $TestDrive } | Should -Throw '*WORKSPACE_OCCUPIED*'
        }
        It 'requires both projects containers volumes and networks to be absent' {
            Assert-CdcApiWorkspaceAbsent $TestDrive $TestDrive
            Should -Invoke Invoke-CdcApiProcess -Times 6 -Exactly
        }
    }
}

Describe 'CDC API E2E setup and governed cleanup adapters' {
    BeforeAll {
        $script:sourceRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        function Copy-FixtureFile([string] $Relative) {
            $target = Join-Path $script:fakeRepo $Relative
            $null = New-Item -ItemType Directory -Path (Split-Path $target) -Force
            Copy-Item (Join-Path $script:sourceRepo $Relative) $target
        }
    }
    BeforeEach {
        $ErrorActionPreference = 'Stop'
        $script:fakeRepo = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:private = Join-Path $script:fakeRepo 'private'
        $null = New-Item -ItemType Directory -Path $script:private -Force
        foreach ($file in @('eng/docker-compose/.env.e2e', 'eng/docker-compose/env-utility.psm1', 'eng/docker-compose/database-safety.psm1',
            'eng/docker-compose/e2e-teardown.psm1', 'eng/docker-compose/tests/cdc-fixture-inputs.psm1',
            'src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json',
            'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json')) { Copy-FixtureFile $file }
        # Native/provider boundaries only are replaced. Actual private input/settings construction executes.
        @'
function Initialize-CdcFixturePrincipal {
    param([bool] $SqlServer, [hashtable] $Inputs)
    'principal' | Add-Content (Join-Path (Split-Path $Inputs.EnvironmentFile) 'calls.txt')
    if ($env:TEST_API_FAILURE -eq 'principal') { throw 'injected principal failure' }
}
'@ | Add-Content (Join-Path $script:fakeRepo 'eng/docker-compose/tests/cdc-fixture-inputs.psm1')
        @'
param([switch] $DbOnly, [switch] $InfraOnly, [switch] $EnableConfig, [switch] $SeparateConfigDatabase, [switch] $CdcDatabaseInfrastructure,
    [string] $DatabaseEngine, [string] $IdentityProvider, [string] $EnvironmentFile,
    [switch] $d, [switch] $v, [switch] $RemoveBootstrap)
if ($d) { 'primitive' | Add-Content (Join-Path (Split-Path $EnvironmentFile) 'calls.txt'); $global:LASTEXITCODE = 0; return }
if (-not $DbOnly -or $InfraOnly -or $EnableConfig -or $SeparateConfigDatabase -or $CdcDatabaseInfrastructure) { throw 'Principal preparation must not start CMS before claims staging' }
'infrastructure' | Add-Content (Join-Path (Split-Path $EnvironmentFile) 'calls.txt')
if ($env:TEST_API_FAILURE -eq 'infrastructure') { throw 'partial infrastructure startup' }
$global:LASTEXITCODE = 0
'@ | Set-Content (Join-Path $script:fakeRepo 'eng/docker-compose/start-local-dms.ps1')
        $script:e2e = Join-Path $script:fakeRepo 'src/dms/tests/EdFi.DataManagementService.Tests.E2E'
        $null = New-Item -ItemType Directory -Path $script:e2e -Force
        @'
param([string] $EnvironmentFile, [string] $DatabaseEngine, [string] $CdcSettingsPath, [string] $CdcBindingStatePath,
    [switch] $EnableKafkaCdc, [switch] $CdcApiE2E)
if (-not ($EnableKafkaCdc -and $CdcApiE2E -and $CdcSettingsPath -and $CdcBindingStatePath -and $EnvironmentFile)) { throw 'Missing explicit admission argument' }
$private = Split-Path $EnvironmentFile
'admission' | Add-Content (Join-Path $private 'calls.txt')
$settings = Get-Content $CdcSettingsPath -Raw | ConvertFrom-Json
$binding = @{ version = 1; deploymentKey = 'local'; tenantKey = 'default'; dataStoreId = '1'; instanceKey = 'datastore-1'; generation = 1
    provider = $(if ($DatabaseEngine -eq 'mssql') { 'sqlServer' } else { 'postgresql' }); physicalSourceFingerprint = ('sha256:' + ('a' * 64)); connectorName = 'local-datastore-1-g1'
    topicName = 'edfi.dms.instance.datastore-1-g1.documents.v1'; partitionCount = 3; partitionerAlgorithm = 'kafka-murmur2-v1'; contractVersion = 1 }
$bindingDir = Join-Path $CdcBindingStatePath 'bindings/local/datastore-1'
$null = New-Item -ItemType Directory -Path $bindingDir -Force
Get-ChildItem $CdcBindingStatePath -Directory -Recurse | ForEach-Object { & chmod 700 $_.FullName }
$binding | ConvertTo-Json | Set-Content (Join-Path $bindingDir '1.json')
& chmod 600 (Join-Path $bindingDir '1.json')
$handoff = Join-Path $private 'handoff.json'
@{ version = 1; settingsPath = $CdcSettingsPath; statePath = $CdcBindingStatePath } | ConvertTo-Json | Set-Content $handoff
$global:LASTEXITCODE = 0
return $handoff
'@ | Set-Content (Join-Path $script:e2e 'setup-local-dms.ps1')
        @'
param([string] $EnvironmentFile, [string] $DatabaseEngine)
'governed' | Add-Content (Join-Path (Split-Path $EnvironmentFile) 'calls.txt')
if ($env:TEST_API_FAILURE -eq 'cleanup') { throw 'injected cleanup failure' }
Remove-Item (Join-Path $PSScriptRoot '../../../../eng/docker-compose/.cdc-deployments/dms-local.json')
$global:LASTEXITCODE = 0
'@ | Set-Content (Join-Path $script:e2e 'teardown-local-dms.ps1')
        $script:savedImage = $env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE
        $env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE = (Get-Content (Join-Path $script:fakeRepo 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json') -Raw | ConvertFrom-Json).image
        $script:savedPg = $env:CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE
        $script:savedSql = $env:CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE
        $env:CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE = 'postgres:pinned'; $env:CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE = 'sqlserver:pinned'
        $script:savedFault = $env:TEST_API_FAILURE
        $env:TEST_API_FAILURE = ''
    }
    AfterEach {
        $env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE = $script:savedImage
        $env:CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE = $script:savedPg
        $env:CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE = $script:savedSql
        $env:TEST_API_FAILURE = $script:savedFault
    }
    It 'dispatches one explicit <Provider> admission whose settings load through production configuration and retained state' -ForEach @(
        @{ Provider = 'Postgresql'; SettingsName = 'postgresql' }; @{ Provider = 'Mssql'; SettingsName = 'sqlserver' }
    ) {
        $handoffOutput = Join-Path $script:private 'handoff-path.txt'
        & pwsh -NoProfile -File (Join-Path $script:sourceRepo 'eng/ci/prepare-cdc-api-e2e.ps1') -Repo $script:fakeRepo -PrivateDirectory $script:private -Provider $Provider -HandoffOutput $handoffOutput *> (Join-Path $script:private 'prepare.log')
        $LASTEXITCODE | Should -Be 0 -Because (Get-Content (Join-Path $script:private 'prepare.log') -Raw)
        @(Get-Content (Join-Path $script:private 'calls.txt')) | Should -Be @('infrastructure', 'principal', 'admission')
        $settingsPath = Join-Path $script:private ".local/cdc/$SettingsName-e2e.json"
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.Cdc.Provider | Should -Be $SettingsName
        $settings.Cdc.Worker.HeapBytes | Should -Be 1073741824
        $resolved = Join-Path $script:private 'resolved.json'
        $handoff = (Get-Content $handoffOutput -Raw).Trim()
        & dotnet run --file (Join-Path $script:sourceRepo 'eng/ci/CdcApiInputs.cs') -- $handoff $resolved *> (Join-Path $script:private 'resolve.log')
        $LASTEXITCODE | Should -Be 0 -Because (Get-Content (Join-Path $script:private 'resolve.log') -Raw)
        $inputs = Get-Content $resolved -Raw | ConvertFrom-Json
        $inputs.Provider | Should -Be $Provider
        $inputs.Database | Should -Be edfi_datamanagementservice_e2e
        $inputs.Generation | Should -Be 1
        $inputs.BindingId | Should -Match '^[a-f0-9]{64}$'
    }
    It 'uses only owned project cleanup after <Fault> with no deployment inventory' -ForEach @(
        @{ Fault = 'infrastructure' }; @{ Fault = 'principal' }
    ) {
        $env:TEST_API_FAILURE = $Fault
        & pwsh -NoProfile -File (Join-Path $script:sourceRepo 'eng/ci/prepare-cdc-api-e2e.ps1') -Repo $script:fakeRepo -PrivateDirectory $script:private -Provider Postgresql -HandoffOutput (Join-Path $script:private 'handoff-path.txt') *> (Join-Path $script:private 'failure.log')
        $LASTEXITCODE | Should -Not -Be 0
        (Get-Content (Join-Path $script:private 'calls.txt')) | Should -Not -Contain admission
        $id = [guid]::NewGuid().ToString()
        @{ InvocationId = $id; Project = 'dms-local'; AbsentBeforeStart = $true } | ConvertTo-Json | Set-Content (Join-Path $script:private 'ownership.json')
        $driver = Join-Path $script:private 'cleanup-driver.ps1'
        @'
param($Script, $Repo, $PrivateDirectory, $InvocationId)
function global:docker { $global:LASTEXITCODE = 0 }
& $Script -Repo $Repo -PrivateDirectory $PrivateDirectory -Provider Postgresql -InvocationId $InvocationId
'@ | Set-Content $driver
        & pwsh -NoProfile -File $driver (Join-Path $script:sourceRepo 'eng/ci/cleanup-cdc-api-e2e.ps1') $script:fakeRepo $script:private $id *> (Join-Path $script:private 'cleanup.log')
        $LASTEXITCODE | Should -Be 0 -Because (Get-Content (Join-Path $script:private 'cleanup.log') -Raw)
        (Get-Content (Join-Path $script:private 'calls.txt'))[-1] | Should -Be primitive
        Test-Path (Join-Path $script:private 'owned-resources.json') | Should -BeTrue
    }
    It 'requires matching ownership and chooses governed teardown once inventory exists (<Fault>)' -ForEach @(
        @{ Fault = 'None' }; @{ Fault = 'cleanup' }; @{ Fault = 'Ownership' }
    ) {
        $env:TEST_API_FAILURE = $Fault
        $id = [guid]::NewGuid().ToString()
        @{ InvocationId = $id; Project = 'dms-local'; AbsentBeforeStart = ($Fault -ne 'Ownership') } | ConvertTo-Json | Set-Content (Join-Path $script:private 'ownership.json')
        Copy-Item (Join-Path $script:fakeRepo 'eng/docker-compose/.env.e2e') (Join-Path $script:private '.env.e2e')
        $inventoryDir = Join-Path $script:fakeRepo 'eng/docker-compose/.cdc-deployments'
        $null = New-Item -ItemType Directory -Path $inventoryDir
        '{}' | Set-Content (Join-Path $inventoryDir 'dms-local.json')
        $state = Join-Path $script:private '.local/cdc/state-pg-e2e'
        $null = New-Item -ItemType Directory -Path $state -Force
        'retain' | Set-Content (Join-Path $state 'provenance')
        $driver = Join-Path $script:private 'cleanup-driver.ps1'
        @'
param($Script, $Repo, $PrivateDirectory, $InvocationId)
function global:docker { $global:LASTEXITCODE = 0 }
& $Script -Repo $Repo -PrivateDirectory $PrivateDirectory -Provider Postgresql -InvocationId $InvocationId
'@ | Set-Content $driver
        & pwsh -NoProfile -File $driver (Join-Path $script:sourceRepo 'eng/ci/cleanup-cdc-api-e2e.ps1') $script:fakeRepo $script:private $id *> (Join-Path $script:private 'cleanup.log')
        if ($Fault -eq 'None') {
            $LASTEXITCODE | Should -Be 0
            Test-Path $state | Should -BeFalse
        } else {
            $LASTEXITCODE | Should -Not -Be 0
            Test-Path (Join-Path $state 'provenance') | Should -BeTrue
        }
        if ($Fault -eq 'Ownership') { Test-Path (Join-Path $script:private 'calls.txt') | Should -BeFalse }
        else { @(Get-Content (Join-Path $script:private 'calls.txt')) | Should -Be @('governed') }
    }
}

Describe 'CDC API E2E prerequisite summary' {
    It 'retains sanitized evidence for always-upload after the workflow runner fails for <Provider>' -ForEach @(
        @{ Provider = 'Postgresql' }; @{ Provider = 'Mssql' }
    ) {
        $runner = Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1'
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/nightly-cdc-qualification.yml') -Raw
        $job = [regex]::Match($workflow, '(?ms)^  run-cdc-qualification:.*?(?=^  [a-z][a-z0-9-]+:|\z)').Value
        $command = [regex]::Match($job, '(?m)^        run: (\./eng/ci/Invoke-CdcQualification.ps1[^\r\n]+)').Groups[1].Value
        $command | Should -Not -BeNullOrEmpty
        $command = $command.Replace('./eng/ci/Invoke-CdcQualification.ps1', '& $Runner').Replace('${{ matrix.lane }}', $Provider).Replace('${{ matrix.suite }}', 'ApiE2E')
        $fallback = [regex]::Match($job, '(?ms)^      - name: Record unavailable qualification environment\r?\n        if: failure\(\)\r?\n        run: \|\r?\n(?<run>(?:          [^\r\n]*\r?\n)+)').Groups['run'].Value
        $fallback | Should -Not -BeNullOrEmpty
        $upload = [regex]::Match($job, '(?ms)^      - name: Upload structured CDC qualification evidence\r?\n.*?(?=^      - name:|\z)').Value
        $upload | Should -Match 'if: always\(\)\r?\n        uses: actions/upload-artifact@'
        $upload | Should -Match 'path: \$\{\{ runner.temp \}\}/cdc-qualification/\*\*'
        $upload | Should -Not -Match 'private|RUNNER_TEMP|\.log|\.trx|\.env|handoff'
        $workspace = Join-Path $TestDrive $Provider
        $null = New-Item -ItemType Directory -Path $workspace
        $destination = Join-Path $workspace 'cdc-qualification'
        $driver = Join-Path $TestDrive 'missing-ownership.ps1'
        @'
param($Runner, $Workspace, $Command)
Set-Location $Workspace
[Environment]::CurrentDirectory = $Workspace
$env:RUNNER_TEMP = $Workspace
$env:CDC_RUNBOOK_OWNED_STACK = ''
function global:docker { throw 'Prerequisite rejection must precede Docker.' }
function global:dotnet { throw 'Prerequisite rejection must precede builds/tests.' }
& ([scriptblock]::Create($Command))
exit $LASTEXITCODE
'@ | Set-Content $driver
        & pwsh -NoProfile -File $driver $runner $workspace $command *> (Join-Path $TestDrive 'prerequisite.log')
        $LASTEXITCODE | Should -Be 1
        Test-Path (Join-Path $destination 'qualification.json') | Should -BeTrue
        $summaryHash = (Get-FileHash (Join-Path $destination 'qualification.json')).Hash
        Push-Location $workspace
        $savedRunnerTemp = $env:RUNNER_TEMP
        try { $env:RUNNER_TEMP = $workspace; & ([scriptblock]::Create($fallback)) }
        finally { $env:RUNNER_TEMP = $savedRunnerTemp; Pop-Location }
        (Get-FileHash (Join-Path $destination 'qualification.json')).Hash | Should -Be $summaryHash
        $report = @(Get-Content (Join-Path $destination 'qualification.json') -Raw | ConvertFrom-Json | Where-Object Name -eq "$Provider-ApiE2E")[0]
        $report.InvocationId | Should -Match '^[a-f0-9-]{36}$'
        $report.Stages.Setup | Should -Be Failed
        $report.Stages.Test | Should -Be NotRun
        $report.Status | Should -Be EnvironmentUnavailable
        $evidence = Get-Content (Join-Path $destination "$Provider-ApiE2E/cdc-api-e2e.json") -Raw | ConvertFrom-Json
        $evidence.InvocationId | Should -Be $report.InvocationId
        $evidence.Scenarios.Count | Should -Be 0
    }
}

Describe 'CDC API E2E bounded diagnostics export' {
    BeforeAll { Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force }
    BeforeEach {
        $raw = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        $script:diagnosticOut = Join-Path $raw 'out'
        $runner = New-CdcApiRunnerReport Postgresql
        $runner.BindingId = 'a' * 64; $runner.Generation = 1
        $runner.ProcessResult = @{ Status = 'Passed'; Total = 1; Passed = 1; Failed = 0; Skipped = 0 }
        $runner.ProcessExit = 0
        foreach ($name in @('Setup', 'Test', 'Teardown', 'Export')) { $runner.Stages[$name] = 'Passed' }
        $fixture = @{ Version = 1; InvocationId = $runner.InvocationId
            Identity = @{ Provider = 'Postgresql'; BindingId = $runner.BindingId; Generation = 1 }
            Attachment = @{ Id = 'Attachment'; Outcome = 'Passed'; Failure = 'None' }
            Disposal = @{ Id = 'Disposal'; Outcome = 'Passed'; Failure = 'None' }
            Scenarios = @(1..8 | ForEach-Object { @{ Id = ('CDC-E2E-{0:00}' -f $_); Outcome = 'Passed'; Failure = 'None' } }) }
        $checkpointPath = Join-Path $raw 'scenario.json.checkpoints'
        $lines = @(
            'Attachment:effectiveSchema=' + ('b' * 64)
            'Attachment:runtime=10.0.5'
            'Attachment:pageSize=100'
            'CDC-E2E-01:admitted-identity-held-create: sourceVersion=2 cacheVersion=0 workVersion=2'
            'CDC-E2E-02:create-consumed: public partition=1 start=2 end=3'
            'CDC-E2E-03:N-completed: candidateVersion=2 outcome=StaleCandidateSuppressed'
            'CDC-E2E-04:tombstone-consumed-while-held: partition=2 offset=17'
            'CDC-E2E-05:administration: completed=SeedBaseline lifecycle=Rebuilding cacheAhead=False'
            'CDC-E2E-06:old-runtime-disposed: utc=2026-09-28T12:00:00.1234567+00:00'
            'CDC-E2E-06:work-page: size=2 selected=2'
            'CDC-E2E-06:replacement-operations: baselineBoundaries=0 baselinePages=0 inventoryPages=0 dropped=0'
            'CDC-E2E-07:healthy-publication:provider-fence-completed'
            'CDC-E2E-07:rejected-Resume-Connect-Unavailable: utc=2026-09-28T12:00:00.1234567+00:00'
            'CDC-E2E-08:healthy-publication:progress:partition=0:start=5:end=6'
            'CDC-E2E-08:lost-NotReady-ConnectOffsetMissing-persisted-contained'
            'CDC-E2E-08:fresh-controller-retained-incident:healthy-offset-reads=0'
        )
        $fixture | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $raw 'scenario.json')
        @($runner.InvocationId) + $lines | Set-Content $checkpointPath
    }
    It 'retains exact boundary, work, recovery and invariant evidence for <Outcome> without recomputing outcomes' -ForEach @(
        @{ Outcome = 'Passed' }; @{ Outcome = 'Failed' }; @{ Outcome = 'Running' }
    ) {
        $fixture.Scenarios[5].Outcome = $Outcome
        if ($Outcome -ne 'Passed') {
            $fixture.Scenarios[6..7] | ForEach-Object { $_.Outcome = 'NotRun' }
            $runner.Stages.Test = 'Failed'; $runner.Reason = 'ScenarioExecutionFailed'
            $runner.CleanupFailure = 'CleanupFailed'; $runner.Stages.Teardown = 'Failed'
        }
        $fixture | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $raw 'scenario.json')
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $safe = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw | ConvertFrom-Json
        $safe.InvocationId | Should -Be $runner.InvocationId
        $safe.Diagnostics.Checkpoints | Should -Be $lines
        $safe.Diagnostics.Rejected | Should -Be 0
        $safe.Scenarios[5].Outcome | Should -Be $Outcome
        $safe.Traceability.Count | Should -Be 8
        $safe.Traceability[5].Invariants | Should -Contain 'CDC-INV-03'
        $safe.Traceability[7].Invariants | Should -Contain 'CDC-INV-11'
        $safe.Failures.CleanupFailure | Should -Be $runner.CleanupFailure
        $safe.Failures.Reason | Should -Be $runner.Reason
    }
    It 'rejects planted bodies, credentials, unknown fields and arbitrary checkpoint names' {
        @('CDC-E2E-01:private-student', 'CDC-E2E-01:create-consumed: sourceVersion=1 cacheVersion=1 workVersion=1 password=opaque',
            'CDC-E2E-01:create-consumed: public partition=0 start=0 end=1 secret', 'Authorization: Bearer opaque',
            '{"firstName":"private-student"}', ('x' * 513)) | Add-Content $checkpointPath
        $runner.Stages.Secret = 'opaque'
        $runner.BindingId = 'Server=private;Password=opaque'
        $runner.Reason = 'private-student'; $runner.RequestedConnectDigest = 'opaque'
        '{"private-student":"opaque"}' | Set-Content (Join-Path $raw 'settings.json')
        # ApiE2E never scans sibling attachments or raw TRX, even allowlisted basenames.
        '{"Unlabelled":"opaque"}' | Set-Content (Join-Path $raw 'native-recovery-secret.json')
        'private-student opaque' | Set-Content (Join-Path $raw 'api.trx')
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $json = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'opaque|private-student|Password|Authorization|Server='
        ($json | ConvertFrom-Json).Diagnostics.Checkpoints | Should -Be $lines
        ($json | ConvertFrom-Json).Diagnostics.Rejected | Should -Be 6
        @(Get-ChildItem $script:diagnosticOut).Count | Should -Be 1
    }
    It 'keeps private failure causes out of exported diagnostics' {
        'failure:CDC-E2E-02: InvalidOperationException: Descriptor POST returned Forbidden' | Add-Content $checkpointPath
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $json = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'InvalidOperationException|Descriptor POST returned Forbidden|failure:CDC-E2E-02'
        ($json | ConvertFrom-Json).Diagnostics.Checkpoints | Should -Be $lines
        ($json | ConvertFrom-Json).Diagnostics.Rejected | Should -Be 1
    }
    It 'bounds or rejects <Fault> diagnostic input explicitly' -ForEach @(
        @{ Fault = 'Oversized'; Availability = 'Oversized' }; @{ Fault = 'Count'; Availability = 'Available' }
        @{ Fault = 'Identity'; Availability = 'IdentityMismatch' }; @{ Fault = 'Missing'; Availability = 'Missing' }
    ) {
        switch ($Fault) {
            Oversized { ('x' * 1050662) | Set-Content $checkpointPath }
            Count { @($runner.InvocationId) + @('CDC-E2E-04:projector-released-and-drained') * 2050 | Set-Content $checkpointPath }
            Identity { @([guid]::NewGuid().ToString()) + $lines | Set-Content $checkpointPath }
            Missing { Remove-Item $checkpointPath }
        }
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $safe = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw | ConvertFrom-Json
        $safe.Diagnostics.Availability | Should -Be $Availability
        $safe.Diagnostics.Checkpoints.Count | Should -Be $(if ($Fault -eq 'Count') { 2048 } else { 0 })
        $safe.Diagnostics.Truncated | Should -Be ($Fault -eq 'Count')
        (Get-Item (Join-Path $script:diagnosticOut 'cdc-api-e2e.json')).Length | Should -BeLessThan 1100000
    }
    It 'exports actual image digests and verified cleanup counts, omitting names and configuration' {
        @{ InvocationId = $runner.InvocationId; Availability = 'Available'; CleanupMode = 'Governed'; ResourcesAbsent = $true
            OwnedCounts = @{ container = 5; volume = 3; network = 1 }; Secret = 'opaque'
            Images = @(@{ Service = 'kafka-cdc-worker'; ImageId = ('sha256:' + ('c' * 64)); ManifestDigest = ('sha256:' + ('d' * 64)); Config = 'opaque' }
                @{ Service = 'db'; ImageId = ('sha256:' + ('e' * 64)); ManifestDigest = ''; Database = 'private-student' }
                @{ Service = 'private-student'; ImageId = 'opaque' }) } |
            ConvertTo-Json -Depth 8 | Set-Content (Join-Path $raw 'runtime-inputs.json')
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $json = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'opaque|private-student|Secret|Database|Config'
        $safe = $json | ConvertFrom-Json
        $safe.RuntimeInputs.Images.Count | Should -Be 2
        $safe.RuntimeInputs.Images[0].ManifestDigest | Should -Be ('sha256:' + ('d' * 64))
        $safe.RuntimeInputs.OwnedCounts.container | Should -Be 5
        $safe.RuntimeInputs.ResourcesAbsent | Should -BeTrue
    }
    It 'rejects a fixture from another invocation without attributing its phases' {
        $fixture.InvocationId = [guid]::NewGuid().ToString()
        $fixture | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $raw 'scenario.json')
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $safe = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw | ConvertFrom-Json
        $safe.ReportValidation | Should -Be ScenarioIdentityMismatch
        $safe.Scenarios.Count | Should -Be 0
    }
    It 'rejects <Fault> runtime data without leaking identifiers or nested content' -ForEach @(
        @{ Fault = 'Oversized'; Availability = 'Oversized' }; @{ Fault = 'Identity'; Availability = 'IdentityMismatch' }
        @{ Fault = 'Malformed'; Availability = 'Invalid' }; @{ Fault = 'Nested'; Availability = 'Available' }
    ) {
        $path = Join-Path $raw 'runtime-inputs.json'
        $inputData = @{ InvocationId = $runner.InvocationId; Availability = 'Available'; CleanupMode = 'OwnedPartial'; ResourcesAbsent = $false
            OwnedCounts = @{ container = 2; volume = 0; network = 0 }
            Images = @(@{ Service = 'db'; ImageId = @('sha256:' + ('a' * 64), 'opaque'); ManifestDigest = 'opaque' }) }
        if ($Fault -eq 'Identity') { $inputData.InvocationId = [guid]::NewGuid().ToString() }
        $inputData | ConvertTo-Json -Depth 8 | Set-Content $path
        if ($Fault -eq 'Oversized') { ('opaque' * 3000) | Set-Content $path }
        if ($Fault -eq 'Malformed') { '{opaque' | Set-Content $path }
        Export-CdcQualificationEvidence $raw $script:diagnosticOut $runner
        $json = Get-Content (Join-Path $script:diagnosticOut 'cdc-api-e2e.json') -Raw
        $json | Should -Not -Match 'opaque'
        $safe = $json | ConvertFrom-Json
        $safe.RuntimeInputs.Availability | Should -Be $Availability
        $safe.RuntimeInputs.Images.Count | Should -Be 0
    }
    InModuleScope cdc-qualification {
        It 'collects only image fields before cleanup and keeps inspecting after one failure' {
            Import-Module (Join-Path $PSScriptRoot '../../docker-compose/env-utility.psm1') -DisableNameChecking
            $script:inspect = 0
            Mock Invoke-NativeCommandWithInput {
                $script:inspect++
                $ArgumentList[2] | Should -Not -Match 'Env|Connection|Password'
                $TimeoutSeconds | Should -Be 10
                if ($script:inspect -eq 1) { return @{ ExitCode = 1; FailureKind = 'TimedOut' } }
                @{ ExitCode = 0; FailureKind = 'None'; StandardOutput = (@{ Service = 'kafka'; ImageId = ('sha256:' + ('a' * 64)); Reference = ('apache/kafka@sha256:' + ('b' * 64)) } | ConvertTo-Json) }
            }
            $path = Join-Path $TestDrive 'actual.json'; $id = [guid]::NewGuid().ToString()
            Write-CdcApiRuntimeInput $path $id @{ container = @('first', 'second'); volume = @('private-volume'); network = @('private-network') } $true
            $safe = Get-CdcApiRuntimeInput $path $id
            $safe.Availability | Should -Be Partial
            $safe.Images.Count | Should -Be 1
            $safe.Images[0].ImageId | Should -Be ('sha256:' + ('a' * 64))
            $safe.ResourcesAbsent | Should -BeFalse
            $safe.OwnedCounts.container | Should -Be 2
            Should -Invoke Invoke-NativeCommandWithInput -Times 2 -Exactly
        }
    }
}

Describe 'CDC runbook failure diagnostics' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-qualification.psm1') -Force
        . (Join-Path $PSScriptRoot '../../docker-compose/tests/cdc-runbook-snippets.ps1')
        Import-Module (Join-Path $PSScriptRoot '../../docker-compose/env-utility.psm1') -DisableNameChecking
    }
    BeforeEach {
        $script:diagnosticRoot = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        $script:private = New-Item -ItemType Directory (Join-Path $script:diagnosticRoot 'raw')
        $script:published = Join-Path $script:diagnosticRoot 'published'
        $script:savedEvidence = $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY
        $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY = $script:private.FullName
        $script:runbookProgress = @{}
        $script:runbookCase = 'cdc-managed-start'
        Remove-Variable runbookOperation -Scope Script -ErrorAction SilentlyContinue
    }
    AfterEach {
        $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY = $script:savedEvidence
        Remove-Variable runbookProgress,runbookOperation,runbookOperationCase,runbookCase,runbookTimer -Scope Script -ErrorAction SilentlyContinue
    }
    It 'exports the assertion location and independent teardown failure without assertion values' {
        $config = New-PesterConfiguration
        $config.Run.Container = New-PesterContainer -ScriptBlock {
            Describe 'injected failures' {
                It 'CDC-DOC cdc-managed-start' { 'opaque-credential' | Should -Be 'private-document' }
                AfterAll { throw 'unlabelled-cleanup-secret' }
            }
        }
        $config.Run.PassThru = $true
        $config.Output.Verbosity = 'None'
        $nested = Invoke-Pester -Configuration $config
        $report = Get-CdcRunbookPesterReport -Tests @($nested.Tests) -PesterResult $nested -QualificationProfile MssqlLifecycle
        $report | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $script:private 'cdc-runbook-live-lifecycle.json')
        ConvertTo-Json -InputObject @($report.Failures) -Depth 10 | Set-Content (Join-Path $script:private 'cdc-runbook-failures.json')
        Export-CdcQualificationEvidence $script:private $script:published
        $cases = Get-Content (Join-Path $script:published 'cdc-runbook-live-lifecycle.json') -Raw | ConvertFrom-Json -NoEnumerate
        $failures = Get-Content (Join-Path $script:published 'cdc-runbook-failures.json') -Raw | ConvertFrom-Json -NoEnumerate
        $report.Status | Should -Be 'Failed'
        $cases[0].Failures[0].Category | Should -Be 'AssertionFailed'
        $cases[0].Failures[0].Source | Should -Be 'eng/ci/tests/CdcQualification.Tests.ps1'
        $line = $cases[0].Failures[0].Line
        (Get-Content $PSCommandPath)[$line - 1] | Should -Match 'opaque-credential.*Should -Be'
        $cases[0].Failures[0].Phase | Should -Be 'Test'
        $failures[0].Category | Should -Be 'BlockFailed'
        $failures[0].Phase | Should -Be 'Teardown'
        (Get-ChildItem $script:published -File | Get-Content -Raw) -join '' | Should -Not -Match 'opaque|private-document|unlabelled|/home/|ScriptStackTrace'
    }
    It 'reports BeforeAll failure even though the required test never runs' {
        $config = New-PesterConfiguration
        $config.Run.Container = New-PesterContainer -ScriptBlock {
            Describe 'failed setup' {
                BeforeAll { throw 'private setup secret' }
                It 'CDC-DOC cdc-managed-start' { throw 'must not run' }
            }
        }
        $config.Run.PassThru = $true
        $config.Output.Verbosity = 'None'
        $nested = Invoke-Pester -Configuration $config
        $report = Get-CdcRunbookPesterReport -Tests @($nested.Tests) -PesterResult $nested -QualificationProfile MssqlLifecycle
        $report.Status | Should -Be 'Failed'
        $report.Cases[0].Outcome | Should -Be 'NotPassed'
        $report.Failures[0].Phase | Should -Be 'Setup'
        $report.Failures[0].Category | Should -Be 'BlockFailed'
        $report.Failures[0].Source | Should -Be 'eng/ci/tests/CdcQualification.Tests.ps1'
        ($report | ConvertTo-Json -Depth 15) | Should -Not -Match 'private setup|must not run'
    }
    It 'persists the operation before invocation and exports timeout timing without process output' {
        Mock Invoke-NativeCommandWithInput {
            $progress = Get-Content (Join-Path $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY 'runbook-progress.json') -Raw | ConvertFrom-Json
            $progress.'cdc-managed-start'[0].Status | Should -Be 'Running'
            $progress.'cdc-managed-start'[0].Operation | Should -Be 'cdc-managed-start'
            [pscustomobject]@{ ExitCode = -1; FailureKind = 'None'; TimedOut = $true; StandardOutput = 'opaque process output'; StandardError = 'private error' }
        }
        $null = Invoke-CdcRunbookLiveWrapper -Id cdc-managed-start -FixtureRoot $script:private -TimeoutSeconds 2
        $report = Get-CdcRunbookPesterReport -Tests @([pscustomobject]@{ ExpandedName = 'CDC-DOC cdc-managed-start'; Result = 'Failed' }) -QualificationProfile MssqlLifecycle -ProgressPath (Join-Path $script:private 'runbook-progress.json')
        $report | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $script:private 'cdc-runbook-live-lifecycle.json')
        Export-CdcQualificationEvidence $script:private $script:published
        $cases = Get-Content (Join-Path $script:published 'cdc-runbook-live-lifecycle.json') -Raw | ConvertFrom-Json -NoEnumerate
        $operation = $cases[0].Operations[0]
        $operation.Operation | Should -Be 'cdc-managed-start'
        $operation.FailureKind | Should -Be 'Timeout'
        $operation.ExitCode | Should -Be -1
        $operation.TimeoutSeconds | Should -Be 2
        $operation.ElapsedMilliseconds | Should -BeGreaterOrEqual 0
        $operation.Status | Should -Be 'Completed'
        @(Get-ChildItem $script:published -Name) | Should -Be @('cdc-runbook-live-lifecycle.json')
        (Get-Content (Join-Path $script:published 'cdc-runbook-live-lifecycle.json') -Raw) | Should -Not -Match 'opaque|private error|StandardOutput'
    }
    It 'keeps process failure when progress collection also fails' {
        Mock Invoke-NativeCommandWithInput {
            [pscustomobject]@{ ExitCode = 17; FailureKind = 'StartFailure'; StandardOutput = ''; StandardError = 'private' }
        }
        # Existing parent, but a file occupies the expected temporary output path.
        $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY = Join-Path $script:private 'not-a-directory'
        Set-Content $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY 'occupied'
        $result = Invoke-CdcRunbookLiveWrapper -Id cdc-managed-start -FixtureRoot $script:private
        $result.ExitCode | Should -Be 17
        $result.FailureKind | Should -Be 'StartFailure'
        $script:runbookProgress.CollectionFailed | Should -BeTrue
        $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY = $script:private.FullName
        Save-CdcRunbookProgress
        $report = Get-CdcRunbookPesterReport -Tests @([pscustomobject]@{ ExpandedName = 'CDC-DOC cdc-managed-start'; Result = 'Failed' }) -QualificationProfile MssqlLifecycle -ProgressPath (Join-Path $script:private 'runbook-progress.json')
        $report.Cases[0].Operations[0].ExitCode | Should -Be 17
        $report.Failures[0].Category | Should -Be 'CollectionFailed'
    }
    It 'does not mark an interrupted case complete when the next case starts' {
        Set-CdcRunbookOperation -Operation verify-deployment
        $script:runbookCase = 'cdc-pg-e2e-setup'
        Set-CdcRunbookOperation -Operation ownership-check -Phase Setup
        Complete-CdcRunbookOperation
        $progress = Get-Content (Join-Path $script:private 'runbook-progress.json') -Raw | ConvertFrom-Json
        $progress.'cdc-managed-start'[0].Status | Should -Be 'Running'
        $progress.'cdc-pg-e2e-setup'[0].Status | Should -Be 'Completed'
        $progress.'cdc-pg-e2e-setup'[0].PSObject.Properties.Name | Should -Not -Contain ExitCode
    }
    It 'preserves the primary assertion when artifact export fails' {
        $report = [ordered]@{ Status = 'Failed'; Cases = @(@{ TestId = 'CDC-DOC cdc-managed-start';
            SnippetId = 'cdc-managed-start'; Outcome = 'NotPassed';
            Failures = @(@{ Phase = 'Test'; Category = 'AssertionFailed'; Line = 17 }) }); Failures = @() }
        Mock -ModuleName cdc-qualification Export-CdcQualificationEvidence { throw 'opaque export secret' }
        Export-CdcRunbookReport -Report $report -RawDirectory $script:private -Destination $script:published -FileName 'cdc-runbook-live-lifecycle.json'
        $report.Status | Should -Be 'Failed'
        $report.Cases[0].Failures[0].Category | Should -Be 'AssertionFailed'
        $report.Cases[0].Failures[0].Line | Should -Be 17
        $report.ExportFailure | Should -Be 'ExportFailed'
        ($report | ConvertTo-Json -Depth 10) | Should -Not -Match 'opaque export secret'
        Should -Invoke -ModuleName cdc-qualification Export-CdcQualificationEvidence -Times 1 -Exactly
    }
    It 'rejects arbitrary diagnostic strings at the final export boundary' {
        @{
            Cases = @(@{ TestId = 'CDC-DOC cdc-managed-start'; SnippetId = 'cdc-managed-start'; Outcome = 'NotPassed'
                Failures = @(@{ Phase = 'private'; Category = 'opaque'; Source = '/home/private.ps1'; Line = 'secret'; Message = 'raw' })
                Operations = @(@{ Operation = 'cdc-secret-credential'; FailureKind = 'password'; ExitCode = 'secret'; ElapsedMilliseconds = -1 }) })
        } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $script:private 'cdc-runbook-live-lifecycle.json')
        Export-CdcQualificationEvidence $script:private $script:published
        $json = Get-Content (Join-Path $script:published 'cdc-runbook-live-lifecycle.json') -Raw
        $json | Should -Not -Match 'private|opaque|secret|raw|password|ElapsedMilliseconds'
        ($json | ConvertFrom-Json -NoEnumerate)[0].Outcome | Should -Be 'NotPassed'
    }
}

Describe 'CDC qualification diagnostic destination' {
    It 'rejects a results directory inside the checkout before creating any files or running tests' {
        $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $destination = Join-Path $repo ('cdc-forbidden-' + [guid]::NewGuid().ToString('N'))
        $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '../Invoke-CdcQualification.ps1') -Lane Contract -ResultsDirectory $destination 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Match 'outside the repository'
        Test-Path -LiteralPath $destination | Should -BeFalse
    }
}
