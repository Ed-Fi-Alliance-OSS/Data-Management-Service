#Requires -Version 7

Describe 'Scheduled pre-image workflow safeguards' {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot '../../../.github/workflows/scheduled-pre-image-test.yml')
        )
        $script:workflow = Get-Content -LiteralPath $script:workflowPath -Raw
        $script:lines = $script:workflow -split "\r?\n"

        function Get-JobBlock {
            param([Parameter(Mandatory)] [string] $Name)
            $jobMatches = @($script:lines | Select-String -Pattern "^  $([regex]::Escape($Name)):\s*$" -AllMatches)
            if ($jobMatches.Count -ne 1) { throw "Expected one job named $Name." }
            $start = $jobMatches[0].LineNumber - 1
            $end = $script:lines.Count
            for ($i = $start + 1; $i -lt $script:lines.Count; $i++) {
                if ($script:lines[$i] -match '^  [A-Za-z0-9_-]+:\s*$') { $end = $i; break }
            }
            return ($script:lines[$start..($end - 1)] -join "`n")
        }

        function Get-StepChunk {
            param([Parameter(Mandatory)] [string] $Name)
            $stepMatches = @([regex]::Matches($script:job, "(?m)^      - name: $([regex]::Escape($Name))\s*$"))
            if ($stepMatches.Count -ne 1) { throw "Expected one step named $Name." }
            $start = $stepMatches[0].Index
            $next = [regex]::Match($script:job.Substring($start + 1), '(?m)^      - name:')
            $end = if ($next.Success) { $start + 1 + $next.Index } else { $script:job.Length }
            return $script:job.Substring($start, $end - $start)
        }

        function Get-RunBlock {
            param([Parameter(Mandatory)] [string] $Name)
            $lines = (Get-StepChunk -Name $Name) -split "\r?\n"
            $start = [array]::IndexOf($lines, '        run: |')
            if ($start -lt 0) { throw "Step $Name has no block run command." }
            $body = [System.Collections.Generic.List[string]]::new()
            for ($i = $start + 1; $i -lt $lines.Count; $i++) {
                if ([string]::IsNullOrWhiteSpace($lines[$i])) { $body.Add('') }
                elseif ($lines[$i].StartsWith('          ')) { $body.Add($lines[$i].Substring(10)) }
                else { break }
            }
            return ($body -join "`n").TrimEnd()
        }

        function Invoke-CapturedRunBlock {
            param([Parameter(Mandatory)] [string] $RunBlock)
            # Collect incrementally so output emitted before a terminating error is retained.
            $output = [System.Collections.Generic.List[object]]::new()
            $failure = $null
            try { & ([scriptblock]::Create($RunBlock)) *>&1 | ForEach-Object { $output.Add($_) } }
            catch { $failure = $_ }
            return [pscustomobject]@{ Output = ($output | Out-String); Failure = $failure }
        }

        $script:job = Get-JobBlock -Name 'test'
    }

    Context 'matrix and publication boundaries' {
        It 'retains scheduled and manual triggers without reducing the matrix' {
            $script:workflow | Should -Match '(?m)^  workflow_dispatch:'
            $script:workflow | Should -Match '(?m)^  schedule:'
            $script:job.Substring(0, $script:job.IndexOf('    steps:')) | Should -Not -Match 'github.event_name|exclude:|include:'
        }

        It 'bounds executable content before subsequent step properties' {
            $oldJob = $script:job
            try {
                $script:job = "      - name: fixture`n        run: |`n          Write-Host 'safe'`n`n        env:`n          VALUE: ignored`n"
                Get-RunBlock -Name 'fixture' | Should -Be "Write-Host 'safe'"
            }
            finally { $script:job = $oldJob }
        }

        It 'uses both engines and preserves both identity providers' {
            $script:job | Should -Match 'database_engine: \[postgresql, mssql\]'
            $script:job | Should -Match 'identityprovider: \[keycloak, self-contained\]'
            $script:job | Should -Match 'fail-fast: false'
            $script:job | Should -Match 'matrix.database_engine.*matrix.identityprovider'
            $script:job | Should -Match 'logs/scheduled-pre-image-.*matrix.database_engine.*matrix.identityprovider'
            $script:job | Should -Match 'scheduled-pre-image-\$\{\{ matrix.database_engine \}\}-\$\{\{ matrix.identityprovider \}\}-logs'
            $script:job | Should -Match 'scheduled-pre-image-\$\{\{ matrix.database_engine \}\}-\$\{\{ matrix.identityprovider \}\}-results'
        }

        It 'publishes only sanitized diagnostic and TRX content' {
            $uploads = @(Get-StepChunk -Name 'Upload DMS E2E Logs'; Get-StepChunk -Name 'Upload DMS End to End Test Results')
            foreach ($upload in $uploads) { $upload | Should -Match "steps.sanitize.outcome == 'success'" }
            $capture = Get-StepChunk -Name 'Capture DMS E2E Docker Logs'
            $capture | Should -Match 'docker logs'
            $capture | Should -Match 'label=com.docker.compose.project=dms-published'
            $capture | Should -Not -Match 'Remove-Item'
            (Get-StepChunk -Name 'Fail if DMS E2E failed') | Should -Match "always\(\).*steps.e2e.outcome == 'failure'"
        }

        It 'registers this focused guard in the pull request Pester list' {
            $prWorkflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../../.github/workflows/on-dms-pullrequest.yml') -Raw
            $prWorkflow | Should -Match 'eng/ci/tests/ScheduledPreImageWorkflow.Tests.ps1'
        }
    }

    Context 'step runtime failure behavior' {
        It 'retains every console-visible PowerShell stream emitted before a terminating error' {
            $result = Invoke-CapturedRunBlock -RunBlock @'
Write-Output 'output-sentinel'
Write-Error 'error-sentinel' -ErrorAction Continue
Write-Warning 'warning-sentinel'
Write-Verbose 'verbose-sentinel' -Verbose
Write-Debug 'debug-sentinel' -Debug
Write-Information 'information-sentinel' -InformationAction Continue
Write-Host 'host-sentinel'
throw 'safe failure'
'@
            $result.Failure.Exception.Message | Should -Be 'safe failure'
            foreach ($stream in @('output', 'error', 'warning', 'verbose', 'debug', 'information', 'host')) {
                $result.Output | Should -Match "$stream-sentinel"
            }
        }

        It 'uses ordered always-run capture, teardown, and sanitizer stages' {
            $capture = Get-StepChunk -Name 'Capture DMS E2E Docker Logs'
            $teardown = Get-StepChunk -Name 'Teardown DMS E2E Environment'
            $sanitize = Get-StepChunk -Name 'Sanitize DMS E2E Diagnostic Artifacts'
            $capture | Should -Match 'if: always\(\)'
            $teardown | Should -Match 'if: always\(\)'
            $sanitize | Should -Match 'if: always\(\)'
            $script:job.IndexOf($capture) | Should -BeLessThan $script:job.IndexOf($teardown)
            $script:job.IndexOf($teardown) | Should -BeLessThan $script:job.IndexOf($sanitize)
            $teardown | Should -Match 'E2E_DATABASE_ENGINE'
            $teardown | Should -Match '\.env\.e2e'
        }

        It 'captures native failure without exposing child output and removes stale TRX before invoking the child' -ForEach @(
            @{ Engine = 'mssql'; Filter = 'Category=@MssqlRepresentative'; Identity = 'keycloak' }
            @{ Engine = 'postgresql'; Filter = '(Category!=@StandardVersion-6_1)&(Category!=@DocumentCacheHostedHappyPath)&(Category!=@CursorPartitionSizing)'; Identity = 'self-contained' }
        ) {
            $workingDirectory = Join-Path $TestDrive "e2e-child-$Engine"
            $diagnosticDirectory = Join-Path $workingDirectory 'diagnostics'
            $trxPath = Join-Path $workingDirectory 'TestResults/EdFi.DataManagementService.Tests.E2E.filtered.trx'
            New-Item -ItemType Directory -Path (Split-Path $trxPath) -Force | Out-Null
            New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
            Set-Content -LiteralPath $trxPath -Value 'stale-result'
            Set-Content -LiteralPath (Join-Path $workingDirectory 'build-dms.ps1') -Value @'
if (Test-Path -LiteralPath $env:E2E_TRX_PATH) { Set-Content -LiteralPath 'trx-existed-at-child-start.txt' -Value 'true' }
Set-Content -LiteralPath 'child-arguments.txt' -Value ($args -join '|')
[Console]::Error.WriteLine('Password=sentinel-secret')
exit 7
'@

            $oldLocation = Get-Location
            $oldEngine = $env:E2E_DATABASE_ENGINE
            $oldIdentity = $env:E2E_IDENTITY_PROVIDER
            $oldDirectory = $env:E2E_DIAGNOSTIC_DIRECTORY
            $oldTrxPath = $env:E2E_TRX_PATH
            try {
                Set-Location $workingDirectory
                $env:E2E_DATABASE_ENGINE = $Engine
                $env:E2E_IDENTITY_PROVIDER = $Identity
                $env:E2E_DIAGNOSTIC_DIRECTORY = $diagnosticDirectory
                $env:E2E_TRX_PATH = $trxPath
                $runBlock = Get-RunBlock -Name 'Run DMS End to End Tests'
                $result = Invoke-CapturedRunBlock -RunBlock $runBlock
                $result.Failure | Should -Not -BeNullOrEmpty
                $result.Failure.Exception.Message | Should -Match 'exit code 7'
                $result.Failure.Exception.Message | Should -Not -Match 'sentinel-secret'
                $result.Output | Should -Not -Match 'sentinel-secret'

                Test-Path -LiteralPath $trxPath | Should -BeFalse
                Test-Path -LiteralPath (Join-Path $workingDirectory 'trx-existed-at-child-start.txt') | Should -BeFalse
                $setupLog = Get-Content -LiteralPath (Join-Path $diagnosticDirectory 'build-dms-setup.log') -Raw
                $setupLog | Should -Match 'sentinel-secret'
                $setupLog | Should -Not -BeNullOrEmpty
                $arguments = Get-Content -LiteralPath (Join-Path $workingDirectory 'child-arguments.txt') -Raw
                $arguments | Should -Match 'UsePublishedImage'
                $arguments | Should -Match "DatabaseEngine\|$Engine"
                $arguments | Should -Match "IdentityProvider\|$Identity"
                $arguments | Should -Match 'EnvironmentFile\|\./\.env\.e2e'
                $arguments | Should -Match ([regex]::Escape("TestFilter|$Filter"))
            }
            finally {
                Set-Location $oldLocation
                foreach ($item in @(
                    @{ Name = 'E2E_DATABASE_ENGINE'; Value = $oldEngine },
                    @{ Name = 'E2E_IDENTITY_PROVIDER'; Value = $oldIdentity },
                    @{ Name = 'E2E_DIAGNOSTIC_DIRECTORY'; Value = $oldDirectory },
                    @{ Name = 'E2E_TRX_PATH'; Value = $oldTrxPath }
                )) {
                    if ($null -eq $item.Value) { Remove-Item "Env:$($item.Name)" -ErrorAction SilentlyContinue }
                    else { Set-Item "Env:$($item.Name)" $item.Value }
                }
            }
        }

        It 'captures Docker <Mode> failure privately and accepts empty enumeration' -ForEach @(
            @{ Mode = 'enumeration'; Message = 'enumeration failed'; Diagnostic = 'docker-ps.err' }
            @{ Mode = 'logs'; Message = 'log capture failed'; Diagnostic = 'dms-container.log' }
        ) {
            $workingDirectory = Join-Path $TestDrive 'docker-capture'
            $diagnosticDirectory = Join-Path $workingDirectory 'diagnostics'
            New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
            $oldLocation = Get-Location
            $oldDirectory = $env:E2E_DIAGNOSTIC_DIRECTORY
            $script:dockerMode = $Mode
            function docker {
                if (($script:dockerMode -eq 'enumeration' -and $args[0] -eq 'ps') -or
                    ($script:dockerMode -eq 'logs' -and $args[0] -eq 'logs')) {
                    Write-Error -Message 'Password=sentinel-secret' -ErrorAction Continue
                    $global:LASTEXITCODE = 7
                    return
                }
                if ($script:dockerMode -eq 'logs' -and $args[0] -eq 'ps') { 'dms-container' }
                $global:LASTEXITCODE = 0
            }
            try {
                Set-Location $workingDirectory
                $env:E2E_DIAGNOSTIC_DIRECTORY = $diagnosticDirectory
                New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $diagnosticDirectory 'build-dms-setup.log') -Value 'setup-output-sentinel'
                $runBlock = Get-RunBlock -Name 'Capture DMS E2E Docker Logs'
                $result = Invoke-CapturedRunBlock -RunBlock $runBlock
                $result.Failure | Should -Not -BeNullOrEmpty
                $result.Failure.Exception.Message | Should -Match $Message
                $result.Failure.Exception.Message | Should -Not -Match 'sentinel-secret'
                $result.Output | Should -Not -Match 'sentinel-secret'
                (Get-Content -LiteralPath (Join-Path $diagnosticDirectory $Diagnostic) -Raw) | Should -Match 'sentinel-secret'

                $script:dockerMode = 'empty'
                $result = Invoke-CapturedRunBlock -RunBlock $runBlock
                $result.Failure | Should -BeNullOrEmpty
                $result.Output | Should -Not -Match 'sentinel-secret'
                Test-Path -LiteralPath (Join-Path $diagnosticDirectory 'docker-ps.err') | Should -BeTrue
                (Get-Content -LiteralPath (Join-Path $diagnosticDirectory 'build-dms-setup.log') -Raw).Trim() | Should -Be 'setup-output-sentinel'
            }
            finally {
                Set-Location $oldLocation
                if ($null -eq $oldDirectory) { Remove-Item Env:E2E_DIAGNOSTIC_DIRECTORY -ErrorAction SilentlyContinue }
                else { $env:E2E_DIAGNOSTIC_DIRECTORY = $oldDirectory }
                Remove-Item Function:\docker -ErrorAction SilentlyContinue
                Remove-Variable dockerMode -Scope Script -ErrorAction SilentlyContinue
            }
        }

        It 'captures and propagates teardown failure without printing its diagnostic' {
            $workingDirectory = Join-Path $TestDrive 'teardown-failure'
            $diagnosticDirectory = Join-Path $workingDirectory 'diagnostics'
            New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
            $oldLocation = Get-Location
            $oldEngine = $env:E2E_DATABASE_ENGINE
            $oldDirectory = $env:E2E_DIAGNOSTIC_DIRECTORY
            function pwsh { Write-Error -Message 'Password=sentinel-secret' -ErrorAction Continue; $global:LASTEXITCODE = 9 }
            try {
                Set-Location $workingDirectory
                $env:E2E_DATABASE_ENGINE = 'mssql'
                $env:E2E_DIAGNOSTIC_DIRECTORY = $diagnosticDirectory
                $runBlock = Get-RunBlock -Name 'Teardown DMS E2E Environment'
                $result = Invoke-CapturedRunBlock -RunBlock $runBlock
                $result.Failure | Should -Not -BeNullOrEmpty
                $result.Failure.Exception.Message | Should -Match 'exit code 9'
                $result.Failure.Exception.Message | Should -Not -Match 'sentinel-secret'
                $result.Output | Should -Not -Match 'sentinel-secret'
                $teardownLog = Get-Content -LiteralPath (Join-Path $diagnosticDirectory 'teardown.log') -Raw
                $teardownLog | Should -Match 'sentinel-secret'
            }
            finally {
                Set-Location $oldLocation
                Remove-Item Function:\pwsh -ErrorAction SilentlyContinue
                if ($null -eq $oldEngine) { Remove-Item Env:E2E_DATABASE_ENGINE -ErrorAction SilentlyContinue }
                else { $env:E2E_DATABASE_ENGINE = $oldEngine }
                if ($null -eq $oldDirectory) { Remove-Item Env:E2E_DIAGNOSTIC_DIRECTORY -ErrorAction SilentlyContinue }
                else { $env:E2E_DIAGNOSTIC_DIRECTORY = $oldDirectory }
            }
        }

        It 'propagates sanitizer failure without echoing raw exception text' {
            $workingDirectory = Join-Path $TestDrive 'sanitizer-failure'
            New-Item -ItemType Directory -Path (Join-Path $workingDirectory 'eng/ci') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $workingDirectory 'eng/ci/sanitize-e2e-artifacts.ps1') -Value "throw 'Password=sentinel-secret'"
            $oldLocation = Get-Location
            $oldDirectory = $env:E2E_DIAGNOSTIC_DIRECTORY
            $oldTrxPath = $env:E2E_TRX_PATH
            try {
                Set-Location $workingDirectory
                $env:E2E_DIAGNOSTIC_DIRECTORY = Join-Path $workingDirectory 'diagnostics'
                $env:E2E_TRX_PATH = Join-Path $workingDirectory 'missing.trx'
                $runBlock = Get-RunBlock -Name 'Sanitize DMS E2E Diagnostic Artifacts'
                $result = Invoke-CapturedRunBlock -RunBlock $runBlock
                $result.Failure | Should -Not -BeNullOrEmpty
                $result.Failure.Exception.Message | Should -Match 'sanitization failed'
                $result.Failure.Exception.Message | Should -Not -Match 'sentinel-secret'
                $result.Output | Should -Not -Match 'sentinel-secret'
            }
            finally {
                Set-Location $oldLocation
                if ($null -eq $oldDirectory) { Remove-Item Env:E2E_DIAGNOSTIC_DIRECTORY -ErrorAction SilentlyContinue }
                else { $env:E2E_DIAGNOSTIC_DIRECTORY = $oldDirectory }
                if ($null -eq $oldTrxPath) { Remove-Item Env:E2E_TRX_PATH -ErrorAction SilentlyContinue }
                else { $env:E2E_TRX_PATH = $oldTrxPath }
            }
        }
    }

    Context 'positive TRX execution evidence' {
        It 'rejects missing, malformed, empty, and non-executed TRX evidence safely' -ForEach @(
            @{ Case = 'missing file'; Xml = $null }
            @{ Case = 'empty file'; Xml = '' }
            @{ Case = 'malformed secret'; Xml = '<TestRun Password="sentinel-secret">' }
            @{ Case = 'wrong root'; Xml = '<Other><ResultSummary><Counters executed="1"/></ResultSummary><Results><UnitTestResult outcome="Passed"/></Results></Other>' }
            @{ Case = 'missing counters'; Xml = '<TestRun><ResultSummary/><Results><UnitTestResult outcome="Passed"/></Results></TestRun>' }
            @{ Case = 'missing executed'; Xml = '<TestRun><ResultSummary><Counters/></ResultSummary><Results><UnitTestResult outcome="Passed"/></Results></TestRun>' }
            @{ Case = 'negative executed'; Xml = '<TestRun><ResultSummary><Counters executed="-1"/></ResultSummary><Results><UnitTestResult outcome="Passed"/></Results></TestRun>' }
            @{ Case = 'fractional executed'; Xml = '<TestRun><ResultSummary><Counters executed="1.5"/></ResultSummary><Results><UnitTestResult outcome="Passed"/></Results></TestRun>' }
            @{ Case = 'nonnumeric executed'; Xml = '<TestRun><ResultSummary><Counters executed="one"/></ResultSummary><Results><UnitTestResult outcome="Passed"/></Results></TestRun>' }
            @{ Case = 'zero executed'; Xml = '<TestRun><ResultSummary><Counters executed="0"/></ResultSummary><Results><UnitTestResult outcome="NotExecuted"/></Results></TestRun>' }
            @{ Case = 'positive count without result records'; Xml = '<TestRun><ResultSummary><Counters executed="1"/></ResultSummary><Results/></TestRun>' }
            @{ Case = 'positive count without a passed record'; Xml = '<TestRun><ResultSummary><Counters executed="1"/></ResultSummary><Results><UnitTestResult outcome="Failed"/></Results></TestRun>' }
        ) {
            $path = Join-Path $TestDrive 'invalid.trx'
            if ($null -eq $Xml) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
            else { Set-Content -LiteralPath $path -Value $Xml }
            $oldPath = $env:E2E_TRX_PATH
            try {
                $env:E2E_TRX_PATH = $path
                $runBlock = Get-RunBlock -Name 'Verify DMS E2E Execution'
                $result = Invoke-CapturedRunBlock -RunBlock $runBlock
                $result.Failure | Should -Not -BeNullOrEmpty -Because $Case
                $result.Failure.Exception.Message | Should -Match 'valid executed test result'
                $result.Failure.Exception.Message | Should -Not -Match 'sentinel-secret'
                $result.Output | Should -Not -Match 'sentinel-secret'
            }
            finally {
                if ($null -eq $oldPath) { Remove-Item Env:E2E_TRX_PATH -ErrorAction SilentlyContinue }
                else { $env:E2E_TRX_PATH = $oldPath }
            }
        }

        It 'accepts namespaced and unnamespaced execution with an ordinary skipped scenario' -ForEach @(
            @{ Namespace = '' }
            @{ Namespace = ' xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"' }
        ) {
            $path = Join-Path $TestDrive 'passed.trx'
            $xml = "<TestRun$Namespace><Results><UnitTestResult testName=`"passed`" outcome=`"Passed`"/><UnitTestResult testName=`"skipped`" outcome=`"NotExecuted`"/></Results><ResultSummary><Counters executed=`"1`"/></ResultSummary></TestRun>"
            Set-Content -LiteralPath $path -Value $xml
            $oldPath = $env:E2E_TRX_PATH
            try {
                $env:E2E_TRX_PATH = $path
                $runBlock = Get-RunBlock -Name 'Verify DMS E2E Execution'
                { & ([scriptblock]::Create($runBlock)) } | Should -Not -Throw
            }
            finally {
                if ($null -eq $oldPath) { Remove-Item Env:E2E_TRX_PATH -ErrorAction SilentlyContinue }
                else { $env:E2E_TRX_PATH = $oldPath }
            }
        }

        It 'sanitizes XML-escaped connection-string credentials before accepting E2E evidence' {
            $path = Join-Path $TestDrive 'escaped-password.trx'
            Set-Content -LiteralPath $path -Value @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="passed" outcome="Passed"><Output><StdOut>Server=dms;Password=prefix&amp;SECRET_SUFFIX;Database=d</StdOut></Output></UnitTestResult></Results><ResultSummary><Counters executed="1"/></ResultSummary></TestRun>
'@
            $oldPath = $env:E2E_TRX_PATH
            try {
                & (Join-Path $PSScriptRoot '../sanitize-e2e-artifacts.ps1') -Path $path
                $sanitized = [xml](Get-Content -LiteralPath $path -Raw)
                $sanitized.TestRun.Results.UnitTestResult.Output.StdOut | Should -Be 'Server=dms;Password=***REDACTED***;Database=d'
                $env:E2E_TRX_PATH = $path
                $runBlock = Get-RunBlock -Name 'Verify DMS E2E Execution'
                { & ([scriptblock]::Create($runBlock)) } | Should -Not -Throw
            }
            finally {
                if ($null -eq $oldPath) { Remove-Item Env:E2E_TRX_PATH -ErrorAction SilentlyContinue }
                else { $env:E2E_TRX_PATH = $oldPath }
            }
        }

        It 'requires successful E2E and sanitization before evidence validation' {
            $evidence = Get-StepChunk -Name 'Verify DMS E2E Execution'
            $evidence | Should -Match 'always\(\)'
            $evidence | Should -Match "steps.e2e.outcome == 'success'"
            $evidence | Should -Match "steps.sanitize.outcome == 'success'"
        }
    }

    Context 'leg summaries and scheduled notifications' {
        It 'reports required stages for each engine and only calls a complete leg passed' -ForEach @(
            @{ Case = 'all stages pass'; Job = 'success'; Build = 'success'; Test = 'success'; Capture = 'success'; Teardown = 'success'; Sanitize = 'success'; Evidence = 'success'; Logs = 'success'; Results = 'success'; Passed = $true }
            @{ Case = 'build failure and test skipped'; Job = 'failure'; Build = 'failure'; Test = 'skipped'; Capture = 'success'; Teardown = 'success'; Sanitize = 'success'; Evidence = 'skipped'; Logs = 'success'; Results = 'success'; Passed = $false }
            @{ Case = 'test failure and evidence skipped'; Job = 'failure'; Build = 'success'; Test = 'failure'; Capture = 'success'; Teardown = 'success'; Sanitize = 'success'; Evidence = 'skipped'; Logs = 'success'; Results = 'success'; Passed = $false }
            @{ Case = 'teardown failure'; Job = 'failure'; Build = 'success'; Test = 'success'; Capture = 'success'; Teardown = 'failure'; Sanitize = 'success'; Evidence = 'success'; Logs = 'success'; Results = 'success'; Passed = $false }
            @{ Case = 'sanitizer failure'; Job = 'failure'; Build = 'success'; Test = 'success'; Capture = 'success'; Teardown = 'success'; Sanitize = 'failure'; Evidence = 'skipped'; Logs = 'skipped'; Results = 'skipped'; Passed = $false }
            @{ Case = 'evidence failure'; Job = 'failure'; Build = 'success'; Test = 'success'; Capture = 'success'; Teardown = 'success'; Sanitize = 'success'; Evidence = 'failure'; Logs = 'success'; Results = 'success'; Passed = $false }
        ) {
            $summaryPath = Join-Path $TestDrive "summary-$($Case -replace '\W', '-')"
            $oldEnvironment = @{}
            foreach ($name in @('E2E_JOB_STATUS', 'E2E_BUILD_OUTCOME', 'E2E_TEST_OUTCOME', 'E2E_CAPTURE_OUTCOME', 'E2E_TEARDOWN_OUTCOME', 'E2E_SANITIZE_OUTCOME', 'E2E_EVIDENCE_OUTCOME', 'E2E_LOG_UPLOAD_OUTCOME', 'E2E_RESULT_UPLOAD_OUTCOME', 'E2E_DATABASE_ENGINE', 'E2E_IDENTITY_PROVIDER', 'GITHUB_STEP_SUMMARY', 'GITHUB_SERVER_URL', 'GITHUB_REPOSITORY', 'GITHUB_RUN_ID')) {
                $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            }
            try {
                $env:E2E_JOB_STATUS = $Job
                $env:E2E_BUILD_OUTCOME = $Build
                $env:E2E_TEST_OUTCOME = $Test
                $env:E2E_CAPTURE_OUTCOME = $Capture
                $env:E2E_TEARDOWN_OUTCOME = $Teardown
                $env:E2E_SANITIZE_OUTCOME = $Sanitize
                $env:E2E_EVIDENCE_OUTCOME = $Evidence
                $env:E2E_LOG_UPLOAD_OUTCOME = $Logs
                $env:E2E_RESULT_UPLOAD_OUTCOME = $Results
                $env:E2E_DATABASE_ENGINE = 'mssql'
                $env:E2E_IDENTITY_PROVIDER = 'self-contained'
                $env:GITHUB_STEP_SUMMARY = $summaryPath
                $env:GITHUB_SERVER_URL = 'https://github.com'
                $env:GITHUB_REPOSITORY = 'Ed-Fi-Alliance-OSS/Data-Management-Service'
                $env:GITHUB_RUN_ID = '12345'
                $runBlock = Get-RunBlock -Name 'Summarize DMS E2E Results'
                & ([scriptblock]::Create($runBlock))
                $text = Get-Content -LiteralPath $summaryPath -Raw
                $text | Should -Match 'SQL Server \(mssql\)'
                $text | Should -Match 'self-contained'
                $text | Should -Match 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/12345'
                $text | Should -Match "Build.*$Build"
                $text | Should -Match "Test.*$Test"
                $text | Should -Match "Sanitize.*$Sanitize"
                if ($Passed) { $text | Should -Match 'Result.*passed' }
                else { $text | Should -Not -Match 'Result.*passed' }
                $text | Should -Not -Match 'sentinel-secret|Password=|Authorization:'
            }
            finally {
                foreach ($name in $oldEnvironment.Keys) {
                    if ($null -eq $oldEnvironment[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
                    else { Set-Item "Env:$name" $oldEnvironment[$name] }
                }
            }
        }

        It 'places an always-run metadata summary after uploads and the final gate' {
            $summary = Get-StepChunk -Name 'Summarize DMS E2E Results'
            $summary | Should -Match 'if: always\(\)'
            $summary | Should -Not -Match 'steps.sanitize.outcome == .success.'
            $summaryIndex = $script:job.IndexOf($summary)
            $summaryIndex | Should -BeGreaterThan $script:job.IndexOf((Get-StepChunk -Name 'Upload DMS E2E Logs'))
            $summaryIndex | Should -BeGreaterThan $script:job.IndexOf((Get-StepChunk -Name 'Upload DMS End to End Test Results'))
            $summaryIndex | Should -BeGreaterThan $script:job.IndexOf((Get-StepChunk -Name 'Fail if DMS E2E failed'))
        }

        It 'keeps Slack results per leg, scheduled-only, and free of commit text' {
            foreach ($name in @('Notify Slack on success', 'Notify Slack on failure')) {
                $step = Get-StepChunk -Name $name
                $step | Should -Match "github.event_name != 'workflow_dispatch'"
                $step | Should -Match 'matrix.database_engine'
                $step | Should -Match 'matrix.identityprovider'
                $step | Should -Match 'github.server_url.*/actions/runs/.+github.run_id'
                $step | Should -Not -Match 'head_commit.message|both engines|combined result'
                $payloadMatch = [regex]::Match($step, '(?ms)^\s+payload:\s*\|\r?\n(?<body>.*?)(?=^\s+webhook:)')
                $payloadMatch.Success | Should -BeTrue
                $payload = ($payloadMatch.Groups['body'].Value -split "\r?\n" | ForEach-Object { $_ -replace '^ {12}', '' }) -join "`n"
                $payload = [regex]::Replace($payload, '\$\{\{\s*(.*?)\s*\}\}', {
                    param($match)
                    switch -Regex ($match.Groups[1].Value.Trim()) {
                        'matrix.database_engine' { 'mssql'; break }
                        'matrix.identityprovider' { 'self-contained'; break }
                        'github.server_url' { 'https://github.com'; break }
                        'github.repository' { 'Ed-Fi-Alliance-OSS/Data-Management-Service'; break }
                        'github.run_id' { '12345'; break }
                        default { 'safe' }
                    }
                })
                { $payload | ConvertFrom-Json -ErrorAction Stop } | Should -Not -Throw
                $message = $payload | ConvertFrom-Json
                $message.text | Should -Match 'mssql'
                $message.text | Should -Match 'self-contained'
                $message.text | Should -Match 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/12345'
                $message.text | Should -Not -Match 'both engines'
            }
            (Get-StepChunk -Name 'Notify Slack on success') | Should -Match "steps.e2e.outcome == 'success'.*steps.evidence.outcome == 'success'"
            (Get-StepChunk -Name 'Notify Slack on failure') | Should -Match 'failure\(\)'
        }
    }
}
