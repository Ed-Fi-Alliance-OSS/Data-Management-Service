# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe 'Configuration Service PR change detection and required checks' {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $script:workflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/on-config-pullrequest.yml') -Raw

        # Only Git is controlled. Execute the same Bash script the workflow invokes,
        # including event handling, diff failure behavior, and GITHUB_OUTPUT emission.
        function Invoke-ConfigDetection {
            param(
                [string[]] $ChangedFile = @(),
                [string] $EventName = 'pull_request',
                [string] $BaseSha = 'base-sha',
                [string] $FailCommand = ''
            )

            $caseDirectory = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            $files = Join-Path $caseDirectory 'files.txt'
            $output = Join-Path $caseDirectory 'github-output.txt'
            Set-Content -LiteralPath $files -Value $ChangedFile
            $start = [Diagnostics.ProcessStartInfo]::new('bash')
            $start.UseShellExecute = $false
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.ArgumentList.Add('-c')
            $start.ArgumentList.Add(@'
git() {
  if [[ "$1" == "$FAIL_COMMAND" ]]; then return 1; fi
  case "$1" in
    fetch) return 0 ;;
    diff) cat "$CHANGED_FILE_LIST" ;;
    *) return 2 ;;
  esac
}
export -f git
bash "$CLASSIFIER"
'@)
            $start.Environment['CLASSIFIER'] = Join-Path $script:repoRoot 'eng/ci/detect-config-changes.sh'
            $start.Environment['CHANGED_FILE_LIST'] = $files
            $start.Environment['FAIL_COMMAND'] = $FailCommand
            $start.Environment['GITHUB_OUTPUT'] = $output
            $start.Environment['GITHUB_EVENT_NAME'] = $EventName
            $start.Environment['GITHUB_BASE_REF'] = 'main'
            $start.Environment['GITHUB_SHA'] = 'head-sha'
            $start.Environment['MERGE_GROUP_BASE_SHA'] = $BaseSha
            $process = [Diagnostics.Process]::Start($start)
            try {
                $stdout = $process.StandardOutput.ReadToEnd()
                $stderr = $process.StandardError.ReadToEnd()
                $process.WaitForExit()
                [pscustomobject]@{
                    ExitCode = $process.ExitCode
                    Output = if (Test-Path $output) { Get-Content $output -Raw | ConvertFrom-StringData } else { @{} }
                    Diagnostics = "$stdout$stderr"
                }
            }
            finally { $process.Dispose() }
        }

        # Declarative wiring checks use job boundaries, as in the existing workflow tests.
        function Get-JobBlock([string] $Name) {
            $match = [regex]::Match($script:workflow, "(?ms)^  $([regex]::Escape($Name)):\r?\n.*?(?=^  [\w-]+:|\z)")
            if (-not $match.Success) { throw "Missing job: $Name" }
            $match.Value
        }

        function Invoke-ConfigGate([string] $Result) {
            $job = Get-JobBlock 'config-ci-gate'
            $body = [regex]::Match($job, '(?ms)^        run: \|\r?\n(?<body>(?:          .*\r?\n|\r?\n)+)').Groups['body'].Value
            if ([string]::IsNullOrWhiteSpace($body)) { throw 'Missing gate script' }
            $oldNeeds = $env:NEEDS_JSON
            try {
                $env:NEEDS_JSON = @{
                    'verify-security-metadata-tools' = @{ result = $Result }
                    'run-unit-tests' = @{ result = 'success' }
                } | ConvertTo-Json
                & ([scriptblock]::Create(($body -replace '(?m)^          ', '')))
            }
            finally { $env:NEEDS_JSON = $oldNeeds }
        }
    }

    It 'runs config checks for a PR changing only <Path>' -ForEach @(
        @{ Path = 'package-helpers.psm1' }
        @{ Path = 'build-config.ps1' }
        @{ Path = 'src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/CommandHost.cs' }
        @{ Path = 'src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit/Fixtures/OdsSecurityMetadata/representative.xml' }
        @{ Path = '.github/workflows/on-config-pullrequest.yml' }
        @{ Path = 'eng/verification/Test-SecurityMetadataToolsPackage.ps1' }
        @{ Path = 'eng/ci/detect-config-changes.sh' }
    ) {
        $result = Invoke-ConfigDetection -ChangedFile @($Path)
        $result.ExitCode | Should -Be 0 -Because $result.Diagnostics
        $result.Output.config_relevant | Should -BeExactly 'true'
        $result.Output.fresh_build_required | Should -BeExactly 'false'
    }

    It 'retains the unrelated PR skip for <Path>' -ForEach @(
        @{ Path = 'docs/README.md' }
        @{ Path = 'src/dms/core/EdFi.DataManagementService.Core/Example.cs' }
        @{ Path = '' }
    ) {
        $result = Invoke-ConfigDetection -ChangedFile @($Path)
        $result.ExitCode | Should -Be 0
        $result.Output.config_relevant | Should -BeExactly 'false'
        $result.Output.fresh_build_required | Should -BeExactly 'false'
    }

    It 'retains fresh image detection for <Path>' -ForEach @(
        @{ Path = 'src/Directory.Packages.props' }
        @{ Path = 'src/config/Dockerfile' }
        @{ Path = 'src/plugins/Directory.Build.props' }
        @{ Path = 'eng/docker-compose/local-config.yml' }
    ) {
        $result = Invoke-ConfigDetection -ChangedFile @('docs/README.md', $Path)
        $result.ExitCode | Should -Be 0
        $result.Output.config_relevant | Should -BeExactly 'true'
        $result.Output.fresh_build_required | Should -BeExactly 'true'
    }

    It 'runs all checks for <EventName>, preserving its fresh-build decision' -ForEach @(
        @{ EventName = 'merge_group'; Fresh = 'false' }
        @{ EventName = 'workflow_dispatch'; Fresh = 'true' }
        @{ EventName = 'unmodeled-event'; Fresh = 'true' }
    ) {
        $result = Invoke-ConfigDetection -ChangedFile @('docs/README.md') -EventName $EventName
        $result.ExitCode | Should -Be 0
        $result.Output.config_relevant | Should -BeExactly 'true'
        $result.Output.fresh_build_required | Should -BeExactly $Fresh
    }

    It 'falls back to full checks when the merge diff is unavailable (<Reason>)' -ForEach @(
        @{ Reason = 'missing base'; Base = ''; Fail = '' }
        @{ Reason = 'git diff failure'; Base = 'base-sha'; Fail = 'diff' }
    ) {
        $result = Invoke-ConfigDetection -EventName 'merge_group' -BaseSha $Base -FailCommand $Fail
        $result.ExitCode | Should -Be 0
        $result.Output.config_relevant | Should -BeExactly 'true'
        $result.Output.fresh_build_required | Should -BeExactly 'true'
    }

    It 'fails detection instead of skipping checks when PR git <Command> fails' -ForEach @(
        @{ Command = 'fetch' }
        @{ Command = 'diff' }
    ) {
        $result = Invoke-ConfigDetection -FailCommand $Command
        $result.ExitCode | Should -Not -Be 0
        $result.Output.Count | Should -Be 0
    }

    It 'uses the tested detector in the workflow' {
        $job = Get-JobBlock 'detect-fresh-build-changes'
        $job | Should -Match 'run: bash eng/ci/detect-config-changes\.sh'
        $job | Should -Match 'MERGE_GROUP_BASE_SHA: \$\{\{ github\.event\.merge_group\.base_sha \}\}'
    }

    It '<Job> consumes config relevance and is required by the gate' -ForEach @(
        @{ Job = 'verify-security-metadata-tools' }
        @{ Job = 'run-unit-tests' }
        @{ Job = 'run-e2e-tests' }
    ) {
        $block = Get-JobBlock $Job
        $block | Should -Match '(?m)^    needs: detect-fresh-build-changes$'
        $block | Should -Match "github.event_name != 'pull_request' \|\|\s+needs.detect-fresh-build-changes.outputs.config_relevant == 'true'"
        (Get-JobBlock 'config-ci-gate') | Should -Match "(?m)^      - $Job$"
    }

    It 'runs versioning and workflow tests plus the reusable local package smoke without publication' {
        $job = Get-JobBlock 'verify-security-metadata-tools'
        $job | Should -Match 'fetch-depth: 0'
        $job | Should -Match 'Get-VersionNumber -projectPrefix "cs"'
        $job | Should -Match 'eng/ci/tests/SecurityMetadataToolsVersioning.Tests.ps1'
        $job | Should -Match 'eng/ci/tests/ConfigPullRequestWorkflow.Tests.ps1'
        $job | Should -Match 'Test-SecurityMetadataToolsPackage.ps1 -AppVersion ''\$\{\{ steps.versions.outputs.dms-v \}\}'''
        $job | Should -Not -Match 'secrets\.|packages: write|id-token: write|\bPush\b|Invoke-Promote'
        (Get-JobBlock 'event_file') | Should -Match 'needs: \[.*verify-security-metadata-tools'
    }

    It 'keeps the upload scenario in the existing unfiltered CMS E2E lane' {
        $job = Get-JobBlock 'run-e2e-tests'
        $job | Should -Match './build-config.ps1 E2ETest '
        $job | Should -Not -Match '-E2ETestFilter'
        $feature = Get-Content (Join-Path $script:repoRoot 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/ClaimsManagement.feature') -Raw
        $feature | Should -Match '@SecurityMetadataTools\s+Scenario: 07 '
    }

    It 'runs only the bounded policy scenarios sequentially in <Job>' -ForEach @(
        @{ Job = 'run-e2e-tests'; Backend = 'postgresql'; Environment = './.env.config.e2e'; Expected = @('Hybrid/5.2', 'Hybrid/6.1', 'Filesystem/5.2', 'Upload/5.2', 'Drift/5.2') }
        @{ Job = 'run-e2e-tests-mssql'; Backend = 'mssql'; Environment = './.env.config.mssql.e2e'; Expected = @('Hybrid/5.2', 'Drift/5.2') }
    ) {
        $job = Get-JobBlock $Job
        $steps = @([regex]::Matches($job, '(?ms)^      - name: .*?(?=^      - name:|\z)') | ForEach-Object Value)
        $scenarios = @($steps | Where-Object { $_ -match 'run: >-\s+\./eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1' })
        $actual = @($scenarios | ForEach-Object {
            $_ | Should -Match "if: matrix.identityprovider == 'self-contained'"
            $_ | Should -Match '-SkipDockerBuild'
            $_ | Should -Match ('-EnvironmentFile ' + [regex]::Escape($Environment))
            $_ | Should -Not -Match 'continue-on-error|Start-Job|ForEach-Object.*-Parallel'
            $_ | Should -Match '-SecurityMetadataToolsScenario (\w+) -DataStandardVersion ([\d.]+)'
            $match = [regex]::Match($_, '-SecurityMetadataToolsScenario (\w+) -DataStandardVersion ([\d.]+)')
            "$($match.Groups[1].Value)/$($match.Groups[2].Value)"
        })
        $actual | Should -Be $Expected
        $tags = @($steps | Where-Object { $_ -match 'run: docker tag ed-fi-api-config-local:latest ed-fi-api-config-cms-e2e-isolated:latest' })
        $tags.Count | Should -Be 1
        $tags[0] | Should -Match "if: matrix.identityprovider == 'self-contained'"
        $job.IndexOf('./build-config.ps1 E2ETest') | Should -BeLessThan $job.IndexOf($tags[0])
        $job.IndexOf($tags[0]) | Should -BeLessThan $job.IndexOf($scenarios[0])
        $upload = @($steps | Where-Object { $_ -match '- name: Upload Test Results' })
        $upload.Count | Should -Be 1
        $upload[0] | Should -Match 'if: always\(\)'
        $upload[0] | Should -Match ('name: E2E Test Results - ' + $Backend + ' - \$\{\{ matrix.identityprovider \}\}')
        $upload[0] | Should -Match ('TestResults/cms-isolation/' + $Backend + '/\*\*')
        $upload[0] | Should -Match '\*\*/\*\.trx'
        $upload[0] | Should -Not -Match 'overwrite: true'
    }

    It 'uses the PostgreSQL job release version source only for installed Drift' {
        $job = Get-JobBlock 'run-e2e-tests'
        $job | Should -Match 'uses: actions/checkout@[^\r\n]+\s+with:\s+(?:#[^\r\n]+\s+)?fetch-depth: 0'
        $job | Should -Match 'id: versions\s+run: \|\s+Import-Module ./package-helpers.psm1\s+Get-VersionNumber -projectPrefix "cs"'
        $job | Should -Match '-SecurityMetadataToolsScenario Drift -DataStandardVersion 5.2\s+-UseInstalledTool -AppVersion ''\$\{\{ steps.versions.outputs.dms-v \}\}'''
        [regex]::Matches($job, '-UseInstalledTool').Count | Should -Be 1
        (Get-JobBlock 'run-e2e-tests-mssql') | Should -Not -Match '-UseInstalledTool|-AppVersion'
        (Get-JobBlock 'run-e2e-tests-mssql') | Should -Match '-E2ETestFilter "TestCategory=MssqlRepresentative"'
        (Get-JobBlock 'run-e2e-tests-mssql-multitenant') | Should -Not -Match 'Invoke-IsolatedConfigE2E'
    }

    It 'requires launcher Pester tests to execute and pass (<Outcome>)' -ForEach @(
        @{ Outcome = 'passed'; Reject = $false }
        @{ Outcome = 'missing'; Reject = $true }
        @{ Outcome = 'zero'; Reject = $true }
        @{ Outcome = 'failed'; Reject = $true }
        @{ Outcome = 'skipped'; Reject = $true }
        @{ Outcome = 'container failure'; Reject = $true }
        @{ Outcome = 'block failure'; Reject = $true }
    ) {
        $job = Get-JobBlock 'run-parity-pester-tests'
        $step = [regex]::Match($job, '(?ms)^      - name: Run Isolated Config E2E Launcher Tests\r?\n.*?(?=^      - name:|\z)').Value
        $step | Should -Not -BeNullOrEmpty
        $body = [regex]::Match($step, '(?ms)^        run: \|\r?\n(?<body>.*)').Groups['body'].Value -replace '(?m)^          ', ''
        $script:launcherPesterResult = if ($Outcome -eq 'missing') { $null } else {
            [pscustomobject]@{
                TotalCount = $(if ($Outcome -eq 'zero') { 0 } else { 1 })
                FailedContainersCount = [int]($Outcome -eq 'container failure')
                FailedBlocksCount = [int]($Outcome -eq 'block failure')
                Tests = @([pscustomobject]@{ Result = $(switch ($Outcome) { 'skipped' { 'Skipped' }; 'failed' { 'Failed' }; default { 'Passed' } }) })
            }
        }
        Mock Invoke-Pester { $script:launcherPesterResult }
        $invoke = { & ([scriptblock]::Create($body)) }
        if ($Reject) { $invoke | Should -Throw '*did not all execute and pass*' }
        else { $invoke | Should -Not -Throw }
        Should -Invoke Invoke-Pester -Times 1 -Exactly -ParameterFilter {
            $Path -eq 'eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.Tests.ps1' -and $PassThru
        }
    }

    It 'fails the actual aggregate gate when package validation is <Result>' -ForEach @(
        @{ Result = 'failure' }
        @{ Result = 'cancelled' }
    ) {
        { Invoke-ConfigGate $Result } | Should -Throw '*verify-security-metadata-tools*'
    }

    It 'accepts <Result> from the package job' -ForEach @(
        @{ Result = 'success' }
        @{ Result = 'skipped' }
    ) {
        { Invoke-ConfigGate $Result } | Should -Not -Throw
    }
}
