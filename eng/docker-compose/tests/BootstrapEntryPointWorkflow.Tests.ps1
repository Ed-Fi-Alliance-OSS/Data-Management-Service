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
                # The wrapper always composes the local-bootstrap data-standard overlay
                # (default 5.2) onto the base env via env-utility, so every wrapper
                # invocation needs the utility module and the overlay files.
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

            $manifest = [ordered]@{
                version = 1
                schema  = [ordered]@{
                    selectionMode        = "Standard"
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
    }

    BeforeEach {
        $script:repo = New-IsolatedBootstrapRepo
    }

    AfterEach {
        if ($null -ne $script:repo) {
            Get-Module bootstrap-wrapper |
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
        It "start-local-dms.ps1 includes local-config.yml unconditionally (no if-guard)" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            # The unconditional assignment must be present.
            $startScript | Should -Match '\$files\s*\+=\s*@\("-f",\s*"local-config\.yml"\)'

            # There must be NO conditional guard wrapping the local-config.yml inclusion.
            # Verify by asserting the old conditional pattern is absent.
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

    Context "MSSQL datastore engine compose selection (DMS-1238)" {
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

        It "start-local-dms.ps1 always includes postgresql.yml (CMS + identity stay on PostgreSQL)" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match '\$files\s*=\s*@\(\s*"-f",\s*"postgresql\.yml"'
        }

        It "start-local-dms.ps1 composes mssql.yml only under the mssql engine guard" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match 'if \(\$DatabaseEngine -eq "mssql"\)\s*\{[^}]*\$files \+= @\("-f", "mssql\.yml"\)'
        }

        It "start-local-dms.ps1 gates Kafka on the PostgreSQL engine (no Debezium CDC on the MSSQL path)" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match 'if \(\$enableKafkaInfrastructure -and \$DatabaseEngine -eq "postgresql"\)\s*\{[^}]*\$files \+= @\("-f", "kafka\.yml"\)'
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
}
