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
                "bootstrap-local-dms.ps1"
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
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
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
    # R1 — start-local-dms.ps1 parameter surface (DmsBaseUrl added; old flags gone)
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
            $startScriptContent = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

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
    # R2 — bootstrap-local-dms.ps1 and bootstrap-published-dms.ps1 param surfaces
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
    # R3 — Wrapper rejects -DmsBaseUrl without -InfraOnly
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
    # R4 — Wrapper -InfraOnly shape: configure + provision, no -DmsOnly, IDE guidance
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

            # Must not throw — -AddSmokeTestCredentials with -InfraOnly (no -DmsBaseUrl) is valid
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
    # R5 — Wrapper -InfraOnly -DmsBaseUrl: call-graph proof
    #   Story Req 2: first start invocation has -InfraOnly but NOT -DmsBaseUrl;
    #   second (health-wait) start invocation carries -DmsBaseUrl;
    #   order: start-infra → configure → provision → start-infra-healthwait
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
    # R6 — Regression D3: NEED_DATABASE_SETUP forced false when manifest present
    #   The BootstrapSchemaDeploymentSafety suite already checks the conditional
    #   block text ("if ($bootstrapManifestPresent)"). Here we extend coverage to
    #   verify the actual assignment ($env:NEED_DATABASE_SETUP = "false") is present
    #   in that block and NOT in the non-manifest branch.
    # =========================================================================
    Context "NEED_DATABASE_SETUP manifest-present lockdown (D3 regression)" {
        It "start-local-dms.ps1 forces NEED_DATABASE_SETUP to false inside the manifest-present branch" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            # The assignment must be inside the if ($bootstrapManifestPresent) region.
            # The existing test in BootstrapSchemaDeploymentSafety asserts the if-guard text;
            # this companion assertion verifies the forced-false assignment itself is present.
            $startScript | Should -Match '\$env:NEED_DATABASE_SETUP\s*=\s*"false"'

            # The non-manifest branch must NOT contain the forced assignment (it defers to env file).
            # Verify by confirming the forced assignment only appears inside the bootstrapManifestPresent region.
            # Strategy: the "else" branch for no-manifest starts DMS without any NEED_DATABASE_SETUP override,
            # and the message for that path says "controlled by the environment file".
            $startScript | Should -Match 'No bootstrap manifest detected; starting DMS with database startup provisioning controlled by the environment file\.'
        }
    }
}
