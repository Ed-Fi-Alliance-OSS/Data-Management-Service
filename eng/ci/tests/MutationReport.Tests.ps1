# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'Weekend mutation report validation' {
    BeforeAll {
        $script:gate = Join-Path $PSScriptRoot '../Test-MutationReport.ps1'

        function New-MutationReport {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
                Justification = 'Writes only disposable Pester TestDrive fixtures.')]
            param([string] $Directory, [string[]] $Statuses)

            $mutants = @(
                for ($i = 0; $i -lt $Statuses.Count; $i++) {
                    @{
                        id = "$i"
                        status = $Statuses[$i]
                        mutatorName = 'Boolean mutation'
                        replacement = "false`n&& false"
                        statusReason = 'Test result reason'
                        coveredBy = @('test-123')
                        location = @{ start = @{ line = 42; column = 7 }; end = @{ line = 42; column = 11 } }
                    }
                }
            )
            $report = @{
                schemaVersion = '2'
                files = @{ 'src/Authorization.cs' = @{ source = 'return true;'; mutants = $mutants } }
            }
            $reportDirectory = Join-Path $Directory 'stryker/current/reports'
            New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
            $report | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $reportDirectory 'mutation-report.json')
        }
    }

    BeforeEach {
        $script:output = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:oldSummaryPath = $env:GITHUB_STEP_SUMMARY
        $env:GITHUB_STEP_SUMMARY = Join-Path $TestDrive 'github-summary.md'
        Remove-Item $env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue
    }

    AfterEach {
        $env:GITHUB_STEP_SUMMARY = $script:oldSummaryPath
    }

    It 'passes detected mutants and reports timeouts and excluded mutants separately' {
        New-MutationReport $script:output @('Killed', 'Timeout', 'CompileError', 'Ignored')
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Not -Throw
        $summary = Get-Content (Join-Path $script:output 'summary.md') -Raw
        $summary | Should -Match 'Mutation report validated'
        $summary | Should -Match '\| Timeout \| 1 \|'
        $summary | Should -Match '\| CompileError \| 1 \|'
        Get-Content $env:GITHUB_STEP_SUMMARY -Raw | Should -Be $summary
    }

    It 'reports <Status> with source-level diagnostics without failing' -ForEach @(
        @{ Status = 'Survived' }
        @{ Status = 'NoCoverage' }
    ) {
        New-MutationReport $script:output @('Killed', $Status)
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Not -Throw
        $details = Get-Content (Join-Path $script:output 'mutant-details.txt') -Raw
        $details | Should -Match "$Status \| src/Authorization.cs:42:7 \| mutant 1"
        $details | Should -Match 'Boolean mutation'
        $details | Should -Match 'false\\n&& false'
        $details | Should -Match 'Test result reason'
        $details | Should -Match 'test-123'
        $summary = Get-Content $env:GITHUB_STEP_SUMMARY -Raw
        $summary | Should -Match "\| $Status \| 1 \|"
        $summary | Should -Match 'Surviving and uncovered mutants are informational'
    }

    It 'reports exact survivor counts even when a reported score rounds to 100' {
        New-MutationReport $script:output @('Killed', 'Survived')
        $path = Join-Path $script:output 'stryker/current/reports/mutation-report.json'
        $report = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $report['mutationScore'] = 100
        $report | ConvertTo-Json -Depth 15 | Set-Content $path
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Not -Throw
        Get-Content $env:GITHUB_STEP_SUMMARY -Raw | Should -Match '\| Survived \| 1 \|'
    }

    It 'fails incomplete or non-executable reports (<Statuses>)' -ForEach @(
        @{ Statuses = @('Killed', 'Pending'); Message = '*pending mutant*' }
        @{ Statuses = @('Killed', 'Unexpected'); Message = '*unrecognized status*' }
        @{ Statuses = @('CompileError', 'Ignored'); Message = '*No executable mutants*' }
        @{ Statuses = @(); Message = '*No executable mutants*' }
    ) {
        New-MutationReport $script:output $Statuses
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Throw $Message
        Get-Content (Join-Path $script:output 'summary.md') -Raw | Should -Match 'Mutation report validation failed'
    }

    It 'fails missing reports and still writes a useful summary' {
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Throw '*found 0*'
        Get-Content $env:GITHUB_STEP_SUMMARY -Raw | Should -Match 'Inspect console.log and tool-restore.log'
    }

    It 'rejects stale or ambiguous results rather than selecting a previous passing report' {
        New-MutationReport $script:output @('Killed')
        New-MutationReport (Join-Path $script:output 'previous') @('Killed')
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Throw '*found 2*'
    }

    It 'rejects malformed reports (<Content>) and logs the failure' -ForEach @(
        @{ Content = '{' }
        @{ Content = '{}' }
        @{ Content = '{"files":{"broken.cs":{}}}' }
    ) {
        New-Item -ItemType Directory -Path $script:output -Force | Out-Null
        $Content | Set-Content (Join-Path $script:output 'mutation-report.json')
        { & $script:gate -OutputDirectory $script:output -Target 'test-target' } | Should -Throw
        Get-Content (Join-Path $script:output 'summary.md') -Raw | Should -Match 'Mutation report validation failed'
    }
}

Describe 'Weekend mutation workflow' {
    BeforeAll {
        $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
        $script:workflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/weekend-mutation-tests.yml') -Raw
    }

    It 'covers every named mutation configuration once and leaves exploratory configurations out' {
        $entries = [regex]::Matches($script:workflow,
            '(?m)^          - target: (.+)\r?\n            directory: (.+)\r?\n            config: (.+)\r?$')
        $entries.Count | Should -Be 11
        @($entries | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique).Count | Should -Be 11
        $configured = @($entries | ForEach-Object {
            $path = Join-Path -Path $script:repoRoot -ChildPath $_.Groups[2].Value -AdditionalChildPath $_.Groups[3].Value
            Test-Path -LiteralPath $path | Should -BeTrue
            (Resolve-Path $path).Path
        })
        $named = @(Get-ChildItem (Join-Path $script:repoRoot 'src') -Filter 'stryker-*.json' -Recurse -File |
            Where-Object { $_.Name -ne 'stryker-config.json' -and $_.FullName -notmatch '[/\\](bin|obj|StrykerOutput)[/\\]' } |
            Select-Object -ExpandProperty FullName)
        Compare-Object ($configured | Sort-Object) ($named | Sort-Object) | Should -BeNullOrEmpty
    }

    It 'runs all targets through failures and always attempts report validation and artifact upload' {
        $script:workflow | Should -Match 'fail-fast: false'
        $script:workflow | Should -Match 'set -euo pipefail'
        $script:workflow | Should -Match '--break-on-initial-test-failure --log-to-file'
        $script:workflow | Should -Match '(?m)      - name: Validate report and summarize mutation findings\r?\n        if: always\(\)'
        $script:workflow | Should -Match '(?m)      - name: Upload mutation reports and diagnostics\r?\n        if: always\(\)'
        $script:workflow | Should -Not -Match 'continue-on-error|pull_request:'
    }

    It 'preserves a Stryker runner failure through tee and captures its diagnostic output' -Skip:$IsWindows {
        $runScript = [regex]::Match($script:workflow, '(?ms)        run: \|\r?\n(.*?)\r?\n      - name: Validate report').Groups[1].Value
        $runScript | Should -Not -BeNullOrEmpty
        $stubDirectory = Join-Path $TestDrive 'stub-bin'
        New-Item -ItemType Directory -Path $stubDirectory | Out-Null
        $stub = Join-Path $stubDirectory 'dotnet'
        @'
#!/usr/bin/env bash
if [[ "$1" == "stryker" ]]; then
  echo 'Stryker baseline test failed' >&2
  exit 37
fi
exit 0
'@ | Set-Content $stub
        & chmod +x $stub
        $config = Join-Path $TestDrive 'stryker.json'
        '{}' | Set-Content $config
        $values = @{
            PATH = "$stubDirectory$([IO.Path]::PathSeparator)$env:PATH"
            ARTIFACT_DIRECTORY = Join-Path $TestDrive 'runner-failure'
            CONFIG_FILE = $config
            TARGET = 'test-target'
            GITHUB_SHA = 'test-commit'
        }
        $original = @{}
        foreach ($key in $values.Keys) {
            $original[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $values[$key])
        }
        try {
            $PSNativeCommandUseErrorActionPreference = $false
            & bash -c $runScript | Out-Null
            $LASTEXITCODE | Should -Be 37
            Get-Content (Join-Path $values.ARTIFACT_DIRECTORY 'console.log') -Raw |
                Should -Match 'Stryker baseline test failed'
        }
        finally {
            foreach ($key in $original.Keys) {
                [Environment]::SetEnvironmentVariable($key, $original[$key])
            }
        }
    }

    It 'schedules Saturday with one aggregate brief Slack result and suppresses manual and fork notifications' {
        $script:workflow | Should -Match 'cron: "0 8 \* \* 6"'
        $notification = [regex]::Match($script:workflow, '(?ms)^  notify-results:.*\z').Value
        $notification | Should -Match '(?m)^    needs: mutation-tests$'
        $notification | Should -Match "if: always\(\) && github.event_name == 'schedule' && github.repository_owner == 'Ed-Fi-Alliance-OSS'"
        $notification | Should -Match "if: needs.mutation-tests.result == 'success'"
        $notification | Should -Match "if: needs.mutation-tests.result != 'success'"
        $payloads = @([regex]::Matches($notification, '(?ms)          payload: \|\r?\n(.*?)          webhook:') |
            ForEach-Object { $_.Groups[1].Value | ConvertFrom-Json })
        $payloads.Count | Should -Be 2
        foreach ($payload in $payloads) {
            @($payload.PSObject.Properties.Name) | Should -Be @('text')
            $payload.text | Should -Not -Match '[\r\n]'
        }
        $payloads[1].text | Should -Match 'actions/runs/.*View run'
        [regex]::Matches($notification, 'webhook: \$\{\{ secrets.SLACK_WEBHOOK_URL \}\}').Count | Should -Be 2
    }
}
