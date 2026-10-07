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
            $matches = @($script:lines | Select-String -Pattern "^  $([regex]::Escape($Name)):\s*$" -AllMatches)
            if ($matches.Count -ne 1) { throw "Expected one job named $Name." }
            $start = $matches[0].LineNumber - 1
            $end = $script:lines.Count
            for ($i = $start + 1; $i -lt $script:lines.Count; $i++) {
                if ($script:lines[$i] -match '^  [A-Za-z0-9_-]+:\s*$') { $end = $i; break }
            }
            return ($script:lines[$start..($end - 1)] -join "`n")
        }

        function Get-StepChunk {
            param([Parameter(Mandatory)] [string] $Name)
            $matches = @([regex]::Matches($script:job, "(?m)^      - name: $([regex]::Escape($Name))\s*$"))
            if ($matches.Count -ne 1) { throw "Expected one step named $Name." }
            $start = $matches[0].Index
            $next = [regex]::Match($script:job.Substring($start + 1), '(?m)^      - name:')
            $end = if ($next.Success) { $start + 1 + $next.Index } else { $script:job.Length }
            return $script:job.Substring($start, $end - $start)
        }

        function Get-RunBlock {
            param([Parameter(Mandatory)] [string] $Name)
            $step = Get-StepChunk -Name $Name
            $match = [regex]::Match($step, '(?ms)^        run: \|\r?\n(?<body>(?:^          .*\r?\n|^\r?\n)*)')
            if (-not $match.Success) { throw "Step $Name has no block run command." }
            return (($match.Groups['body'].Value -split "\r?\n" | ForEach-Object { if ($_.Length -ge 10) { $_.Substring(10) } else { '' } }) -join "`n")
        }

        $script:job = Get-JobBlock -Name 'test'
    }

    Context 'matrix and publication boundaries' {
        It 'uses both engines and preserves both identity providers' {
            $script:job | Should -Match 'database_engine: \[postgresql, mssql\]'
            $script:job | Should -Match 'identityprovider: \[keycloak, self-contained\]'
            $script:job | Should -Match 'fail-fast: false'
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
                { & ([scriptblock]::Create($runBlock)) } | Should -Throw '*exit code 7*'

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

        It 'distinguishes failed Docker enumeration from empty enumeration' {
            $workingDirectory = Join-Path $TestDrive 'docker-capture'
            $diagnosticDirectory = Join-Path $workingDirectory 'diagnostics'
            New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
            $oldLocation = Get-Location
            $oldDirectory = $env:E2E_DIAGNOSTIC_DIRECTORY
            $script:dockerMode = 'failure'
            function docker {
                if ($script:dockerMode -eq 'failure') {
                    Write-Error -Message 'Password=sentinel-secret' -ErrorAction Continue
                    $global:LASTEXITCODE = 7
                    return
                }
                $global:LASTEXITCODE = 0
            }
            try {
                Set-Location $workingDirectory
                $env:E2E_DIAGNOSTIC_DIRECTORY = $diagnosticDirectory
                New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $diagnosticDirectory 'build-dms-setup.log') -Value 'setup-output-sentinel'
                $runBlock = Get-RunBlock -Name 'Capture DMS E2E Docker Logs'
                { & ([scriptblock]::Create($runBlock)) } | Should -Throw '*enumeration failed*'
                (Get-Content -LiteralPath (Join-Path $diagnosticDirectory 'docker-ps.err') -Raw) | Should -Match 'sentinel-secret'

                $script:dockerMode = 'empty'
                { & ([scriptblock]::Create($runBlock)); 'resumed' } | Should -Not -Throw
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
                { & ([scriptblock]::Create($runBlock)) } | Should -Throw '*exit code 9*'
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
                $message = $null
                try { & ([scriptblock]::Create($runBlock)) }
                catch { $message = $_.Exception.Message }
                $message | Should -Match 'sanitization failed'
                $message | Should -Not -Match 'sentinel-secret'
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
}
