# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
param()

Describe "DMS-1153 bootstrap entry-point and IDE workflow" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"

        function script:Get-DeclaredScriptParameters {
            param(
                [string]$Path
            )

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) {
                throw "Failed to parse $Path"
            }

            return @(
                $ast.ParamBlock.Parameters |
                    ForEach-Object { $_.Name.VariablePath.UserPath } |
                    Select-Object -Unique
            )
        }

        function script:New-TestDirectory {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1153-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function script:Copy-DockerComposeFile {
            param(
                [string]$FileName,
                [string]$Destination
            )
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $FileName) -Destination $Destination
        }

        function script:New-IsolatedBootstrapRepo {
            $repoRoot = New-TestDirectory
            $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
            $engRoot = Join-Path $repoRoot "eng"
            New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null
            New-Item -ItemType Directory -Path $engRoot -Force | Out-Null

            foreach ($fileName in @(
                "bootstrap-wrapper.psm1",
                "bootstrap-local-dms.ps1",
                "bootstrap-schema-catalog.psm1",
                # The wrapper always composes the local-bootstrap data-standard overlay
                # (default 5.2) onto the base env via env-utility, so every wrapper
                # invocation needs the utility module and the overlay files.
                "env-utility.psm1",
                ".env.bootstrap.ds52",
                ".env.bootstrap.ds61"
            )) {
                Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
            }
            Copy-Item `
                -LiteralPath (Join-Path $script:sourceRepoRoot "eng/schema-package-utility.psm1") `
                -Destination (Join-Path $engRoot "schema-package-utility.psm1")

            $envFile = Join-Path $dockerComposeRoot ".env.example"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            return [pscustomobject]@{
                RepoRoot         = $repoRoot
                DockerComposeRoot = $dockerComposeRoot
                BootstrapRoot    = Join-Path $dockerComposeRoot ".bootstrap"
                EnvFile          = $envFile
                WrapperScript    = Join-Path $dockerComposeRoot "bootstrap-local-dms.ps1"
            }
        }

        function script:New-BootstrapManifestFile {
            param(
                [Parameter(Mandatory)]
                [string]$DockerComposeRoot
            )

            $bootstrapRoot = Join-Path $DockerComposeRoot ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null

            $manifest = [ordered]@{
                version = 1
                schema  = [ordered]@{
                    selectionMode       = "ApiSchemaPath"
                    selectedExtensions  = @()
                    effectiveSchemaHash = "abc123"
                    workspaceFingerprint = "0000000000000000000000000000000000000000000000000000000000000000"
                    apiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                }
                claims  = [ordered]@{
                    mode                      = "Embedded"
                    directory                 = "claims"
                    fingerprint               = "def456"
                    expectedVerificationChecks = @()
                }
                seed    = [ordered]@{
                    extensionNamespacePrefixes = @()
                }
            }
            $manifestPath = Join-Path $bootstrapRoot "bootstrap-manifest.json"
            $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            return $manifestPath
        }

        # ---------------------------------------------------------------------------
        # Call-recording stub factory: writes one line per phase invocation with all
        # bound parameters in a machine-readable key=value format.
        # ---------------------------------------------------------------------------
        function script:New-RecordingStartScript {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath
            )

            $scriptPath = Join-Path $Directory "start-local-dms.ps1"
            @"
param(
    [switch] `$InfraOnly,
    [switch] `$DmsOnly,
    [switch] `$EnableConfig,
    [string] `$EnvironmentFile,
    [string] `$IdentityProvider,
    [string] `$DmsBaseUrl,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
`$hasDmsBaseUrl = `$PSBoundParameters.ContainsKey('DmsBaseUrl') -and -not [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)
`$label = if (`$InfraOnly -and `$hasDmsBaseUrl) { "start-infra-healthwait" }
          elseif (`$InfraOnly) { "start-infra" }
          elseif (`$DmsOnly)  { "start-dms" }
          else                { "start-legacy" }
Add-Content -LiteralPath '$CallLogPath' -Value "`$label DmsBaseUrl=`$DmsBaseUrl"
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:New-RecordingConfigureScript {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath,

                [long[]]$DataStoreIds = @(42)
            )

            $idsJson = ($DataStoreIds | ForEach-Object { "[long]$_" }) -join ', '
            $scriptPath = Join-Path $Directory "configure-local-data-store.ps1"
            @"
param(
    [string] `$EnvironmentFile,
    [switch] `$NoDataStore,
    [switch] `$AddSmokeTestCredentials,
    [string] `$SchoolYearRange,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$CallLogPath' -Value "configure smoke=`$AddSmokeTestCredentials"
[pscustomobject]@{
    DataStoreIds              = [long[]] @($idsJson)
    SelectedDataStoreIds      = [long[]] @($idsJson)
    RouteContexts             = @()
    Tenant                    = ''
    SchoolYears               = [int[]] @()
    HasRouteQualifiedDataStores = `$false
}
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:New-RecordingProvisionScript {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath
            )

            $scriptPath = Join-Path $Directory "provision-dms-schema.ps1"
            @"
param(
    [string] `$EnvironmentFile,
    [long[]] `$DataStoreId,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$CallLogPath' -Value "provision"
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:New-RecordingSeedScript {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath
            )

            $scriptPath = Join-Path $Directory "load-dms-seed-data.ps1"
            @"
param(
    [string] `$EnvironmentFile,
    [string] `$DmsBaseUrl,
    [string] `$IdentityProvider,
    [long[]] `$DataStoreId,
    [int[]]  `$SchoolYear,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$CallLogPath' -Value "seed DmsBaseUrl=`$DmsBaseUrl"
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:New-SchemaOnlyBootstrapManifestFile {
            # A manifest written by prepare-dms-schema.ps1 alone: schema section present, no
            # claims/seed. Models the incomplete state the wrapper must complete before infrastructure.
            param(
                [Parameter(Mandatory)]
                [string]$DockerComposeRoot
            )

            $bootstrapRoot = Join-Path $DockerComposeRoot ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null

            $overlayContent = Get-Content -LiteralPath (Join-Path $DockerComposeRoot ".env.bootstrap.ds52") -Raw
            $packagesJson = [regex]::Match(
                $overlayContent,
                "(?ms)^[ \t]*SCHEMA_PACKAGES='(?<value>\[.*?\])'"
            ).Groups["value"].Value
            $selectedPackages = @(
                ($packagesJson | ConvertFrom-Json) |
                    ForEach-Object { "$($_.name)@$($_.version)" }
            )

            $manifest = [ordered]@{
                version = 1
                schema  = [ordered]@{
                    selectionMode        = "Standard"
                    selectedExtensions   = @("tpdm")
                    selectedPackages     = $selectedPackages
                    effectiveSchemaHash  = "abc123"
                    workspaceFingerprint = "0000000000000000000000000000000000000000000000000000000000000000"
                    apiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                }
            }
            $manifestPath = Join-Path $bootstrapRoot "bootstrap-manifest.json"
            $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            return $manifestPath
        }

        function script:New-RecordingPrepareScripts {
            # Call-recording stubs for the two staging phase scripts: each appends a single line so
            # tests can assert which staging phases ran and in what order relative to the start phase.
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath
            )

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$CallLogPath' -Value "prepare-schema"
"@ | Set-Content -LiteralPath (Join-Path $Directory "prepare-dms-schema.ps1") -Encoding utf8

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$CallLogPath' -Value "prepare-claims"
"@ | Set-Content -LiteralPath (Join-Path $Directory "prepare-dms-claims.ps1") -Encoding utf8
        }

        function script:New-RecordingTeardownStartScript {
            # Call-recording start-local-dms.ps1 stub for teardown delegation tests. Captures the
            # teardown-relevant switches/values as an explicit key=value line so a test can tell
            # RemoveBootstrap=True apart from RemoveBootstrap=False (switch defaults to False when
            # the caller omits it). Anything outside the forwarded whitelist lands in the trailing
            # Rest= field via ValueFromRemainingArguments, so a test can assert excluded options
            # (seed, configure, IDE, -DataStandardVersion) never reach teardown. FailureMessage
            # models a failed teardown (the real start scripts throw when compose down fails) so
            # the entry script's error propagation can be exercised.
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath,

                [string]$FailureMessage
            )

            $failureStatement = if ($FailureMessage) { "throw '$FailureMessage'" } else { "" }
            $scriptPath = Join-Path $Directory "start-local-dms.ps1"
            @"
param(
    [switch] `$d,
    [switch] `$v,
    [switch] `$RemoveBootstrap,
    [string] `$EnvironmentFile,
    [string] `$IdentityProvider,
    [switch] `$EnableKafkaUI,
    [switch] `$EnableSwaggerUI,
    [string] `$DatabaseEngine,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$CallLogPath' -Value "teardown d=`$d v=`$v RemoveBootstrap=`$RemoveBootstrap EnvironmentFile=`$EnvironmentFile IdentityProvider=`$IdentityProvider EnableKafkaUI=`$EnableKafkaUI EnableSwaggerUI=`$EnableSwaggerUI DatabaseEngine=`$DatabaseEngine Rest=`$Rest"
$failureStatement
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:Invoke-TeardownDelegation {
            # Drives the wrapper teardown path with every phase stub recording, asserts the run
            # short-circuited to exactly one start-local-dms.ps1 delegation (no staging, configure,
            # provision, or seed phase), and returns that single delegation log line so the caller can
            # make the flag-specific assertions (v, RemoveBootstrap, forwarded compose-shape options).
            param(
                [Parameter(Mandatory)]
                [string]$CallLogName,

                [switch]$v
            )

            $callLog = Join-Path $script:repo.RepoRoot $CallLogName
            New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog
            New-RecordingTeardownStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            if ($v) {
                & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -d -v
            }
            else {
                & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -d
            }

            $log = @(Get-Content -LiteralPath $callLog)

            $log.Count | Should -Be 1 -Because "teardown must short-circuit to a single start-local-dms.ps1 delegation"
            $log[0] | Should -Match "^teardown "
            $log | Where-Object { $_ -like "prepare-*" } | Should -BeNullOrEmpty
            $log | Where-Object { $_ -like "configure*" } | Should -BeNullOrEmpty
            $log | Where-Object { $_ -like "provision*" } | Should -BeNullOrEmpty
            $log | Where-Object { $_ -like "seed*" }      | Should -BeNullOrEmpty

            return $log[0]
        }

        function script:Invoke-RealStartScriptTeardown {
            # Runs a REAL start script's teardown branch (`-d -v -RemoveBootstrap`, or plain `-d`
            # with -StopOnly) with `docker` shadowed by a call-recording function. The stub sets
            # $global:LASTEXITCODE the same way a native docker call would, so the script's
            # post-down failure check sees the configured compose outcome. Returns the terminating
            # error the script raised (null on success).
            param(
                [Parameter(Mandatory)]
                [string]$StartScriptName,

                [Parameter(Mandatory)]
                [string]$DockerLogPath,

                [int]$DockerExitCode = 0,

                [switch]$StopOnly
            )

            $stub = {
                Add-Content -LiteralPath $DockerLogPath -Value (@($args | ForEach-Object { $_ }) -join ' ')
                $global:LASTEXITCODE = $DockerExitCode
            }.GetNewClosure()
            Set-Item -Path function:script:docker -Value $stub

            $teardownArgs = @{ d = $true }
            if (-not $StopOnly) {
                $teardownArgs.v = $true
                $teardownArgs.RemoveBootstrap = $true
            }
            try {
                $teardownError = $null
                try {
                    & (Join-Path $script:repo.DockerComposeRoot $StartScriptName) `
                        -EnvironmentFile $script:repo.EnvFile `
                        @teardownArgs |
                        Out-Null
                }
                catch {
                    $teardownError = $_
                }

                return [pscustomobject]@{
                    Error = $teardownError
                }
            }
            finally {
                Remove-Item -Path function:script:docker -Force -ErrorAction SilentlyContinue
            }
        }
    }

    BeforeEach {
        $script:repo = New-IsolatedBootstrapRepo
    }

    AfterEach {
        if ($null -ne $script:repo) {
            Get-Module bootstrap-wrapper, bootstrap-manifest, bootstrap-claims-gate, env-utility |
                Where-Object { $_.Path -like "$($script:repo.RepoRoot)*" } |
                Remove-Module -Force -ErrorAction SilentlyContinue
        }

        if ($null -ne $script:repo -and (Test-Path -LiteralPath $script:repo.RepoRoot)) {
            Remove-Item -LiteralPath $script:repo.RepoRoot -Recurse -Force
        }
    }

    # =========================================================================
    # R1 - start-local-dms.ps1 parameter surface (DmsBaseUrl added; old flags gone)
    # =========================================================================
    Context "start-local-dms.ps1 parameter surface" {
        It "declares -DmsBaseUrl" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            )
            $params | Should -Contain "DmsBaseUrl"
        }

        It "rejects -DmsBaseUrl without -InfraOnly at the param-parse level" {
            # The script throws before any Docker or module activity when DmsBaseUrl is
            # provided without InfraOnly, so we can dot-source the param block to exercise
            # the early validation path. We inject a stub for the modules it would load.
            # Extract only the early validation block (before module imports) as a scriptblock
            # by checking the raw script for the throw pattern, then verify it via a temp
            # wrapper that calls the script with invalid args.
            $startScript = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            {
                & $startScript -DmsBaseUrl "http://localhost:8080"
            } | Should -Throw "*-DmsBaseUrl requires -InfraOnly*"
        }

        It "rejects -DmsBaseUrl with -DmsOnly at the param-parse level" {
            $startScript = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            {
                & $startScript -DmsOnly -DmsBaseUrl "http://localhost:8080"
            } | Should -Throw "*-DmsBaseUrl is not valid with -DmsOnly*"
        }
    }

    # =========================================================================
    # R2 - bootstrap-local-dms.ps1 and bootstrap-published-dms.ps1 param surfaces
    # =========================================================================
    Context "wrapper entry-script parameter surfaces" {
        It "bootstrap-local-dms.ps1 declares -InfraOnly and -DmsBaseUrl" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            )
            $params | Should -Contain "InfraOnly"
            $params | Should -Contain "DmsBaseUrl"
        }

        It "bootstrap-published-dms.ps1 does not declare -InfraOnly or -DmsBaseUrl" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            )
            $params | Should -Not -Contain "InfraOnly"
            $params | Should -Not -Contain "DmsBaseUrl"
        }
    }

    # =========================================================================
    # R3 - Wrapper rejects -DmsBaseUrl without -InfraOnly
    # =========================================================================
    Context "wrapper IDE workflow fail-fast validation" {
        It "rejects -DmsBaseUrl without -InfraOnly before any phase invocation" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            {
                & $script:repo.WrapperScript `
                    -EnvironmentFile $script:repo.EnvFile `
                    -DmsBaseUrl "http://localhost:8080"
            } | Should -Throw "*-DmsBaseUrl requires -InfraOnly*"

            # No phase script should have been called
            Test-Path -LiteralPath $callLog | Should -BeFalse
        }

        It "rejects -LoadSeedData with -InfraOnly but without -DmsBaseUrl" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null

            {
                & $script:repo.WrapperScript `
                    -EnvironmentFile $script:repo.EnvFile `
                    -InfraOnly `
                    -LoadSeedData `
                    -SeedDataPath $script:repo.DockerComposeRoot
            } | Should -Throw "*-LoadSeedData with -InfraOnly requires -DmsBaseUrl*"
        }
    }

    # =========================================================================
    # R4 - Wrapper -InfraOnly shape: configure + provision, no -DmsOnly, IDE guidance
    # =========================================================================
    Context "wrapper -InfraOnly pre-DMS terminal shape" {
        It "runs configure and provision, does not invoke -DmsOnly start, and prints IDE guidance" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                *>&1 | Out-String

            $log = @(Get-Content -LiteralPath $callLog)

            # configure and provision must appear
            $log | Should -Contain "configure smoke=False"
            $log | Should -Contain "provision"

            # -DmsOnly start must NOT appear
            $log | Where-Object { $_ -like "start-dms*" } | Should -BeNullOrEmpty

            # seed must NOT be invoked (no -LoadSeedData)
            $log | Where-Object { $_ -like "seed*" } | Should -BeNullOrEmpty

            # IDE guidance must mention appsettings or DmsBaseUrl workflow hint
            $output | Should -Match "(?i)(appsettings|DmsBaseUrl|IDE)"

            # AC: terminal output must not present a second start-local-dms.ps1 run as a resume
            # mechanism; the fresh wrapper continuation invocation is the supported follow-up.
            $output | Should -Not -Match "start-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl" -Because "terminal guidance must not present a second start-local-dms.ps1 run as a resume mechanism (DMS-1153 AC)"

            # The follow-up wrapper hint must carry -NoDataStore: the terminal run already created
            # the data store, and a plain wrapper re-run creates a duplicate (verified live).
            $output | Should -Match "bootstrap-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl\s+<url>\s+-NoDataStore" -Because "the continuation hint must reuse the data store the terminal run created, not duplicate it"
        }

        It "start-local-dms.ps1 terminal guidance block does not print a second start run as a resume mechanism" {
            # Static companion to the wrapper output assertion above: the start script's own
            # -InfraOnly terminal guidance (Write-Output lines) must not suggest re-running
            # start-local-dms.ps1 with -DmsBaseUrl. Help-text documentation of the -DmsBaseUrl
            # parameter surface (.DESCRIPTION/.PARAMETER) is allowed; runtime guidance is not.
            $startContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") -Raw
            $startContent | Should -Not -Match 'Write-Output\s+"[^"\n]*start-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl' -Because "the -InfraOnly terminal guidance must not present a second start-local-dms.ps1 run as a resume mechanism (DMS-1153 AC)"
        }

        It "-InfraOnly -AddSmokeTestCredentials works without -DmsBaseUrl and forwards smoke flag to configure phase" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            # Must not throw - -AddSmokeTestCredentials with -InfraOnly (no -DmsBaseUrl) is valid
            { & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -AddSmokeTestCredentials } | Should -Not -Throw

            $log = @(Get-Content -LiteralPath $callLog)

            # smoke flag forwarded to configure, not to start
            $log | Should -Contain "configure smoke=True"

            # start-infra invocation must NOT carry any DmsBaseUrl
            $startLine = $log | Where-Object { $_ -like "start-infra*" } | Select-Object -First 1
            $startLine | Should -Match "DmsBaseUrl=$"
        }
    }

    # =========================================================================
    # R4b - Wrapper schema/claims staging phase (claims-completion before infra)
    #   A schema-only manifest (prepare-dms-schema.ps1 run without prepare-dms-claims.ps1)
    #   is an incomplete bootstrap state: start-local-dms.ps1 activates staged claims and runs
    #   the claims-ready gate, both of which require the claims/seed sections. The wrapper must
    #   complete the claims staging BEFORE any infrastructure side effects rather than letting
    #   the gate throw after Docker/CMS startup.
    # =========================================================================
    Context "wrapper schema/claims staging phase" {
        It "stages schema then claims before infrastructure when no manifest exists" {
            $callLog = Join-Path $script:repo.RepoRoot "call-log-stage-fresh.txt"
            New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly

            $log = @(Get-Content -LiteralPath $callLog)

            $schemaIndex = [array]::IndexOf($log, "prepare-schema")
            $claimsIndex = [array]::IndexOf($log, "prepare-claims")
            $startIndex  = [array]::IndexOf($log, ($log | Where-Object { $_ -like "start-infra*" } | Select-Object -First 1))

            $schemaIndex | Should -BeGreaterOrEqual 0 -Because "a clean checkout must stage the core schema"
            $claimsIndex | Should -BeGreaterThan $schemaIndex -Because "claims stage after schema"
            $startIndex  | Should -BeGreaterThan $claimsIndex -Because "all staging must run before infrastructure starts"
        }

        It "completes a schema-only manifest by staging claims (not schema) before infrastructure starts" {
            New-SchemaOnlyBootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-stage-claims.txt"
            New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly

            $log = @(Get-Content -LiteralPath $callLog)

            # Schema is already staged; only the claims completion runs.
            $log | Should -Not -Contain "prepare-schema" -Because "an already-staged schema must not be re-staged"
            $log | Should -Contain "prepare-claims" -Because "the missing claims/seed sections must be completed"

            # The claims completion must run BEFORE any infrastructure side effect.
            $claimsIndex = [array]::IndexOf($log, "prepare-claims")
            $startIndex  = [array]::IndexOf($log, ($log | Where-Object { $_ -like "start-infra*" } | Select-Object -First 1))
            $startIndex | Should -BeGreaterThan $claimsIndex -Because "claims completion must precede infrastructure startup"
        }

        It "does not false-fail claims staging when a stale nonzero \$LASTEXITCODE lingers from the session" {
            # Regression: prepare-dms-claims.ps1 signals failure by throwing and runs no native command,
            # so a nonzero $LASTEXITCODE left by an earlier command in the session must NOT be read as a
            # staging failure. The wrapper resets the sentinel before each prepare invocation.
            New-SchemaOnlyBootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-stale-exit.txt"
            New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            # Simulate a prior native-command failure lingering in the PowerShell session.
            $global:LASTEXITCODE = 99

            { & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly } |
                Should -Not -Throw -Because "a stale exit code must not be mistaken for a prepare-dms-claims.ps1 failure"

            @(Get-Content -LiteralPath $callLog) | Should -Contain "prepare-claims" -Because "claims completion must still run and succeed"
        }

        It "reuses a complete manifest as-is and does not re-stage schema or claims" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-stage-complete.txt"
            New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly

            $log = @(Get-Content -LiteralPath $callLog)

            $log | Should -Not -Contain "prepare-schema" -Because "a complete manifest must be reused as-is"
            $log | Should -Not -Contain "prepare-claims" -Because "a complete manifest must be reused as-is"
            $log | Where-Object { $_ -like "start-infra*" } | Should -Not -BeNullOrEmpty -Because "the start phase still runs"
        }
    }

    # =========================================================================
    # R5 - Wrapper -InfraOnly -DmsBaseUrl: call-graph proof
    #   Story Req 2: first start invocation has -InfraOnly but NOT -DmsBaseUrl;
    #   second (health-wait) start invocation carries -DmsBaseUrl;
    #   order: start-infra -> configure -> provision -> start-infra-healthwait
    # =========================================================================
    Context "wrapper -InfraOnly -DmsBaseUrl call-graph ordering" {
        It "first start invocation does not carry -DmsBaseUrl; health-wait invocation carries it AFTER provision" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-healthwait.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DmsBaseUrl "http://localhost:8080"

            $log = @(Get-Content -LiteralPath $callLog)

            # Exactly four entries: start-infra, configure, provision, start-infra-healthwait
            $log.Count | Should -Be 4

            # Order: [0]=start-infra, [1]=configure, [2]=provision, [3]=start-infra-healthwait
            $log[0] | Should -Match "^start-infra "
            $log[1] | Should -Match "^configure "
            $log[2] | Should -Be "provision"
            $log[3] | Should -Match "^start-infra-healthwait "

            # First start invocation must NOT carry a DmsBaseUrl
            $log[0] | Should -Match "DmsBaseUrl=$"

            # Health-wait invocation must carry the DmsBaseUrl
            $log[3] | Should -Match "DmsBaseUrl=http://localhost:8080"
        }

        It "with -LoadSeedData, -DmsBaseUrl is forwarded to seed phase after health-wait" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-seed.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DmsBaseUrl "http://localhost:8080" `
                -LoadSeedData `
                -SeedDataPath $script:repo.DockerComposeRoot

            $log = @(Get-Content -LiteralPath $callLog)

            # Five entries: start-infra, configure, provision, start-infra-healthwait, seed
            $log.Count | Should -Be 5

            # Seed line must carry DmsBaseUrl
            $log[4] | Should -Match "^seed "
            $log[4] | Should -Match "DmsBaseUrl=http://localhost:8080"

            # Seed runs AFTER the health-wait (order enforced by position)
            $provisionIndex = [array]::IndexOf($log, "provision")
            $healthWaitIndex = $log.IndexOf(($log | Where-Object { $_ -like "start-infra-healthwait*" }))
            $seedIndex = $log.IndexOf(($log | Where-Object { $_ -like "seed*" }))
            $healthWaitIndex | Should -BeGreaterThan $provisionIndex
            $seedIndex | Should -BeGreaterThan $healthWaitIndex
        }
    }

    # =========================================================================
    # R6 - Config Service always included (DMS-1153 bootstrap entry-point spec)
    #   Per the spec, every non-teardown bootstrap run starts Config Service,
    #   including keycloak-backed runs. -EnableConfig is retained for backward
    #   compatibility only and is no longer a meaningful opt-out.
    # =========================================================================
    Context "Config Service always included in compose set" {
        It "start-local-dms.ps1 includes local-config.yml for every non-DbOnly compose set" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            # Config Service is unconditional within the full application compose set. The
            # outer databaseOnlyStartup guard keeps the dedicated database diagnostic isolated.
            $startScript | Should -Match '\$files\s*\+=\s*@\("-f",\s*"local-config\.yml"\)'
            $startScript | Should -Match '(?s)if \(-not \$databaseOnlyStartup\) \{.*?\$files\s*\+=\s*@\("-f",\s*"local-config\.yml"\)'

            # There must be no feature-switch guard wrapping the local-config.yml inclusion.
            $startScript | Should -Not -Match 'if\s*\([^)]*EnableConfig[^)]*\)[^{]*\{[^}]*local-config\.yml' -Because "local-config.yml must be included unconditionally, not gated on -EnableConfig"
            $startScript | Should -Not -Match 'if\s*\(\$EnableConfig\s*-or' -Because "the old conditional guard must have been removed"
        }

        It "start-local-dms.ps1 retains -EnableConfig parameter for backward compatibility" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            )
            $params | Should -Contain "EnableConfig" -Because "-EnableConfig must be retained for backward compatibility even though it is no longer a meaningful opt-out"
        }

        It "start-published-dms.ps1 includes published-config.yml when bootstrap mode is active" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"
            ) -Raw

            $publishedConfigGuardPattern = 'if \(\$EnableConfig -or \$InfraOnly -or \$IdentityProvider -eq "self-contained" -or \$bootstrapMode\)\s*\{[^}]*?\$files \+= @\("-f", "published-config\.yml"\)'
            $startScript | Should -Match $publishedConfigGuardPattern -Because "published bootstrap starts must include the Configuration Service compose file so staged claims mount with DMS ApiSchema"
        }

        It "start-published-dms.ps1 keeps non-bootstrap keycloak published-config.yml opt-in behavior" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"
            ) -Raw

            $guardMatch = [regex]::Match(
                $startScript,
                'if \((?<condition>[^\r\n]+)\)\s*\{[^}]*?\$files \+= @\("-f", "published-config\.yml"\)'
            )
            $guardMatch.Success | Should -BeTrue

            $condition = $guardMatch.Groups["condition"].Value
            $condition | Should -Be '$EnableConfig -or $InfraOnly -or $IdentityProvider -eq "self-contained" -or $bootstrapMode'
            $condition | Should -Not -Match "keycloak" -Because "non-bootstrap keycloak published starts remain opt-in through -EnableConfig"
        }
    }

    Context "MSSQL datastore engine compose selection and runtime isolation (DMS-1238, DMS-1279)" {
        # The pin guards below assert uniqueness plus value: exactly one occurrence of each
        # pinned line may exist in the file, and it must carry the pinned value. A reverted
        # pin fails the value check, and any second occurrence (however it is nested) fails
        # the count check, so no unrelated occurrence can mask a stale authoritative pin.
        It "isolates the SQL Server 2025 runtime from legacy SQL Server volumes (DMS-1279)" {
            $mssqlCompose = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "mssql.yml"
            ) -Raw

            $imagePins = [regex]::Matches($mssqlCompose, '(?m)^\s*image:\s*\S+\s*$')
            $imagePins.Count | Should -Be 1 -Because "mssql.yml defines a single db service with a single image pin"
            $imagePins[0].Value.Trim() | Should -Be 'image: mcr.microsoft.com/mssql/server:2025-latest'

            $dataMounts = [regex]::Matches($mssqlCompose, '(?m)^\s*-\s*\S+:/var/opt/mssql\s*$')
            $dataMounts.Count | Should -Be 1 -Because "the SQL Server data directory must be mounted from exactly one named volume"
            $dataMounts[0].Value.Trim() | Should -Be '- dms-mssql-2025:/var/opt/mssql' -Because "attaching the 2025 runtime to a legacy SQL Server 2022 volume is not supported"

            $volumeDeclarations = [regex]::Matches($mssqlCompose, '(?m)^\s*dms-mssql[\w-]*:\s*$')
            $volumeDeclarations.Count | Should -Be 1 -Because "exactly one dms-mssql* named volume may be declared, keeping the legacy volume detached"
            $volumeDeclarations[0].Value.Trim() | Should -Be 'dms-mssql-2025:'
        }

        It "pins the DMS CI MSSQL_IMAGE env to the SQL Server 2025 image (DMS-1279)" {
            $dmsWorkflow = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot ".github/workflows/on-dms-pullrequest.yml"
            ) -Raw

            $imagePins = [regex]::Matches($dmsWorkflow, '(?m)^\s*MSSQL_IMAGE:\s*\S+\s*$')
            $imagePins.Count | Should -Be 1 -Because "the workflow-level env is the single authoritative MSSQL_IMAGE pin for the backend, DMS API, and SchemaTools MSSQL lanes: on an older runtime the native-json evaluation fixture ignores itself instead of failing, so this pin must never be duplicated or reverted silently"
            $imagePins[0].Value.Trim() | Should -Be 'MSSQL_IMAGE: "mcr.microsoft.com/mssql/server:2025-latest"'
        }

        It "pins the CMS CI integration-test mssql services container to the SQL Server 2025 image (DMS-1279)" {
            $cmsWorkflow = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot ".github/workflows/on-config-pullrequest.yml"
            ) -Raw

            $mssqlImagePins = [regex]::Matches($cmsWorkflow, '(?m)^\s*image:\s*mcr\.microsoft\.com/mssql/server:\S+\s*$')
            $mssqlImagePins.Count | Should -Be 1 -Because "the workflow runs exactly one SQL Server container (the run-integration-tests mssql service), so a second occurrence could only mask a stale pin"
            $mssqlImagePins[0].Value.Trim() | Should -Be 'image: mcr.microsoft.com/mssql/server:2025-latest'
        }

        It "keeps the template-workflow backup-coupling comments aligned with the SQL Server 2025 image (DMS-1279)" {
            foreach ($workflowName in @("build-minimal-template.yml", "build-populated-template.yml")) {
                $workflow = Get-Content -LiteralPath (
                    Join-Path $script:sourceRepoRoot ".github/workflows/$workflowName"
                ) -Raw

                $workflow | Should -Match '(?m)^\s*#\s*backup is coupled to the mcr\.microsoft\.com/mssql/server:2025-latest image used to create\s*$' -Because "$workflowName documents the exact image line its .bak template packages are coupled to in this comment, and that comment must move with the executable pins"
                $workflow | Should -Not -Match 'mssql/server:2022' -Because "$workflowName must not describe the backup coupling against the retired SQL Server 2022 image"
            }
        }

        It "start-local-dms.ps1 declares a -DatabaseEngine parameter validated to postgresql/mssql" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            )
            $params | Should -Contain "DatabaseEngine"

            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw
            $startScript | Should -Match '\[ValidateSet\("postgresql",\s*"mssql"\)\]'
        }

        It "start-local-dms.ps1 exposes -Rebuild as an alias for -r" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match '\[Alias\("Rebuild"\)\]\s*\[Switch\]\s*\$r'
        }

        It "start-local-dms.ps1 swaps the database compose file by engine (single db service)" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            # postgresql.yml and mssql.yml both define the "db" service that local-config.yml
            # gates on (depends_on: db: service_healthy), so exactly one of them may join the
            # compose set: SQL Server hosts the whole stack on the mssql path (DMS-1243 CMS
            # SQL Server backend), and no PostgreSQL container runs.
            $startScript | Should -Match '\$databaseComposeFile = if \(\$DatabaseEngine -eq "mssql"\) \{ "mssql\.yml" \} else \{ "postgresql\.yml" \}'
            $startScript | Should -Match '\$files\s*=\s*@\(\s*"-f",\s*\$databaseComposeFile'
            $startScript | Should -Not -Match '\$files \+= @\("-f", "mssql\.yml"\)'
        }

        It "start-local-dms.ps1 passes engine-aware database parameters to every setup-openiddict.ps1 call" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            # On SQL Server the OpenIddict stores live in the shared DMS datastore database
            # (created by -InitDb when missing, now that CMS shares it too); every invocation
            # must splat the shared engine-aware parameters.
            $startScript | Should -Match 'DbType = "MSSQL"; DbUser = "sa"; DbPort = "ENV:MSSQL_PORT"; DbName = "ENV:MSSQL_DB_NAME"'
            $openiddictCalls = [regex]::Matches($startScript, '(?m)^.*\./setup-openiddict\.ps1 .*$')
            $openiddictCalls.Count | Should -BeGreaterThan 0
            foreach ($call in $openiddictCalls) {
                $call.Value | Should -Match '@identityDbParams'
            }
        }

        It "start-local-dms.ps1 gates Kafka on the PostgreSQL engine (no Debezium CDC on the MSSQL path)" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match 'if \(\$enableKafkaInfrastructure -and \$DatabaseEngine -eq "postgresql"\)\s*\{[^}]*\$files \+= @\("-f", "kafka\.yml"\)'
        }

        It "start-local-dms.ps1 gates the Kafka UI startup on the PostgreSQL engine (no Debezium CDC on the MSSQL path)" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match 'if \(\$EnableKafkaUI -and \$DatabaseEngine -eq "postgresql"\)\s*\{[^}]*up \$upArgs kafka-ui'
            $startScript | Should -Match 'elseif \(\$EnableKafkaUI -and \$DatabaseEngine -eq "mssql"\)\s*\{[^}]*Skipping Kafka UI'
        }

        It "start-local-dms.ps1 composes the MSSQL engine overlay after the data-standard overlay and before reading env values" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $dataStandardIndex = $startScript.IndexOf('$EnvironmentFile = Resolve-DataStandardEnvironmentFile')
            $engineIndex = $startScript.IndexOf('$EnvironmentFile = Resolve-DatabaseEngineEnvironmentFile')
            $readValuesIndex = $startScript.IndexOf('$envValues = ReadValuesFromEnvFile $EnvironmentFile')

            $dataStandardIndex | Should -BeGreaterThan -1
            $engineIndex | Should -BeGreaterThan $dataStandardIndex
            $readValuesIndex | Should -BeGreaterThan $engineIndex

            $startScript | Should -Match 'Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine \$DatabaseEngine -BaseEnvironmentFile \$EnvironmentFile -DockerComposeRoot \$PSScriptRoot'
        }

        It "start-local-dms.ps1 falls back to shared local-settings env resolution when -EnvironmentFile is omitted" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            # A clean checkout has no hand-created .env; the documented teardown
            # (start-local-dms.ps1 -DatabaseEngine mssql -d -v) and other direct invocations
            # must resolve through the shared resolver (which seeds .env from the tracked
            # .env.example) instead of throwing on ./.env.
            $startScript | Should -Match 'Resolve-LocalSettingsEnvironmentFile -Path "" -DockerComposeRoot \$PSScriptRoot'
            $startScript | Should -Not -Match '\$EnvironmentFile = "\./\.env"'
        }
    }

    Context "Bootstrap -DataStandardVersion local surface selection" {
        It "bootstrap entry points declare -DataStandardVersion validated to 5.2/6.1 defaulting to 5.2" {
            foreach ($name in @("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                $params = Get-DeclaredScriptParameters -Path (
                    Join-Path $script:sourceDockerComposeRoot $name
                )
                $params | Should -Contain "DataStandardVersion"

                $source = Get-Content -LiteralPath (
                    Join-Path $script:sourceDockerComposeRoot $name
                ) -Raw
                $source | Should -Match '\[ValidateSet\("5\.2",\s*"6\.1"\)\]\s*\[string\]\$DataStandardVersion = "5\.2"'
            }
        }

        It "the wrapper declares the same validated 5.2 default (defaults do not cross PSBoundParameters)" {
            $wrapperSource = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            ) -Raw

            $wrapperSource | Should -Match '\[ValidateSet\("5\.2",\s*"6\.1"\)\]\s*\[string\]\$DataStandardVersion = "5\.2"'
        }

        It "the wrapper composes the local-bootstrap overlay into the gitignored .derived directory" {
            $wrapperSource = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            ) -Raw

            # env-utility must be imported inside the composition block: the wrapper's other
            # env-utility imports live inside helper functions that run AFTER this block.
            $wrapperSource | Should -Match '(?s)Import-Module \(Join-Path \$PSScriptRoot "env-utility\.psm1"\) -Force\s*\$baseEnvFile = Resolve-DataStandardEnvironmentFile'
            $wrapperSource | Should -Match '-DataStandardVersion \$DataStandardVersion'
            $wrapperSource | Should -Match '-OverlayPrefix "\.env\.bootstrap"'
        }

        It "the wrapper gates overlay composition to always-on for start-local-dms.ps1 and explicit-only for start-published-dms.ps1" {
            $wrapperSource = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            ) -Raw

            $wrapperSource | Should -Match '\$composeDataStandardOverlay\s*=\s*\(\$StartScriptName\s+-eq\s+"start-local-dms\.ps1"\)\s*-or\s*\r?\n\s*\$PSBoundParameters\.ContainsKey\(''DataStandardVersion''\)'
            $wrapperSource | Should -Match '(?s)if \(\$composeDataStandardOverlay\)\s*\{.*?Resolve-DataStandardEnvironmentFile'
        }

        It "the wrapper never forwards -DataStandardVersion to a start script" {
            # The start scripts' own -DataStandardVersion composes the SHARED .env.ds<NN> overlays
            # (E2E/SDK surfaces including Sample/Homograph); forwarding it would re-compose that
            # surface over the local-bootstrap derived env. The start phases must receive the
            # derived file via -EnvironmentFile only.
            $wrapperSource = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            ) -Raw

            $wrapperSource | Should -Not -Match '(startArgs|healthWaitArgs|dmsStartArgs)\.DataStandardVersion'
            $wrapperSource | Should -Not -Match '(startArgs|healthWaitArgs|dmsStartArgs)\["DataStandardVersion"\]'
        }

        It "local-bootstrap overlays carry the minimal surfaces: DS 5.2 core+TPDM, DS 6.1 core only" {
            $ds52Overlay = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot ".env.bootstrap.ds52"
            ) -Raw
            $ds61Overlay = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot ".env.bootstrap.ds61"
            ) -Raw

            $ds52Names = @([regex]::Matches($ds52Overlay, '"name":"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
            $ds52Names | Should -Be @("EdFi.DataStandard52.ApiSchema", "EdFi.DataStandard52.TPDM.ApiSchema")

            $ds61Names = @([regex]::Matches($ds61Overlay, '"name":"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
            $ds61Names | Should -Be @("EdFi.DataStandard61.ApiSchema")

            $ds52Overlay | Should -Match '(?m)^DMS_CONFIG_DATA_STANDARD_VERSION=5\.2$'
            $ds61Overlay | Should -Match '(?m)^DMS_CONFIG_DATA_STANDARD_VERSION=6\.1$'

            # Single-line SCHEMA_PACKAGES keeps overlay composition trivial (base multi-line block
            # is removed wholesale before the overlay is appended).
            $ds52SchemaLines = @($ds52Overlay -split "`n" | Where-Object { $_ -match "^SCHEMA_PACKAGES=" })
            $ds52SchemaLines.Count | Should -Be 1
            $ds52SchemaLines[0] | Should -Match "\]'\s*$"
            $ds61SchemaLines = @($ds61Overlay -split "`n" | Where-Object { $_ -match "^SCHEMA_PACKAGES=" })
            $ds61SchemaLines.Count | Should -Be 1
            $ds61SchemaLines[0] | Should -Match "\]'\s*$"
        }
    }

    Context "Bootstrap -DataStandardVersion overlay composition gating per entry point" {
        BeforeAll {
            Import-Module (Join-Path $script:sourceDockerComposeRoot "env-utility.psm1") -Force

            # Builds an isolated repo carrying only the named wrapper entry script, its shared
            # wrapper module, and the composition prerequisites (env-utility + bootstrap overlays).
            # The stubbed start script copies whatever -EnvironmentFile the wrapper forwards to it
            # so the test can inspect the effective env file's composed (or uncomposed) contents.
            # No configure/provision/prepare siblings are staged, so the wrapper's isolated-fixture
            # degrade path returns immediately after this single start invocation (mirrors the
            # BootstrapSeedDelivery.Tests.ps1 published-wrapper fixtures).
            function script:New-CompositionProbeRepo {
                param(
                    [Parameter(Mandatory)]
                    [ValidateSet("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")]
                    [string]$WrapperEntryScriptName,

                    [Parameter(Mandatory)]
                    [string]$BaseSchemaPackagesValue
                )

                $repoRoot = New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    "bootstrap-wrapper.psm1",
                    $WrapperEntryScriptName,
                    "env-utility.psm1",
                    ".env.bootstrap.ds52",
                    ".env.bootstrap.ds61"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }

                $envFile = Join-Path $dockerComposeRoot ".env.example"
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
SCHEMA_PACKAGES='$BaseSchemaPackagesValue'
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                $capturedEnvPath = Join-Path $repoRoot "captured.env"
                $startScriptName = $WrapperEntryScriptName -replace '^bootstrap-', 'start-'
                @"
param(
    [string] `$EnvironmentFile,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Copy-Item -LiteralPath `$EnvironmentFile -Destination '$capturedEnvPath' -Force
"@ | Set-Content -LiteralPath (Join-Path $dockerComposeRoot $startScriptName) -Encoding utf8

                return [pscustomobject]@{
                    RepoRoot        = $repoRoot
                    WrapperScript   = Join-Path $dockerComposeRoot $WrapperEntryScriptName
                    CapturedEnvPath = $capturedEnvPath
                }
            }
        }

        AfterEach {
            if ($null -ne $script:compositionProbeRepo -and (Test-Path -LiteralPath $script:compositionProbeRepo.RepoRoot)) {
                Remove-Item -LiteralPath $script:compositionProbeRepo.RepoRoot -Recurse -Force
            }
            $script:compositionProbeRepo = $null
        }

        It "bootstrap-local-dms.ps1 composes the overlay by default, overriding a custom base SCHEMA_PACKAGES" {
            $script:compositionProbeRepo = New-CompositionProbeRepo `
                -WrapperEntryScriptName "bootstrap-local-dms.ps1" `
                -BaseSchemaPackagesValue '[{"name":"Custom.Base.Package","version":"9.9.9"}]'

            & $script:compositionProbeRepo.WrapperScript

            $capturedValues = ReadValuesFromEnvFile $script:compositionProbeRepo.CapturedEnvPath
            $capturedValues["DMS_CONFIG_DATA_STANDARD_VERSION"] | Should -Be "5.2"
            $capturedValues["SCHEMA_PACKAGES"] | Should -BeLike "*EdFi.DataStandard52.ApiSchema*"
            $capturedValues["SCHEMA_PACKAGES"] | Should -Not -BeLike "*Custom.Base.Package*" -Because "the local entry point always composes the default overlay over a custom base SCHEMA_PACKAGES"
        }

        It "bootstrap-published-dms.ps1 composes the overlay when -DataStandardVersion is explicitly supplied" {
            $script:compositionProbeRepo = New-CompositionProbeRepo `
                -WrapperEntryScriptName "bootstrap-published-dms.ps1" `
                -BaseSchemaPackagesValue '[{"name":"Custom.Base.Package","version":"9.9.9"}]'

            & $script:compositionProbeRepo.WrapperScript -DataStandardVersion "6.1"

            $capturedValues = ReadValuesFromEnvFile $script:compositionProbeRepo.CapturedEnvPath
            $capturedValues["DMS_CONFIG_DATA_STANDARD_VERSION"] | Should -Be "6.1"
            $capturedValues["SCHEMA_PACKAGES"] | Should -BeLike "*EdFi.DataStandard61.ApiSchema*"
            $capturedValues["SCHEMA_PACKAGES"] | Should -Not -BeLike "*Custom.Base.Package*" -Because "an explicit -DataStandardVersion composes the overlay even on the published entry point"
        }

        It "bootstrap-published-dms.ps1 does not compose the overlay and leaves a custom base SCHEMA_PACKAGES untouched when -DataStandardVersion is omitted" {
            $script:compositionProbeRepo = New-CompositionProbeRepo `
                -WrapperEntryScriptName "bootstrap-published-dms.ps1" `
                -BaseSchemaPackagesValue '[{"name":"Custom.Base.Package","version":"9.9.9"}]'

            & $script:compositionProbeRepo.WrapperScript

            $capturedValues = ReadValuesFromEnvFile $script:compositionProbeRepo.CapturedEnvPath
            $capturedValues.ContainsKey("DMS_CONFIG_DATA_STANDARD_VERSION") | Should -BeFalse -Because "no overlay was composed, so no overlay-only key was introduced"
            $capturedValues["SCHEMA_PACKAGES"] | Should -BeLike "*Custom.Base.Package*" -Because "omitting -DataStandardVersion on the published entry point must leave the base env file's own SCHEMA_PACKAGES driving the run"
        }
    }

    Context "MSSQL engine overlay composition via the wrapper (DMS-1238)" {
        BeforeAll {
            Import-Module (Join-Path $script:sourceDockerComposeRoot "env-utility.psm1") -Force

            # Same isolated-fixture shape as the -DataStandardVersion composition probe above: a
            # stubbed start-local-dms.ps1 copies whatever -EnvironmentFile the wrapper forwards to
            # it so the test can inspect the effective (composed) env file's contents.
            function script:New-EngineOverlayProbeRepo {
                $repoRoot = New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    "bootstrap-wrapper.psm1",
                    "bootstrap-local-dms.ps1",
                    "env-utility.psm1",
                    ".env.bootstrap.ds52",
                    ".env.bootstrap.ds61",
                    ".env.mssql"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }

                $envFile = Join-Path $dockerComposeRoot ".env.example"
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
DMS_DATASTORE=postgresql
SCHEMA_PACKAGES='[{"version":"1.0.333","name":"EdFi.DataStandard52.ApiSchema"},{"version":"1.0.333","name":"EdFi.DataStandard52.TPDM.ApiSchema"}]'
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                $capturedEnvPath = Join-Path $repoRoot "captured.env"
                @"
param(
    [string] `$EnvironmentFile,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Copy-Item -LiteralPath `$EnvironmentFile -Destination '$capturedEnvPath' -Force
"@ | Set-Content -LiteralPath (Join-Path $dockerComposeRoot "start-local-dms.ps1") -Encoding utf8

                return [pscustomobject]@{
                    RepoRoot        = $repoRoot
                    WrapperScript   = Join-Path $dockerComposeRoot "bootstrap-local-dms.ps1"
                    CapturedEnvPath = $capturedEnvPath
                }
            }
        }

        AfterEach {
            if ($null -ne $script:engineOverlayProbeRepo -and (Test-Path -LiteralPath $script:engineOverlayProbeRepo.RepoRoot)) {
                Remove-Item -LiteralPath $script:engineOverlayProbeRepo.RepoRoot -Recurse -Force
            }
            $script:engineOverlayProbeRepo = $null
        }

        It "bootstrap-local-dms.ps1 -DatabaseEngine mssql composes DMS_DATASTORE=mssql and the SQL Server admin connection string onto the effective env" {
            $script:engineOverlayProbeRepo = New-EngineOverlayProbeRepo

            & $script:engineOverlayProbeRepo.WrapperScript -DatabaseEngine mssql

            $capturedValues = ReadValuesFromEnvFile $script:engineOverlayProbeRepo.CapturedEnvPath
            $capturedValues["DMS_DATASTORE"] | Should -Be "mssql"
            $capturedValues["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Match "^Server=dms-mssql;"
            $capturedValues["MSSQL_SA_PASSWORD"] | Should -Not -BeNullOrEmpty

            # The data-standard overlay (always composed for the local entry point) must still take
            # effect: the two overlays touch disjoint keys and must not clobber one another.
            $capturedValues["DMS_CONFIG_DATA_STANDARD_VERSION"] | Should -Be "5.2"
            $capturedValues["SCHEMA_PACKAGES"] | Should -BeLike "*EdFi.DataStandard52.ApiSchema*"
        }

        It "bootstrap-local-dms.ps1 without -DatabaseEngine (postgresql default) composes nothing new" {
            $script:engineOverlayProbeRepo = New-EngineOverlayProbeRepo

            & $script:engineOverlayProbeRepo.WrapperScript

            $capturedValues = ReadValuesFromEnvFile $script:engineOverlayProbeRepo.CapturedEnvPath
            $capturedValues["DMS_DATASTORE"] | Should -Be "postgresql"
            $capturedValues.ContainsKey("MSSQL_SA_PASSWORD") | Should -BeFalse -Because "the postgresql engine (default) must not introduce any MSSQL-only keys"
            $capturedValues.ContainsKey("DATABASE_CONNECTION_STRING") | Should -BeFalse
        }
    }

    Context "Published InfraOnly claims-ready gate" {
        It "start-published-dms.ps1 imports and runs the claims-ready gate after CMS auth metadata setup" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"
            ) -Raw

            $startScript | Should -Match 'Import-Module \(Join-Path \$PSScriptRoot "bootstrap-claims-gate\.psm1"\) -Force'

            $infraStart = $startScript.IndexOf('if ($InfraOnly) {')
            $infraStart | Should -BeGreaterThan -1
            $infraComplete = $startScript.IndexOf(
                'Write-Output "Infrastructure phase complete. DMS service was not started."',
                $infraStart
            )
            $infraComplete | Should -BeGreaterThan $infraStart
            $infraBlock = $startScript.Substring($infraStart, $infraComplete - $infraStart)

            $healthIndex = $infraBlock.IndexOf('Write-Output "Configuration Service is healthy."')
            $authMetadataClientIndex = $infraBlock.IndexOf('CMSAuthMetadataReadOnlyAccess')
            $gateIndex = $infraBlock.IndexOf('Test-CmsClaimsReady')

            $healthIndex | Should -BeGreaterThan -1
            $authMetadataClientIndex | Should -BeGreaterThan -1
            $gateIndex | Should -BeGreaterThan $healthIndex
            $gateIndex | Should -BeGreaterThan $authMetadataClientIndex

            $infraBlock | Should -Match 'if \(\$bootstrapManifestPresent\)\s*\{[\s\S]*?Test-CmsClaimsReady'
            $infraBlock | Should -Match '-EnvironmentFile \$EnvironmentFile'
            $infraBlock | Should -Match '-IdentityProvider \$IdentityProvider'
        }

        It "start-published-dms.ps1 skips the claims-ready gate with an informational message on no-manifest InfraOnly runs" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"
            ) -Raw

            $infraStart = $startScript.IndexOf('if ($InfraOnly) {')
            $infraComplete = $startScript.IndexOf(
                'Write-Output "Infrastructure phase complete. DMS service was not started."',
                $infraStart
            )
            $infraBlock = $startScript.Substring($infraStart, $infraComplete - $infraStart)

            $infraBlock | Should -Match 'else\s*\{[\s\S]*?Write-Information "Claims gate: no bootstrap manifest present; skipping claims-ready check on no-bootstrap run\." -InformationAction Continue'
        }
    }

    # =========================================================================
    # DMS-1272 - bootstrap-local-dms.ps1 teardown delegation
    #   The turnkey entry point stops the stack (-d) and optionally deletes
    #   volumes + removes the .bootstrap workspace (-d -v) by delegating to
    #   start-local-dms.ps1, short-circuiting before any staging/configure/
    #   provision/DMS/seed orchestration. start-local-dms.ps1 still owns
    #   -d/-v/-RemoveBootstrap; the published wrapper gains no teardown flags
    #   (local-only).
    # =========================================================================
    Context "bootstrap-local-dms.ps1 teardown parameter surface (DMS-1272)" {
        It "bootstrap-local-dms.ps1 declares -d and -v" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            )
            $params | Should -Contain "d"
            $params | Should -Contain "v"
        }

        It "bootstrap-published-dms.ps1 does not declare -d or -v (teardown is local-only)" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            )
            $params | Should -Not -Contain "d"
            $params | Should -Not -Contain "v"
        }

        It "start-local-dms.ps1 still owns -d, -v, and -RemoveBootstrap" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            )
            $params | Should -Contain "d"
            $params | Should -Contain "v"
            $params | Should -Contain "RemoveBootstrap"
        }
    }

    Context "bootstrap-local-dms.ps1 teardown delegation call-graph (DMS-1272)" {
        It "-d delegates to start-local-dms.ps1 -d and runs no staging/configure/provision/seed phase" {
            $delegation = Invoke-TeardownDelegation -CallLogName "call-log-teardown-d.txt"

            $delegation | Should -Match "d=True"
            $delegation | Should -Match "v=False"
            $delegation | Should -Match "RemoveBootstrap=False" -Because "-d alone must not remove the .bootstrap workspace"
        }

        It "-d -v delegates to start-local-dms.ps1 with -v and -RemoveBootstrap, running no other phase" {
            $delegation = Invoke-TeardownDelegation -CallLogName "call-log-teardown-dv.txt" -v

            $delegation | Should -Match "d=True"
            $delegation | Should -Match "v=True"
            $delegation | Should -Match "RemoveBootstrap=True" -Because "-d -v must remove the .bootstrap workspace via delegation"
        }

        It "-d propagates the failure when the start-local-dms.ps1 teardown delegation throws" {
            $callLog = Join-Path $script:repo.RepoRoot "call-log-teardown-exit.txt"
            New-RecordingTeardownStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog `
                -FailureMessage "Failed to shut down Docker environment. Exit code 3" | Out-Null

            {
                & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -d
            } | Should -Throw "*Failed to shut down Docker environment. Exit code 3*"

            # The delegation must have run and recorded before it threw.
            (@(Get-Content -LiteralPath $callLog))[0] | Should -Match "^teardown "
        }

        It "-d forwards the teardown-relevant compose-shape options to start-local-dms.ps1" {
            $callLog = Join-Path $script:repo.RepoRoot "call-log-teardown-forward.txt"
            New-RecordingTeardownStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -IdentityProvider self-contained `
                -EnableKafkaUI `
                -EnableSwaggerUI `
                -DatabaseEngine mssql `
                -d

            $log = @(Get-Content -LiteralPath $callLog)

            $log.Count | Should -Be 1
            $log[0] | Should -Match "IdentityProvider=self-contained"
            $log[0] | Should -Match "EnableKafkaUI=True"
            $log[0] | Should -Match "EnableSwaggerUI=True"
            $log[0] | Should -Match "DatabaseEngine=mssql"
            $log[0] | Should -Match ([regex]::Escape("EnvironmentFile=$($script:repo.EnvFile)"))
        }

        It "-d forwards only the compose-shape whitelist; every other declared option stays behind" {
            # Guards the teardown forwarding whitelist. Options that do not change the compose-file set
            # must never reach the delegation; the stub records anything outside the whitelist in Rest=,
            # so an accidental addition to the forwarding loop surfaces here instead of passing silently.
            $forwarded = @('EnvironmentFile', 'IdentityProvider', 'EnableKafkaUI', 'EnableSwaggerUI', 'DatabaseEngine')
            $excluded = @(
                'LoadSeedData', 'SeedTemplate', 'SeedDataPath', 'AdditionalNamespacePrefix',
                'SchoolYearRange', 'DataStandardVersion', 'InfraOnly', 'DmsBaseUrl',
                'EnableConfig', 'AddExtensionSecurityMetadata', 'NoDataStore', 'AddSmokeTestCredentials'
            )

            # Completeness guard: every parameter the entry script declares must be classified here
            # as a teardown switch, forwarded, or excluded (and bound below), so a new parameter
            # fails this assertion and forces an explicit forwarding decision.
            $declared = Get-DeclaredScriptParameters -Path $script:repo.WrapperScript
            ($declared | Sort-Object) | Should -Be ((@('d', 'v') + $forwarded + $excluded) | Sort-Object)

            # Binds every excluded parameter (the teardown short-circuit returns before the wrapper's
            # option-validation rules run, so all of them can be bound in one invocation); an unbound
            # option would slip through the forwarding loop's ContainsKey gate unnoticed.
            $callLog = Join-Path $script:repo.RepoRoot "call-log-teardown-no-overforward.txt"
            New-RecordingTeardownStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -LoadSeedData `
                -SeedTemplate Minimal `
                -SeedDataPath (Join-Path $script:repo.RepoRoot "seed-data") `
                -AdditionalNamespacePrefix "uri://example.org" `
                -SchoolYearRange "2025-2026" `
                -DataStandardVersion 6.1 `
                -InfraOnly `
                -DmsBaseUrl "http://localhost:5198" `
                -EnableConfig `
                -AddExtensionSecurityMetadata `
                -NoDataStore `
                -AddSmokeTestCredentials `
                -d

            $log = @(Get-Content -LiteralPath $callLog)

            $log.Count | Should -Be 1
            $log[0] | Should -Match 'Rest=$' -Because "no argument outside the compose-shape whitelist may reach start-local-dms.ps1 teardown"
        }
    }

    Context "bootstrap-local-dms.ps1 teardown fail-fast validation (DMS-1272)" {
        It "-v without -d is rejected before any phase or delegation runs" {
            $callLog = Join-Path $script:repo.RepoRoot "call-log-teardown-v-only.txt"
            New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog
            New-RecordingTeardownStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            {
                & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -v
            } | Should -Throw "*-v requires -d*"

            Test-Path -LiteralPath $callLog | Should -BeFalse -Because "no phase or teardown delegation may run when -v is supplied without -d"
        }
    }

    Context "bootstrap-local-dms.ps1 explicit -Switch:`$false teardown binding (DMS-1272)" {
        It "-d:`$false takes the start path and does not reach the wrapper as an undeclared parameter" {
            # An explicit -d:$false / -v:$false binds the switch into $PSBoundParameters without tripping
            # the teardown short-circuit, so the start path is reached with the switches still bound. The
            # entry script must strip both before splatting to Invoke-BootstrapWrapper, which declares
            # neither; an unstripped -d/-v would crash the wrapper on parameter binding before any phase.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-d-false.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly -d:$false -v:$false

            $log = @(Get-Content -LiteralPath $callLog)

            # The start path ran (configure + provision), proving -d:$false did not short-circuit to
            # teardown and that -d/-v were stripped before the wrapper splat (an unstripped -d throws
            # on binding, leaving the log empty).
            $log | Should -Contain "configure smoke=False"
            $log | Should -Contain "provision"
        }
    }

    # =========================================================================
    # DMS-1272 - start-script teardown failure handling
    #   A failed `docker compose down` must throw before any .bootstrap
    #   workspace removal: a failed down can leave services running against the
    #   bind-mounted schema and claims, and a silent exit reads as success to
    #   direct/CI teardown consumers and to the bootstrap wrapper, which relies
    #   on the thrown error propagating. These tests run the REAL start scripts
    #   against a stubbed docker.
    # =========================================================================
    Context "start-script teardown failure handling (DMS-1272)" {
        BeforeEach {
            foreach ($fileName in @(
                "start-local-dms.ps1",
                "start-published-dms.ps1",
                "bootstrap-manifest.psm1",
                "bootstrap-claims-gate.psm1"
            )) {
                Copy-DockerComposeFile -FileName $fileName -Destination $script:repo.DockerComposeRoot
            }
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
        }

        It "start-local-dms.ps1 throws and preserves the workspace when compose down fails" {
            $dockerLog = Join-Path $script:repo.RepoRoot "docker-log-local-fail.txt"

            $result = Invoke-RealStartScriptTeardown -StartScriptName "start-local-dms.ps1" -DockerLogPath $dockerLog -DockerExitCode 7

            $log = @(Get-Content -LiteralPath $dockerLog)
            $log | Should -HaveCount 1 -Because "teardown must issue exactly one docker compose invocation"
            $log[0] | Should -Match '-p dms-local down --remove-orphans -v$'
            Test-Path -LiteralPath $script:repo.BootstrapRoot | Should -BeTrue -Because "a failed down can leave services running against the bind-mounted workspace"
            $result.Error | Should -Not -BeNullOrEmpty -Because "a failed down must fail loudly for direct/CI teardown consumers"
            $result.Error.Exception.Message | Should -Match 'Failed to shut down Docker environment\. Exit code 7'
        }

        It "start-local-dms.ps1 still removes the workspace when compose down succeeds" {
            $dockerLog = Join-Path $script:repo.RepoRoot "docker-log-local-ok.txt"

            $result = Invoke-RealStartScriptTeardown -StartScriptName "start-local-dms.ps1" -DockerLogPath $dockerLog -DockerExitCode 0

            @(Get-Content -LiteralPath $dockerLog)[0] | Should -Match '-p dms-local down --remove-orphans -v$'
            Test-Path -LiteralPath $script:repo.BootstrapRoot | Should -BeFalse -Because "a clean -d -v -RemoveBootstrap teardown must remove the workspace"
            $result.Error | Should -BeNullOrEmpty
        }

        It "start-local-dms.ps1 throws on a plain -d stop when compose down fails" {
            # Pins the throw on the volume-less branch too: the bootstrap wrapper's -d delegation
            # has no exit-code check of its own, so it relies on this error to propagate.
            $dockerLog = Join-Path $script:repo.RepoRoot "docker-log-local-stop-fail.txt"

            $result = Invoke-RealStartScriptTeardown -StartScriptName "start-local-dms.ps1" -DockerLogPath $dockerLog -DockerExitCode 7 -StopOnly

            @(Get-Content -LiteralPath $dockerLog)[0] | Should -Match '-p dms-local down --remove-orphans$'
            $result.Error | Should -Not -BeNullOrEmpty -Because "a failed down must fail loudly for direct/CI teardown consumers"
            $result.Error.Exception.Message | Should -Match 'Failed to shut down Docker environment\. Exit code 7'
        }

        It "start-published-dms.ps1 throws and preserves the workspace when compose down fails" {
            $dockerLog = Join-Path $script:repo.RepoRoot "docker-log-published-fail.txt"

            $result = Invoke-RealStartScriptTeardown -StartScriptName "start-published-dms.ps1" -DockerLogPath $dockerLog -DockerExitCode 7

            $log = @(Get-Content -LiteralPath $dockerLog)
            $log | Should -HaveCount 1 -Because "teardown must issue exactly one docker compose invocation"
            $log[0] | Should -Match '-p dms-published down --remove-orphans -v$'
            Test-Path -LiteralPath $script:repo.BootstrapRoot | Should -BeTrue -Because "a failed down can leave services running against the bind-mounted workspace"
            $result.Error | Should -Not -BeNullOrEmpty -Because "a failed down must fail loudly for direct/CI teardown consumers"
            $result.Error.Exception.Message | Should -Match 'Failed to shut down Docker environment\. Exit code 7'
        }

        It "start-published-dms.ps1 still removes the workspace when compose down succeeds" {
            $dockerLog = Join-Path $script:repo.RepoRoot "docker-log-published-ok.txt"

            $result = Invoke-RealStartScriptTeardown -StartScriptName "start-published-dms.ps1" -DockerLogPath $dockerLog -DockerExitCode 0

            @(Get-Content -LiteralPath $dockerLog)[0] | Should -Match '-p dms-published down --remove-orphans -v$'
            Test-Path -LiteralPath $script:repo.BootstrapRoot | Should -BeFalse -Because "a clean -d -v -RemoveBootstrap teardown must remove the workspace"
            $result.Error | Should -BeNullOrEmpty
        }
    }

    # =========================================================================
    # DMS-1238 - -DatabaseEngine parameter surface and unconditional forwarding across both
    # start scripts. mssql.yml is no longer a local-only tier: both bootstrap entry points
    # accept -DatabaseEngine, and the wrapper forwards it to whichever start script it targets
    # without gating on StartScriptName.
    # =========================================================================
    Context "Bootstrap -DatabaseEngine parameter surface and forwarding across both start scripts (DMS-1238)" {
        BeforeAll {
            # Isolated fixture proving forwarding directly: a stub start script declares its own
            # -DatabaseEngine parameter and records the bound value, so the assertion does not
            # depend on inspecting the wrapper's own source. Mirrors New-EngineOverlayProbeRepo's
            # shape (env-utility + bootstrap overlays are unused here but keep the fixture valid
            # for either wrapper entry script without a second variant).
            function script:New-DatabaseEngineForwardingProbeRepo {
                param(
                    [Parameter(Mandatory)]
                    [ValidateSet("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")]
                    [string]$WrapperEntryScriptName
                )

                $repoRoot = New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    "bootstrap-wrapper.psm1",
                    $WrapperEntryScriptName,
                    "env-utility.psm1",
                    ".env.bootstrap.ds52",
                    ".env.bootstrap.ds61",
                    ".env.mssql"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }

                $envFile = Join-Path $dockerComposeRoot ".env.example"
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                $callLog = Join-Path $repoRoot "call-log.txt"
                $startScriptName = $WrapperEntryScriptName -replace '^bootstrap-', 'start-'
                @"
param(
    [string] `$EnvironmentFile,
    [string] `$DatabaseEngine,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$callLog' -Value "start DatabaseEngine=`$DatabaseEngine"
"@ | Set-Content -LiteralPath (Join-Path $dockerComposeRoot $startScriptName) -Encoding utf8

                return [pscustomobject]@{
                    RepoRoot      = $repoRoot
                    WrapperScript = Join-Path $dockerComposeRoot $WrapperEntryScriptName
                    CallLog       = $callLog
                }
            }
        }

        AfterEach {
            if ($null -ne $script:databaseEngineForwardingRepo -and (Test-Path -LiteralPath $script:databaseEngineForwardingRepo.RepoRoot)) {
                Remove-Item -LiteralPath $script:databaseEngineForwardingRepo.RepoRoot -Recurse -Force
            }
            $script:databaseEngineForwardingRepo = $null
        }

        It "bootstrap-local-dms.ps1 and bootstrap-published-dms.ps1 both declare -DatabaseEngine validated to postgresql/mssql defaulting to postgresql" {
            foreach ($name in @("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                $params = Get-DeclaredScriptParameters -Path (
                    Join-Path $script:sourceDockerComposeRoot $name
                )
                $params | Should -Contain "DatabaseEngine"

                $source = Get-Content -LiteralPath (
                    Join-Path $script:sourceDockerComposeRoot $name
                ) -Raw
                $source | Should -Match '\[ValidateSet\("postgresql",\s*"mssql"\)\]\s*\[string\]\$DatabaseEngine = "postgresql"'
            }
        }

        It "the wrapper forwards -DatabaseEngine to the start, health-wait, and DMS-start phases unconditionally, regardless of StartScriptName" {
            $wrapperSource = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            ) -Raw

            # mssql.yml is no longer a local-only tier, so there must be no gating variable
            # deciding forwarding by StartScriptName.
            $wrapperSource | Should -Not -Match "startScriptSupportsDatabaseEngine"
            $wrapperSource | Should -Match '(?m)^\s*\$startArgs\.DatabaseEngine\s*=\s*\$DatabaseEngine\s*$'
            $wrapperSource | Should -Match '(?m)^\s*\$healthWaitArgs\.DatabaseEngine\s*=\s*\$DatabaseEngine\s*$'
            $wrapperSource | Should -Match '(?m)^\s*\$dmsStartArgs\.DatabaseEngine\s*=\s*\$DatabaseEngine\s*$'
        }

        It "bootstrap-local-dms.ps1 forwards -DatabaseEngine mssql to start-local-dms.ps1" {
            $script:databaseEngineForwardingRepo = New-DatabaseEngineForwardingProbeRepo -WrapperEntryScriptName "bootstrap-local-dms.ps1"

            & $script:databaseEngineForwardingRepo.WrapperScript -DatabaseEngine mssql

            $log = @(Get-Content -LiteralPath $script:databaseEngineForwardingRepo.CallLog)
            $log | Should -Contain "start DatabaseEngine=mssql"
        }

        It "bootstrap-published-dms.ps1 forwards -DatabaseEngine mssql to start-published-dms.ps1" {
            $script:databaseEngineForwardingRepo = New-DatabaseEngineForwardingProbeRepo -WrapperEntryScriptName "bootstrap-published-dms.ps1"

            & $script:databaseEngineForwardingRepo.WrapperScript -DatabaseEngine mssql

            $log = @(Get-Content -LiteralPath $script:databaseEngineForwardingRepo.CallLog)
            $log | Should -Contain "start DatabaseEngine=mssql"
        }
    }

    # =========================================================================
    # -DbOnly parameter surface and mutual exclusivity: both start scripts start only the
    # database container (a slice for diagnostics and for other tooling to sequence a
    # database-only startup around) and reject combination with -InfraOnly/-DmsOnly.
    # =========================================================================
    Context "Bootstrap -DbOnly parameter surface and mutual exclusivity" {
        BeforeAll {
            # Isolated fixture carrying only what the target start script needs to reach its own
            # -DbOnly/-InfraOnly/-DmsOnly mutual-exclusion guard. Its deliberately malformed
            # bootstrap manifest proves DbOnly bypasses workspace validation before reaching that
            # guard, without any Docker or CMS side effect.
            function script:New-StartScriptGuardRepo {
                param(
                    [Parameter(Mandatory)]
                    [ValidateSet("start-local-dms.ps1", "start-published-dms.ps1")]
                    [string]$StartScriptName
                )

                $repoRoot = New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    $StartScriptName,
                    "bootstrap-manifest.psm1",
                    "bootstrap-claims-gate.psm1",
                    "env-utility.psm1"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }

                $envFile = Join-Path $dockerComposeRoot ".env.guard"
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=unsupported-for-db-only
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
# Deliberately invalid application-only settings. DbOnly parameter guards must still be reached,
# proving the database slice does not parse identity configuration before returning.
DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH=not-an-integer
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                $bootstrapRoot = Join-Path $dockerComposeRoot ".bootstrap"
                New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $bootstrapRoot "bootstrap-manifest.json") -Value '{ malformed' -NoNewline

                return [pscustomobject]@{
                    RepoRoot    = $repoRoot
                    StartScript = Join-Path $dockerComposeRoot $StartScriptName
                    EnvFile     = $envFile
                }
            }
        }

        It "start-local-dms.ps1 and start-published-dms.ps1 both declare -DbOnly" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $params = Get-DeclaredScriptParameters -Path (
                    Join-Path $script:sourceDockerComposeRoot $name
                )
                $params | Should -Contain "DbOnly"
            }
        }

        It "both start scripts bypass bootstrap activation and manifest inspection for database-only startup" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $source = Get-Content -LiteralPath (
                    Join-Path $script:sourceDockerComposeRoot $name
                ) -Raw

                $source | Should -Match '\$databaseOnlyStartup\s*=\s*\$DbOnly\s*-and\s*-not\s*\$d'
                $source | Should -Match '(?s)if \(-not \$databaseOnlyStartup\) \{.*?Import-Module .*?bootstrap-manifest\.psm1.*?bootstrap-claims-gate\.psm1'
                $source | Should -Match '(?s)\$bootstrapMode\s*=\s*\$false.*?\$bootstrapManifestPresent\s*=\s*\$false.*?if \(-not \$databaseOnlyStartup\) \{.*?Invoke-BootstrapStartupConfiguration.*?Get-BootstrapRoot'
                $source | Should -Match '(?s)\$envValues\s*=\s*ReadValuesFromEnvFile.*?if \(-not \$databaseOnlyStartup\) \{.*?Resolve-IdentityClientSecretConfiguration'
                $source | Should -Match 'Resolve-DatabaseEngineEnvironmentFile[^\r\n]*-SkipMssqlCmsDatabaseValidation:\(\$databaseOnlyStartup -or \$d\)' -Because "DbOnly and teardown must not parse application-only CMS database settings"
            }
        }

        It "both start scripts keep application and bootstrap compose files out of database-only startup" {
            foreach ($case in @(
                @{ Name = "start-local-dms.ps1"; ApplicationFile = "local-dms.yml"; ConfigFile = "local-config.yml" },
                @{ Name = "start-published-dms.ps1"; ApplicationFile = "published-dms.yml"; ConfigFile = "published-config.yml" }
            )) {
                $source = Get-Content -LiteralPath (
                    Join-Path $script:sourceDockerComposeRoot $case.Name
                ) -Raw
                $applicationComposeFileIndex = $source.IndexOf('"' + $case.ApplicationFile + '"')
                $applicationComposeGuardIndex = $source.LastIndexOf(
                    'if (-not $databaseOnlyStartup) {',
                    $applicationComposeFileIndex
                )

                $applicationComposeFileIndex | Should -BeGreaterThan -1
                $applicationComposeGuardIndex | Should -BeGreaterThan -1
                $applicationComposeFileIndex | Should -BeGreaterThan $applicationComposeGuardIndex
                $source.IndexOf('"' + $case.ConfigFile + '"', $applicationComposeGuardIndex) |
                    Should -BeGreaterThan $applicationComposeGuardIndex
                $source.IndexOf('"bootstrap-dms.yml"', $applicationComposeGuardIndex) |
                    Should -BeGreaterThan $applicationComposeGuardIndex
                $source | Should -Match 'docker compose \$files --env-file \$EnvironmentFile -p dms-(?:local|published) up \$upArgs db'
                $source | Should -Match '(?s)\$upArgs\s*=\s*@\("--detach"\).*?if \(-not \$databaseOnlyStartup\) \{.*?\$upArgs\s*\+=\s*"--remove-orphans"' -Because "DbOnly must not remove already-running application containers omitted from its reduced compose set"
            }
        }

        It "start-local-dms.ps1 and start-published-dms.ps1 both reject -DbOnly with -InfraOnly at the param-parse level" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $guardRepo = New-StartScriptGuardRepo -StartScriptName $name
                try {
                    {
                        & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -DbOnly -InfraOnly
                    } | Should -Throw "*-DbOnly is mutually exclusive with -InfraOnly and -DmsOnly*"
                }
                finally {
                    Remove-Item -LiteralPath $guardRepo.RepoRoot -Recurse -Force
                }
            }
        }

        It "start-local-dms.ps1 and start-published-dms.ps1 both reject -DbOnly with -DmsOnly at the param-parse level" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $guardRepo = New-StartScriptGuardRepo -StartScriptName $name
                try {
                    {
                        & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -DbOnly -DmsOnly
                    } | Should -Throw "*-DbOnly is mutually exclusive with -InfraOnly and -DmsOnly*"
                }
                finally {
                    Remove-Item -LiteralPath $guardRepo.RepoRoot -Recurse -Force
                }
            }
        }

        It "start-local-dms.ps1 rejects -DbOnly with -r before any Docker activity" {
            $guardRepo = New-StartScriptGuardRepo -StartScriptName "start-local-dms.ps1"
            try {
                { & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -DbOnly -r } |
                    Should -Throw "*-r/-Rebuild is not valid with -DbOnly*"
            }
            finally {
                Remove-Item -LiteralPath $guardRepo.RepoRoot -Recurse -Force
            }
        }
    }

    # =========================================================================
    # build-dms.ps1 StartEnvironment forwarding: -DatabaseEngine forwards only when supplied
    # (the bootstrap wrapper's own default governs when it is omitted); -DataStandardVersion
    # forwards only when the caller explicitly supplied it (captured at script scope as
    # $dataStandardVersionSupplied, before Invoke-Main, since PSBoundParameters inside the
    # Invoke-Main script block reflects that block's own bindings, not the top-level script's).
    # =========================================================================
    Context "build-dms.ps1 StartEnvironment forwarding (-DatabaseEngine, -DataStandardVersion)" {
        It "declares -DatabaseEngine validated to postgresql/mssql and -DataStandardVersion validated to 5.2/6.1" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $buildScript | Should -Match '\[ValidateSet\("postgresql",\s*"mssql"\)\]\s*\$DatabaseEngine,'
            $buildScript | Should -Match '\[ValidateSet\("5\.2",\s*"6\.1"\)\]\s*\$DataStandardVersion\r?\n\)'
        }

        It "captures whether -DataStandardVersion was supplied at script scope, before Invoke-Main" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $suppliedIndex = $buildScript.IndexOf(
                '$dataStandardVersionSupplied = $PSBoundParameters.ContainsKey(''DataStandardVersion'')'
            )
            $invokeMainIndex = $buildScript.IndexOf('Invoke-Main {')

            $suppliedIndex | Should -BeGreaterThan -1
            $invokeMainIndex | Should -BeGreaterThan $suppliedIndex -Because "PSBoundParameters inside the Invoke-Main script block reflects that block's own bindings, not the top-level script's"
        }

        It "the StartEnvironment command forwards -DatabaseEngine, -DataStandardVersion, and the supplied-gate switch to Start-BootstrapDockerEnvironment" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $buildScript | Should -Match 'StartEnvironment \{ Invoke-Step \{ Start-BootstrapDockerEnvironment -UsePublishedImage:\$UsePublishedImage -SkipDockerBuild:\$SkipDockerBuild -LoadSeedData:\$LoadSeedData -DatabaseEngine \$DatabaseEngine -IdentityProvider \$IdentityProvider -DataStandardVersion \$DataStandardVersion -DataStandardVersionSupplied:\$dataStandardVersionSupplied \} \}'
        }

        It "Start-BootstrapDockerEnvironment forwards -DatabaseEngine to the bootstrap wrapper only when supplied" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $buildScript | Should -Match '(?s)if \(\$DatabaseEngine\) \{\s*\$bootstrapArgs\.DatabaseEngine = \$DatabaseEngine\s*\}'
        }

        It "Start-BootstrapDockerEnvironment forwards the effective database engine to teardown" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $buildScript | Should -Match '(?s)\$effectiveDatabaseEngine\s*=.*?if \(\[string\]::IsNullOrWhiteSpace\(\$DatabaseEngine\)\).*?"postgresql"'
            $buildScript | Should -Match '(?s)Stop-DockerEnvironment\s+`\s*-EnvironmentFilePath \$environmentFilePath\s+`\s*-IdentityProvider \$IdentityProvider\s+`\s*-DatabaseEngine \$effectiveDatabaseEngine'
            $buildScript | Should -Match '(?s)function Stop-DockerEnvironment.*?\[ValidateSet\("postgresql", "mssql"\)\].*?\$DatabaseEngine = "postgresql"'
            $buildScript | Should -Match 'start-local-dms\.ps1 .*?-DatabaseEngine \$DatabaseEngine -d -v'
            $buildScript | Should -Match 'start-published-dms\.ps1 .*?-DatabaseEngine \$DatabaseEngine -d -v'
        }

        It "Start-BootstrapDockerEnvironment forwards -DataStandardVersion to the bootstrap wrapper only when the caller explicitly supplied it" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $buildScript | Should -Match '(?s)if \(\$DataStandardVersionSupplied\) \{\s*\$bootstrapArgs\.DataStandardVersion = \$DataStandardVersion\s*\}'
        }
    }
}
