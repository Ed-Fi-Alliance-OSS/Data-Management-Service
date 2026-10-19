# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
param()

# Exact workspace ownership, recorded at creation. Fixtures below stage copies of repository
# scripts and modules inside temp workspaces this run creates, and EXECUTING a staged script
# imports env-utility (and siblings) from the staged path - module-table instances that outlive
# the deleted workspace and break any later suite in the same session that binds -ModuleName
# mocks. The file-level cleanup at the bottom may unload ONLY module instances rooted beneath a
# workspace this same run created and recorded through this registrar. Nothing else establishes
# ownership: not a directory-name prefix, not a module name, not living under the system temp
# directory - a caller-owned module beneath a lookalike-named directory is not this file's to
# touch, and the whole-file lifecycle tests at the bottom pin exactly that.
BeforeAll {
    $script:ownedWorkspaceRoot = [System.Collections.Generic.List[string]]::new()
    function script:Register-OwnedWorkspaceRoot {
        param([Parameter(Mandatory)] [string]$Path)
        $script:ownedWorkspaceRoot.Add([System.IO.Path]::GetFullPath($Path))
    }
}

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
            # Recorded before any staged code can import from it: this exact root is what the
            # file-level cleanup is allowed to unload module instances from.
            Register-OwnedWorkspaceRoot -Path $path
            return $path
        }

        function script:Copy-DockerComposeFile {
            param(
                [string]$FileName,
                [string]$Destination
            )
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $FileName) -Destination $Destination
            if ($FileName -in @('start-local-dms.ps1', 'start-published-dms.ps1')) {
                # These fixtures exercise ordinary startup/teardown with no retained CDC deployment.
                # CDC deployment discovery and governance have their own production-module suite.
                @'
function Test-CdcInfrastructureInvocation { return $false }
function Test-CdcDeployment { param($Project) return $false }
function Assert-CdcUnregisteredInfrastructure { param($Project) }
function Test-CdcBootstrapWorkspaceProtected { param($BootstrapRoot) return $false }
Export-ModuleMember -Function Test-CdcInfrastructureInvocation, Test-CdcDeployment, Assert-CdcUnregisteredInfrastructure, Test-CdcBootstrapWorkspaceProtected
'@ | Set-Content -LiteralPath (Join-Path $Destination 'cdc-lifecycle.psm1')
            }
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
                "bootstrap-published-dms.ps1",
                "bootstrap-schema-catalog.psm1",
                # The wrapper always composes the local-bootstrap data-standard overlay
                # (default 5.2) onto the base env via env-utility, so every wrapper
                # invocation needs the utility module and the overlay files.
                "env-utility.psm1",
                # The real start scripts (copied into this fixture by contexts that run them)
                # import the shared Compose-equivalent resolver from this module.
                "database-safety.psm1",
                ".env.bootstrap.ds52",
                ".env.bootstrap.ds61",
                # A -DatabaseEngine mssql wrapper run composes this engine overlay onto the base
                # env before any phase command, so the fixture needs it to reach the phase scripts.
                ".env.mssql"
            )) {
                Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
            }
            Copy-Item `
                -LiteralPath (Join-Path $script:sourceRepoRoot "eng/schema-package-utility.psm1") `
                -Destination (Join-Path $engRoot "schema-package-utility.psm1")

            # The wrapper formats the -AddSmokeTestCredentials summary with the real smoke-test module,
            # imported from its repository-relative path; that module imports Dms-Management.psm1.
            $smokeModuleRoot = Join-Path $engRoot "smoke_test/modules"
            New-Item -ItemType Directory -Path $smokeModuleRoot -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $script:sourceRepoRoot "eng/smoke_test/modules/SmokeTest.psm1") -Destination $smokeModuleRoot
            Copy-Item -LiteralPath (Join-Path $script:sourceRepoRoot "eng/Dms-Management.psm1") -Destination $engRoot

            $envFile = Join-Path $dockerComposeRoot ".env.example"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            return [pscustomobject]@{
                RepoRoot         = $repoRoot
                DockerComposeRoot = $dockerComposeRoot
                BootstrapRoot    = Join-Path $dockerComposeRoot ".bootstrap"
                EnvFile          = $envFile
                WrapperScript    = Join-Path $dockerComposeRoot "bootstrap-local-dms.ps1"
                PublishedWrapperScript = Join-Path $dockerComposeRoot "bootstrap-published-dms.ps1"
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
                [string]$CallLogPath,

                # When supplied, each invocation also records the forwarded engine and
                # -SeparateConfigDatabase state to this separate file. Deliberately not the shared
                # call log: several tests assert that log's exact line count and ordering.
                [string]$ForwardLogPath,

                [ValidateSet("start-local-dms.ps1", "start-published-dms.ps1")]
                [string]$FileName = "start-local-dms.ps1"
            )

            $scriptPath = Join-Path $Directory $FileName
            $forwardRecording = if ([string]::IsNullOrWhiteSpace($ForwardLogPath)) {
                ""
            }
            else {
                "Add-Content -LiteralPath '$ForwardLogPath' -Value `"`$label engine=`$DatabaseEngine separate=`$(`$SeparateConfigDatabase.IsPresent)`""
            }
            @"
param(
    [switch] `$InfraOnly,
    [switch] `$DmsOnly,
    [switch] `$r,
    [switch] `$EnableConfig,
    [switch] `$SuppressWriterGuidance,
    [string] `$EnvironmentFile,
    [string] `$IdentityProvider,
    [string] `$DmsBaseUrl,
    [string] `$DatabaseEngine,
    [switch] `$SeparateConfigDatabase,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
`$hasDmsBaseUrl = `$PSBoundParameters.ContainsKey('DmsBaseUrl') -and -not [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)
`$label = if (`$InfraOnly -and `$hasDmsBaseUrl) { "start-infra-healthwait" }
          elseif (`$InfraOnly) { "start-infra" }
          elseif (`$DmsOnly)  { "start-dms" }
          else                { "start-legacy" }
Add-Content -LiteralPath '$CallLogPath' -Value "`$label DmsBaseUrl=`$DmsBaseUrl rebuild=`$(`$r.IsPresent) writerGuidanceSuppressed=`$(`$SuppressWriterGuidance.IsPresent)"
if (`$InfraOnly -and -not `$hasDmsBaseUrl -and -not `$SuppressWriterGuidance) {
    Write-Output "Infrastructure phase complete. DMS service was not started."
}
$forwardRecording
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:New-RecordingConfigureScript {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath,

                [long[]]$DataStoreIds = @(42),

                # Shape of the returned result, mirroring configure-local-data-store.ps1: a tenant,
                # school-year route contexts for data stores 101/2024 and 102/2025, and - like the real
                # phase - a synthetic SmokeTestCredential only when -AddSmokeTestCredentials is bound,
                # unless -OmitSmokeTestCredential models a phase that breaks that contract.
                [string]$Tenant = "",

                [switch]$SchoolYearContexts,

                [switch]$OmitSmokeTestCredential
            )

            $idsJson = ($DataStoreIds | ForEach-Object { "[long]$_" }) -join ', '
            $routeContexts = if ($SchoolYearContexts) {
                "@([pscustomobject]@{ DataStoreId = [long]101; ContextKey = 'schoolYear'; ContextValue = '2024' }, [pscustomobject]@{ DataStoreId = [long]102; ContextKey = 'schoolYear'; ContextValue = '2025' })"
            }
            else {
                "@()"
            }
            $smokeCredentialCondition = if ($OmitSmokeTestCredential) { "`$false" } else { "`$AddSmokeTestCredentials" }
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
`$result = [pscustomobject]@{
    DataStoreIds              = [long[]] @($idsJson)
    SelectedDataStoreIds      = [long[]] @($idsJson)
    RouteContexts             = $routeContexts
    Tenant                    = '$Tenant'
    SchoolYears               = [int[]] @()
    HasRouteQualifiedDataStores = `$$([bool]$SchoolYearContexts)
}
if ($smokeCredentialCondition) {
    `$result | Add-Member -NotePropertyName SmokeTestCredential -NotePropertyValue ([pscustomobject][ordered]@{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH`$SECRET-1292'; ApplicationName = 'Smoke Test Application' })
}
`$result
"@ | Set-Content -LiteralPath $scriptPath -Encoding utf8
            return $scriptPath
        }

        function script:New-RecordingProvisionScript {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [Parameter(Mandatory)]
                [string]$CallLogPath,

                # When supplied, the bound -DatabaseEngine is recorded to this separate file.
                # Deliberately not the shared call log: several tests assert that log's exact line
                # count and ordering, so adding a line there would break them.
                [string]$EngineLogPath,

                # When supplied, the bound -SeparateConfigDatabase state is recorded to this separate
                # file, for the same reason the engine log is kept out of the shared call log.
                [string]$ForwardLogPath
            )

            $scriptPath = Join-Path $Directory "provision-dms-schema.ps1"
            $engineRecording = if ([string]::IsNullOrWhiteSpace($EngineLogPath)) {
                ""
            }
            else {
                "Add-Content -LiteralPath '$EngineLogPath' -Value `"provision-engine=`$DatabaseEngine`""
            }
            $forwardRecording = if ([string]::IsNullOrWhiteSpace($ForwardLogPath)) {
                ""
            }
            else {
                "Add-Content -LiteralPath '$ForwardLogPath' -Value `"provision separate=`$(`$SeparateConfigDatabase.IsPresent)`""
            }
            @"
param(
    [string] `$EnvironmentFile,
    [long[]] `$DataStoreId,
    [string] `$DatabaseEngine,
    [switch] `$SeparateConfigDatabase,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$CallLogPath' -Value "provision"
$engineRecording
$forwardRecording
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
            Get-Module bootstrap-wrapper, bootstrap-manifest, bootstrap-claims-gate, env-utility, SmokeTest, Dms-Management -All |
                Where-Object { $_.Path -like "$($script:repo.RepoRoot)*" } |
                Remove-Module -Force -ErrorAction SilentlyContinue
        }

        if ($null -ne $script:repo -and (Test-Path -LiteralPath $script:repo.RepoRoot)) {
            Remove-Item -LiteralPath $script:repo.RepoRoot -Recurse -Force
        }
    }

    Context "ordinary DmsOnly dependency startup" {
        BeforeEach {
            foreach ($fileName in @('start-local-dms.ps1', 'start-published-dms.ps1',
                'bootstrap-manifest.psm1', 'bootstrap-claims-gate.psm1')) {
                Copy-DockerComposeFile -FileName $fileName -Destination $script:repo.DockerComposeRoot
            }
            $script:dockerCommands = [Collections.Generic.List[string]]::new()
            $recordedCommands = $script:dockerCommands
            $stub = {
                $recordedCommands.Add((@($args | ForEach-Object { $_ }) -join ' '))
                $global:LASTEXITCODE = 0
                if ($args[0] -eq 'network') { return 'existing-network' }
            }.GetNewClosure()
            Set-Item function:script:docker -Value $stub
            Mock Invoke-WebRequest { @{ StatusCode = 200 } }
        }
        AfterEach { Remove-Item function:script:docker -Force -ErrorAction SilentlyContinue }

        It 'allows dependencies and preserves peers for <flavor> DmsOnly (Swagger=<swagger>)' -ForEach @(
            @{ flavor = 'local'; swagger = $false }, @{ flavor = 'published'; swagger = $false },
            @{ flavor = 'local'; swagger = $true }, @{ flavor = 'published'; swagger = $true }
        ) {
            & (Join-Path $script:repo.DockerComposeRoot "start-$flavor-dms.ps1") `
                -EnvironmentFile $script:repo.EnvFile -DmsOnly -EnableSwaggerUI:$swagger | Out-Null

            $commands = @($script:dockerCommands | Where-Object { $_ -like 'compose *' })
            $commands.Count | Should -Be 1
            $services = if ($swagger) { 'dms swagger-ui' } else { 'dms' }
            $commands[0] | Should -Match "-p dms-$flavor up --detach $services$"
            $commands[0] | Should -Not -Match '--no-deps|--remove-orphans'
            Should -Invoke Invoke-WebRequest -Times 1 -Exactly
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
        It "bootstrap-local-dms.ps1 declares -InfraOnly, -DmsBaseUrl, and -Rebuild" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            )
            $params | Should -Contain "InfraOnly"
            $params | Should -Contain "DmsBaseUrl"
            $params | Should -Contain "Rebuild"
        }

        It "bootstrap-local-dms.ps1 exposes -r as the public alias for -Rebuild" {
            $tokens = $null
            $errors = $null
            $scriptPath = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $scriptPath,
                [ref]$tokens,
                [ref]$errors
            )
            if ($errors.Count -gt 0) {
                throw "Failed to parse $scriptPath"
            }

            $rebuildParameter = $ast.ParamBlock.Parameters |
                Where-Object { $_.Name.VariablePath.UserPath -eq "Rebuild" }
            $aliases = @(
                $rebuildParameter.Attributes |
                    Where-Object { $_.TypeName.Name -eq "Alias" } |
                    ForEach-Object { $_.PositionalArguments.Value }
            )

            $aliases | Should -Contain "r"
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

    Context "normal local wrapper startup controls" {
        It "forwards explicit rebuild only to the initial infrastructure startup and suppresses its terminal guidance" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-rebuild.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -Rebuild `
                *>&1 | Out-String

            $log = @(Get-Content -LiteralPath $callLog)
            $log | Should -Contain "start-infra DmsBaseUrl= rebuild=True writerGuidanceSuppressed=True"
            $log | Should -Contain "start-dms DmsBaseUrl= rebuild=False writerGuidanceSuppressed=False"
            $output | Should -Not -Match "Infrastructure phase complete\. DMS service was not started\."
        }

        It "does not rebuild by default, suppresses initial terminal guidance, and reaches DMS-only startup" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-default-start.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                *>&1 | Out-String

            $log = @(Get-Content -LiteralPath $callLog)
            $log | Should -Contain "start-infra DmsBaseUrl= rebuild=False writerGuidanceSuppressed=True"
            $log | Should -Contain "start-dms DmsBaseUrl= rebuild=False writerGuidanceSuppressed=False"
            $output | Should -Not -Match "Infrastructure phase complete\. DMS service was not started\."
        }

        It "suppresses initial terminal guidance before published DMS startup" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-published-start.txt"
            New-RecordingStartScript `
                -Directory $script:repo.DockerComposeRoot `
                -CallLogPath $callLog `
                -FileName "start-published-dms.ps1" | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $output = & $script:repo.PublishedWrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                *>&1 | Out-String

            $log = @(Get-Content -LiteralPath $callLog)
            $log | Should -Contain "start-infra DmsBaseUrl= rebuild=False writerGuidanceSuppressed=True"
            $log | Should -Contain "start-dms DmsBaseUrl= rebuild=False writerGuidanceSuppressed=False"
            $output | Should -Not -Match "Infrastructure phase complete\. DMS service was not started\."
        }
    }

    # =========================================================================
    # R4 - Wrapper -InfraOnly shape: configure + provision, no -DmsOnly, IDE guidance
    # =========================================================================
    Context "wrapper -InfraOnly pre-DMS terminal shape" {
        BeforeAll {
            # The start script's continuation-fragment builder, lifted from the real file so the rows
            # below exercise production text rather than a copy. Dot-sourcing start-local-dms.ps1 is not
            # an option - it is a straight-line script that would start Docker - so the single function
            # is extracted, the same way this suite reaches other in-script helpers.
            function script:Get-ScriptFunctionText {
                param(
                    [Parameter(Mandatory)] [string]$ScriptPath,
                    [Parameter(Mandatory)] [string]$FunctionName
                )

                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors.Count -gt 0) { throw "Failed to parse $ScriptPath" }
                $functionAst = $ast.FindAll(
                    { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName },
                    $true) | Select-Object -First 1
                if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
                return $functionAst.Extent.Text
            }

            . ([scriptblock]::Create((Get-ScriptFunctionText `
                -ScriptPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") `
                -FunctionName "Get-ContinuationCommandArgument")))
            Set-Alias -Name Get-StartScriptContinuationArgument -Value Get-ContinuationCommandArgument -Scope Script

            # The whole terminal guidance block, also lifted from the real file, so every command it would
            # print can be collected. Reaching it by actually running the script is not possible in a unit
            # test: the -InfraOnly path first waits on SQL Server readiness through a native docker exec
            # and then on a real CMS /health endpoint, both from inside the script itself.
            . ([scriptblock]::Create((Get-ScriptFunctionText `
                -ScriptPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") `
                -FunctionName "Get-InfraOnlyTerminalGuidance")))

            # Every bootstrap-local-dms.ps1 continuation command in a block of guidance lines, reduced to
            # the bindable argument text: everything after the "-DmsBaseUrl <url>" placeholder and before
            # the "[-LoadSeedData ...]" decoration, both illustrative rather than bindable.
            function script:Get-PrintedWrapperContinuationArgumentList {
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the collection of continuation arguments found; the plural noun reflects the return shape.')]
                # Guidance blocks contain blank spacer lines, which a [string[]] parameter rejects by
                # default; both allowances are needed to pass a block through verbatim.
                param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]]$Line)

                return @(
                    foreach ($candidate in $Line) {
                        if ($candidate -match 'bootstrap-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl\s+<url>') {
                            $afterPlaceholder = ($candidate -split '-DmsBaseUrl\s+<url>\s*', 2)[1]
                            ($afterPlaceholder -replace '\[\-LoadSeedData[^\]]*\]\s*$', '').Trim()
                        }
                    }
                )
            }

            # A parameter block standing in for the continuation targets, so an emitted fragment is
            # proven to BIND - one argument per value, original characters intact - instead of merely
            # appearing in the text. The parameter names and types mirror the real scripts'; a separate
            # row asserts those scripts actually declare them.
            function script:New-ContinuationArgumentRecorder {
                param([Parameter(Mandatory)] [string]$Directory)

                $path = Join-Path $Directory "continuation-argument-recorder.ps1"
                @'
param(
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql",
    [string]$EnvironmentFile = "",
    [string]$DataStandardVersion = "",
    [string]$IdentityProvider = "",
    [Switch]$NoDataStore,
    [Switch]$SeparateConfigDatabase
)

[pscustomobject]@{
    DatabaseEngine = $DatabaseEngine
    EnvironmentFile = $EnvironmentFile
    DataStandardVersion = $DataStandardVersion
    IdentityProvider = $IdentityProvider
    NoDataStore = $NoDataStore.IsPresent
    SeparateConfigDatabase = $SeparateConfigDatabase.IsPresent
} | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath $path -Encoding utf8
                return $path
            }

            function script:Invoke-BoundContinuationArgument {
                param(
                    [Parameter(Mandatory)] [string]$RecorderPath,
                    [Parameter(Mandatory)] [string]$ArgumentText
                )

                # The fragment is parsed and bound by PowerShell itself, which is the whole point: a path
                # that lost its quoting would bind as two arguments (or fail) here rather than pass.
                $quotedRecorder = "'" + $RecorderPath.Replace("'", "''") + "'"
                return & ([scriptblock]::Create("& $quotedRecorder $ArgumentText")) | ConvertFrom-Json
            }

            function script:Get-PrintedWrapperContinuationArgument {
                <#
                .SYNOPSIS
                The single fresh-run continuation command the supplied output must contain, reduced to its
                bindable argument text. Throws when the output carries none, or more than one - two
                continuation commands in one transcript is itself the defect, since they can disagree
                about the environment to compose from.
                #>
                param([Parameter(Mandatory)] [string]$Output)

                $arguments = @(Get-PrintedWrapperContinuationArgumentList -Line ($Output -split "`r?`n"))
                if ($arguments.Count -eq 0) {
                    throw "The output printed no fresh-run continuation command."
                }
                if ($arguments.Count -gt 1) {
                    throw "The output printed $($arguments.Count) fresh-run continuation commands; exactly one may appear. Found: $($arguments -join ' || ')"
                }

                return $arguments[0]
            }
        }

        It "runs configure and provision without printing the initial phase guidance, does not invoke -DmsOnly start, and prints IDE guidance" {
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
            $log | Should -Contain "start-infra DmsBaseUrl= rebuild=False writerGuidanceSuppressed=True"

            # -DmsOnly start must NOT appear
            $log | Where-Object { $_ -like "start-dms*" } | Should -BeNullOrEmpty

            # seed must NOT be invoked (no -LoadSeedData)
            $log | Where-Object { $_ -like "seed*" } | Should -BeNullOrEmpty

            # IDE guidance must mention appsettings or DmsBaseUrl workflow hint
            $output | Should -Match "(?i)(appsettings|DmsBaseUrl|IDE)"
            $output | Should -Not -Match "Infrastructure phase complete\. DMS service was not started\." -Because "the wrapper has not yet configured or provisioned the data store when its initial infrastructure phase returns"

            # AC: terminal output must not present a second start-local-dms.ps1 run as a resume
            # mechanism; the fresh wrapper continuation invocation is the supported follow-up.
            $output | Should -Not -Match "start-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl" -Because "terminal guidance must not present a second start-local-dms.ps1 run as a resume mechanism (DMS-1153 AC)"

            # The follow-up wrapper hint must carry -NoDataStore: the terminal run already created
            # the data store, and a plain wrapper re-run creates a duplicate (verified live).
            $output | Should -Match "bootstrap-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl\s+<url>\s+-NoDataStore" -Because "the continuation hint must reuse the data store the terminal run created, not duplicate it"

            # Shared mode must not advertise a topology switch it was not started with.
            $output | Should -Not -Match "-NoDataStore\s+-SeparateConfigDatabase" -Because "the hint records the topology this run actually used"
        }

        It "carries -SeparateConfigDatabase on the continuation hint when the run is separate-topology" {
            # A fresh wrapper run recomposes the environment from its own switches, so a hint that
            # drops the declaration continues a separate-mode stack in SHARED mode and undoes the
            # operator's selection.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-sep-guidance.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -SeparateConfigDatabase `
                *>&1 | Out-String

            $output | Should -Match "bootstrap-local-dms\.ps1\s+-InfraOnly\s+-DmsBaseUrl\s+<url>\s+-NoDataStore\b[^\r\n]*\s-SeparateConfigDatabase" -Because "the continuation must reuse the data store AND keep the topology the stack was started with"
        }

        It "forwards -DatabaseEngine to the provision phase (DMS-1270 Phase 1b)" {
            # Invoke-BootstrapWrapper builds $provisionArgs by hand rather than splatting
            # $PSBoundParameters, and originally omitted DatabaseEngine - so an mssql wrapper run
            # provisioned the DMS relational schema with the provision script's postgresql default.
            # Asserted through the recording stub's own parameter binding, so the value has to
            # survive the whole wrapper -> phase-script boundary rather than merely appear in a
            # hashtable.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-engine.txt"
            $engineLog = Join-Path $script:repo.RepoRoot "engine-log.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -EngineLogPath $engineLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DatabaseEngine mssql `
                *>&1 | Out-Null

            @(Get-Content -LiteralPath $callLog) | Should -Contain "provision"
            @(Get-Content -LiteralPath $engineLog) | Should -Contain "provision-engine=mssql" -Because "the provision phase must receive the engine the wrapper was invoked with, not its own default"
        }

        It "forwards the default -DatabaseEngine to the provision phase" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-engine-default.txt"
            $engineLog = Join-Path $script:repo.RepoRoot "engine-log-default.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -EngineLogPath $engineLog | Out-Null

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly *>&1 | Out-Null

            @(Get-Content -LiteralPath $engineLog) | Should -Contain "provision-engine=postgresql"
        }

        # DMS-1270 Phase 3: -SeparateConfigDatabase is forwarded unchanged from the public wrapper to
        # the start script, on both engines. Asserted through the recording stub's own parameter
        # binding, so the switch has to survive the wrapper -> start-script boundary rather than
        # merely appear in a hashtable.
        It "forwards -SeparateConfigDatabase to the start script for <_>" -ForEach @('postgresql', 'mssql') {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-sep-$_.txt"
            $forwardLog = Join-Path $script:repo.RepoRoot "forward-log-sep-$_.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -ForwardLogPath $forwardLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DatabaseEngine $_ `
                -SeparateConfigDatabase `
                *>&1 | Out-Null

            $forwarded = @(Get-Content -LiteralPath $forwardLog)
            $forwarded | Should -Contain "start-infra engine=$_ separate=True" -Because "the start script must receive both the engine and the topology switch the wrapper was invoked with"
        }

        It "does not forward -SeparateConfigDatabase when it was not requested, for <_>" -ForEach @('postgresql', 'mssql') {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-nosep-$_.txt"
            $forwardLog = Join-Path $script:repo.RepoRoot "forward-log-nosep-$_.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -ForwardLogPath $forwardLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DatabaseEngine $_ `
                *>&1 | Out-Null

            @(Get-Content -LiteralPath $forwardLog) | Should -Contain "start-infra engine=$_ separate=False" -Because "shared mode must remain the default all the way through the wrapper"
        }

        It "runs configure and provision to completion with -SeparateConfigDatabase requested" {
            # Both datastore phases receive the switch, because each enforces one half of the same
            # rule: configure judges a name it is about to register (that forwarding is pinned in
            # BootstrapSchemaDeploymentSafety.Tests.ps1, whose stub binds the switch by name), and
            # provision judges the database each selected target resolves to - the only place a
            # REUSED data store's stored connection string is known. What this row adds is that the
            # whole -InfraOnly chain still completes with the switch requested, reaching both phases.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-sep-phases.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            { & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly -SeparateConfigDatabase *>&1 | Out-Null } |
                Should -Not -Throw

            $log = @(Get-Content -LiteralPath $callLog)
            $log | Should -Contain "configure smoke=False"
            $log | Should -Contain "provision"
        }

        It "forwards -SeparateConfigDatabase to the provision phase for <_>" -ForEach @('postgresql', 'mssql') {
            # Asserted through the recording stub's own parameter binding, so the switch has to
            # survive the wrapper -> provision-script boundary rather than merely appear in a
            # hashtable. Without it the phase cannot judge a REUSED data store's stored target and
            # would deploy the DMS schema into the dedicated Configuration Service database.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-prov-sep-$_.txt"
            $forwardLog = Join-Path $script:repo.RepoRoot "forward-log-prov-sep-$_.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -ForwardLogPath $forwardLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DatabaseEngine $_ `
                -SeparateConfigDatabase `
                *>&1 | Out-Null

            @(Get-Content -LiteralPath $forwardLog) | Should -Contain "provision separate=True"
        }

        It "does not forward -SeparateConfigDatabase to the provision phase when it was not requested, for <_>" -ForEach @('postgresql', 'mssql') {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-prov-nosep-$_.txt"
            $forwardLog = Join-Path $script:repo.RepoRoot "forward-log-prov-nosep-$_.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -ForwardLogPath $forwardLog | Out-Null

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                -DatabaseEngine $_ `
                *>&1 | Out-Null

            @(Get-Content -LiteralPath $forwardLog) | Should -Contain "provision separate=False" -Because "shared mode must remain the default all the way through the wrapper"
        }

        It "start-local-dms.ps1 -InfraOnly guidance builds every command it emits from a continuation argument fragment" {
            # The manual continuation is where this run's state cannot be inferred: the topology marker
            # lives in the derived file this start wrote, and the engine and environment file are pure
            # invocation state. Printing part of it on some commands and not others leaves the rest
            # unable to refuse the dedicated Configuration Service database, or continuing on the
            # PostgreSQL default and the default environment. Each emitted command therefore
            # interpolates a fragment rather than carrying its own hand-written switch list, which is
            # what keeps the separate-mode and shared-mode wording from drifting apart; what the
            # fragment actually resolves to is bound, not matched, in the rows below.
            $startContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") -Raw

            $startContent | Should -Match '\$lines\.Add\("  1\. configure-local-data-store\.ps1 \$phaseContinuationArgument'
            $startContent | Should -Match '\$lines\.Add\("  2\. provision-dms-schema\.ps1 \$phaseContinuationArgument'
            $startContent | Should -Match '\$lines\.Add\("  bootstrap-local-dms\.ps1 -InfraOnly -DmsBaseUrl <url> \$wrapperContinuationArgument'
            $startContent | Should -Not -Match '(configure-local-data-store|provision-dms-schema|bootstrap-local-dms)\.ps1[^"\n]* -SeparateConfigDatabase' -Because "no emitted command may hand-write the topology switch alongside the fragment that already carries it"
        }

        It "the start script's own terminal guidance carries this run's full state on every command it emits" {
            # The real production function, lifted from the real file. The phase commands must continue from
            # the COMPOSED environment while the fresh-wrapper command must name the operator's BASE file -
            # the distinction that was wrong when a wrapper-supplied derived path was printed as a base one.
            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $guidance = @(Get-InfraOnlyTerminalGuidance `
                -DatabaseEngine "mssql" `
                -EffectiveEnvironmentFile "/repo/eng/docker-compose/.derived/.env.custom.mssql.topology" `
                -BaseEnvironmentFile "/repo/eng/docker-compose/.env.custom" `
                -DataStandardVersion "6.1" `
                -SeparateConfigDatabase)

            foreach ($scriptName in @("configure-local-data-store.ps1", "provision-dms-schema.ps1")) {
                $line = @($guidance | Where-Object { $_ -match [regex]::Escape($scriptName) }) | Select-Object -First 1
                $line | Should -Not -BeNullOrEmpty
                $argumentText = ($line -split [regex]::Escape($scriptName), 2)[1] -replace '\(.*\)\s*$', ''
                $bound = Invoke-BoundContinuationArgument -RecorderPath $recorder -ArgumentText $argumentText.Trim()

                $bound.DatabaseEngine | Should -Be "mssql"
                $bound.EnvironmentFile | Should -Be "/repo/eng/docker-compose/.derived/.env.custom.mssql.topology" -Because "$scriptName continues from the environment this run already composed"
                $bound.SeparateConfigDatabase | Should -BeTrue
            }

            $wrapperArguments = @(Get-PrintedWrapperContinuationArgumentList -Line $guidance)
            $wrapperArguments.Count | Should -Be 1 -Because "exactly one fresh-wrapper command may be advertised"
            $boundWrapper = Invoke-BoundContinuationArgument -RecorderPath $recorder -ArgumentText $wrapperArguments[0]
            $boundWrapper.DatabaseEngine | Should -Be "mssql"
            $boundWrapper.EnvironmentFile | Should -Be "/repo/eng/docker-compose/.env.custom" -Because "a fresh wrapper run recomposes its overlays from the operator's base file"
            $boundWrapper.DataStandardVersion | Should -Be "6.1" -Because "the wrapper always recomposes a data-standard overlay and would otherwise fall back to the default"
            $boundWrapper.SeparateConfigDatabase | Should -BeTrue
        }

        It "the start-script fresh-wrapper command carries the RESOLVED identity provider, and the phase commands do not: <Name>" -ForEach @(
            @{ Name = "separate topology, explicit keycloak over a self-contained environment"; Provider = "keycloak"; Separate = $true }
            @{ Name = "shared topology, explicit keycloak over a self-contained environment"; Provider = "keycloak"; Separate = $false }
            @{ Name = "shared topology, resolved self-contained"; Provider = "self-contained"; Separate = $false }
        ) {
            # An explicit -IdentityProvider outranks the environment file's own
            # CONFIGURATION_SERVICE_IDENTITY_PROVIDER, so a keycloak run whose environment says self-contained would
            # advertise a continuation that silently switches providers. The environment file named here
            # deliberately represents the self-contained value, so the provider can only have come from
            # this run's resolution.
            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $guidance = @(Get-InfraOnlyTerminalGuidance `
                -DatabaseEngine "mssql" `
                -EffectiveEnvironmentFile "/repo/eng/docker-compose/.derived/.env.self-contained.mssql" `
                -BaseEnvironmentFile "/repo/eng/docker-compose/.env.self-contained" `
                -DataStandardVersion "6.1" `
                -IdentityProvider $Provider `
                -SeparateConfigDatabase:$Separate)

            $wrapperArguments = @(Get-PrintedWrapperContinuationArgumentList -Line $guidance)
            $wrapperArguments.Count | Should -Be 1 -Because "exactly one fresh-wrapper command may be advertised"
            ([regex]::Matches($wrapperArguments[0], '-IdentityProvider\b')).Count |
                Should -Be 1 -Because "the resolved provider appears exactly once, not zero times and not duplicated"

            $bound = Invoke-BoundContinuationArgument -RecorderPath $recorder -ArgumentText $wrapperArguments[0]
            $bound.IdentityProvider | Should -Be $Provider -Because "following the hint must keep the provider this run resolved"
            $bound.DatabaseEngine | Should -Be "mssql" -Because "the other preserved state is unaffected"
            $bound.EnvironmentFile | Should -Be "/repo/eng/docker-compose/.env.self-contained"
            $bound.DataStandardVersion | Should -Be "6.1"
            $bound.SeparateConfigDatabase | Should -Be $Separate

            # configure-local-data-store.ps1 and provision-dms-schema.ps1 do not declare the parameter,
            # so a hint that offered it would not bind at all.
            foreach ($scriptName in @("configure-local-data-store.ps1", "provision-dms-schema.ps1")) {
                $line = @($guidance | Where-Object { $_ -match [regex]::Escape($scriptName) }) | Select-Object -First 1
                $line | Should -Not -BeNullOrEmpty
                $line | Should -Not -Match '-IdentityProvider' -Because "$scriptName does not accept -IdentityProvider"
            }
        }

        It "neither phase script declares -IdentityProvider, so the phase commands must never offer it" {
            # The reason the previous row's negative assertion matters, stated against the real parameter
            # surfaces rather than assumed.
            foreach ($scriptName in @("configure-local-data-store.ps1", "provision-dms-schema.ps1")) {
                (Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot $scriptName)) |
                    Should -Not -Contain "IdentityProvider" -Because "$scriptName has no such parameter to bind"
            }
            # And the command that DOES receive it accepts it.
            (Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1")) |
                Should -Contain "IdentityProvider"
        }

        It "the wrapper's own terminal continuation carries the resolved identity provider exactly once" {
            # The wrapper-owned half: an explicit -IdentityProvider is resolved by the wrapper and must
            # travel on the single hint it prints, over an environment file whose own value differs.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-identity-hint.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            # The fixture env declares self-contained; the run explicitly requests keycloak.
            $customEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.custom-identity-hint"
            Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $customEnvFile -Encoding utf8
            @(Get-Content -LiteralPath $customEnvFile) |
                Should -Contain "CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained" -Because "the conflict this row is about must actually exist in the environment"

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $customEnvFile `
                -InfraOnly `
                -DatabaseEngine mssql `
                -IdentityProvider keycloak `
                -SeparateConfigDatabase `
                *>&1 | Out-String

            $argumentText = Get-PrintedWrapperContinuationArgument -Output $output
            ([regex]::Matches($argumentText, '-IdentityProvider\b')).Count | Should -Be 1

            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $bound = Invoke-BoundContinuationArgument -RecorderPath $recorder -ArgumentText $argumentText
            $bound.IdentityProvider | Should -Be "keycloak" -Because "the wrapper resolved keycloak, not the environment file's self-contained"
            $bound.DatabaseEngine | Should -Be "mssql"
            $bound.EnvironmentFile | Should -Be $customEnvFile
            $bound.SeparateConfigDatabase | Should -BeTrue
            $bound.NoDataStore | Should -BeTrue
        }

        It "the wrapper's terminal continuation carries the environment-derived provider when none is explicit" {
            # Shared-topology control with no explicit provider: the hint still names what the run
            # resolved, so following it cannot drift to a different default.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-identity-hint-default.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -InfraOnly `
                *>&1 | Out-String

            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $bound = Invoke-BoundContinuationArgument `
                -RecorderPath $recorder `
                -ArgumentText (Get-PrintedWrapperContinuationArgument -Output $output)

            $bound.IdentityProvider | Should -Be "self-contained" -Because "the environment file's own value is what this run resolved"
            $bound.SeparateConfigDatabase | Should -BeFalse
        }

        It "the start script emits NO fresh-wrapper command when the wrapper owns the workflow, but keeps the phase commands" {
            # Inside a wrapper-owned run this script is handed an already-derived -EnvironmentFile and is
            # deliberately not given the wrapper's -DataStandardVersion, so it cannot describe the input a
            # fresh wrapper run would compose from. The wrapper prints that command instead.
            $guidance = @(Get-InfraOnlyTerminalGuidance `
                -DatabaseEngine "mssql" `
                -EffectiveEnvironmentFile "/repo/eng/docker-compose/.derived/.env.custom.mssql.topology" `
                -BaseEnvironmentFile "/repo/eng/docker-compose/.derived/.env.custom.mssql.topology" `
                -SeparateConfigDatabase `
                -SuppressWrapperContinuationGuidance)

            @(Get-PrintedWrapperContinuationArgumentList -Line $guidance).Count |
                Should -Be 0 -Because "the wrapper owns this hint and holds the state needed to build it"
            $guidance | Should -Contain "Infrastructure phase complete. DMS service was not started." -Because "direct InfraOnly guidance remains available for manual phase execution"
            $guidance -join "`n" | Should -Not -Match "wrapper-managed health-wait" -Because "the preamble must go with the command it introduces"
            $guidance -join "`n" | Should -Match "configure-local-data-store\.ps1" -Because "the manual phase next-steps are unaffected"
            $guidance -join "`n" | Should -Match "provision-dms-schema\.ps1"
        }

        It "a wrapper run with a custom base env, a non-default data standard, MSSQL and separate topology advertises exactly one continuation, bound to that state" {
            # End to end over the pair that produced the contradiction: the wrapper suppresses the start
            # script's hint (recorded from the real forwarded argument) and prints its own, and the whole
            # transcript is then searched for EVERY bootstrap-local-dms.ps1 continuation. More than one, or
            # one that binds to a derived path / the default data standard, fails here.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-single-hint.txt"
            $suppressLog = Join-Path $script:repo.RepoRoot "suppress-log.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            # The recording start script accepts remaining arguments; record whether the suppression switch
            # actually arrived, so the wrapper's half of the contract is asserted and not assumed.
            $startScriptPath = Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1"
            Add-Content -LiteralPath $startScriptPath -Value "Add-Content -LiteralPath '$suppressLog' -Value `"suppress=`$(`$Rest -contains '-SuppressWrapperContinuationGuidance')`""

            $customEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.custom-single-hint"
            Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $customEnvFile -Encoding utf8

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $customEnvFile `
                -InfraOnly `
                -DatabaseEngine mssql `
                -DataStandardVersion "6.1" `
                -SeparateConfigDatabase `
                *>&1 | Out-String

            @(Get-Content -LiteralPath $suppressLog) |
                Should -Contain "suppress=True" -Because "the wrapper must tell the start script it owns the fresh-wrapper hint"

            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            # Throws when the transcript carries zero or more than one continuation command.
            $bound = Invoke-BoundContinuationArgument `
                -RecorderPath $recorder `
                -ArgumentText (Get-PrintedWrapperContinuationArgument -Output $output)

            $bound.DatabaseEngine | Should -Be "mssql"
            $bound.EnvironmentFile | Should -Be $customEnvFile -Because "the surviving hint names the caller-supplied base file, not a derived one"
            $bound.DataStandardVersion | Should -Be "6.1" -Because "the selected data standard must survive into the continuation"
            $bound.SeparateConfigDatabase | Should -BeTrue
            $bound.NoDataStore | Should -BeTrue -Because "the terminal run already created the data store"
        }

        It "the emitted continuation fragment BINDS to this run's engine, environment file, and topology: <Name>" -ForEach @(
            @{ Name = "MSSQL + custom environment + separate topology"; Engine = "mssql"; Separate = $true;  EnvPath = "/tmp/custom/.env.mssql-sep" }
            @{ Name = "MSSQL + custom environment + shared topology";   Engine = "mssql"; Separate = $false; EnvPath = "/tmp/custom/.env.mssql-shared" }
            @{ Name = "PostgreSQL + custom environment";                Engine = "postgresql"; Separate = $false; EnvPath = "/tmp/custom/.env.pg" }
            @{ Name = "environment path containing spaces";             Engine = "mssql"; Separate = $true;  EnvPath = "/tmp/my custom dir/.env file" }
            @{ Name = "environment path containing an apostrophe";      Engine = "mssql"; Separate = $true;  EnvPath = "/tmp/o'brien's env/.env" }
        ) {
            # Substring matching cannot tell a quoted path that survives argument binding from one that
            # splits into two arguments or terminates its own quote, so the fragment is EXECUTED against a
            # parameter block and the bound values are what is asserted.
            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $argumentText = Get-StartScriptContinuationArgument `
                -DatabaseEngine $Engine `
                -EnvironmentFile $EnvPath `
                -SeparateConfigDatabase:$Separate

            $bound = Invoke-BoundContinuationArgument -RecorderPath $recorder -ArgumentText $argumentText

            $bound.DatabaseEngine | Should -Be $Engine -Because "the continuation must run on the engine this run used, not the postgresql default"
            $bound.EnvironmentFile | Should -Be $EnvPath -Because "the path must arrive as ONE argument with its original characters"
            $bound.SeparateConfigDatabase | Should -Be $Separate -Because "the topology declaration travels only when this run declared it"
        }

        It "every parameter the start-script continuation fragment emits exists on both phase scripts it is printed for" {
            # A fragment that binds against a stand-in proves nothing if the real phase scripts do not
            # accept those parameters. Both phases are checked, since the same fragment is printed for
            # each.
            $argumentText = Get-StartScriptContinuationArgument `
                -DatabaseEngine "mssql" `
                -EnvironmentFile "/tmp/.env" `
                -SeparateConfigDatabase

            $emittedParameters = @(
                [regex]::Matches($argumentText, '(?<=\s|^)-(?<Name>[A-Za-z][A-Za-z0-9]*)') |
                    ForEach-Object { $_.Groups["Name"].Value }
            )
            $emittedParameters | Should -Not -BeNullOrEmpty

            foreach ($scriptName in @("configure-local-data-store.ps1", "provision-dms-schema.ps1")) {
                $declared = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot $scriptName)
                foreach ($parameterName in $emittedParameters) {
                    $declared | Should -Contain $parameterName -Because "$scriptName must be able to bind -$parameterName as printed"
                }
            }
        }

        It "the wrapper continuation hint it actually prints BINDS to the run's engine and base environment file, for <_>" -ForEach @('postgresql', 'mssql') {
            # The real emitted line, not a reconstruction: the wrapper is run against a custom
            # environment file and the printed command is lifted out of its own output and bound.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-hint-bind-$_.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $customEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.custom-hint-$_"
            Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $customEnvFile -Encoding utf8

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $customEnvFile `
                -InfraOnly `
                -DatabaseEngine $_ `
                -SeparateConfigDatabase `
                *>&1 | Out-String

            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $bound = Invoke-BoundContinuationArgument `
                -RecorderPath $recorder `
                -ArgumentText (Get-PrintedWrapperContinuationArgument -Output $output)

            $bound.DatabaseEngine | Should -Be $_ -Because "following the hint must recompose the engine this run used"
            $bound.EnvironmentFile | Should -Be $customEnvFile -Because "the fresh run recomposes its overlays from the BASE file this run composed from, not a derived one and not the default"
            $bound.NoDataStore | Should -BeTrue -Because "the terminal run already created the data store"
            $bound.SeparateConfigDatabase | Should -BeTrue
        }

        It "the wrapper continuation hint does not advertise -SeparateConfigDatabase for a shared-mode run, but still binds engine and environment" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-hint-bind-shared.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $customEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.custom-hint-shared"
            Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $customEnvFile -Encoding utf8

            $output = & $script:repo.WrapperScript `
                -EnvironmentFile $customEnvFile `
                -InfraOnly `
                -DatabaseEngine mssql `
                *>&1 | Out-String

            $recorder = New-ContinuationArgumentRecorder -Directory $script:repo.RepoRoot
            $bound = Invoke-BoundContinuationArgument `
                -RecorderPath $recorder `
                -ArgumentText (Get-PrintedWrapperContinuationArgument -Output $output)

            $bound.SeparateConfigDatabase | Should -BeFalse -Because "the hint records the topology this run actually used"
            $bound.DatabaseEngine | Should -Be "mssql" -Because "shared mode carries the engine too"
            $bound.EnvironmentFile | Should -Be $customEnvFile
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
            $startLine | Should -Match "DmsBaseUrl= rebuild="
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

        Context "with -IncludeE2EClaimSets" {
            BeforeEach {
                # The complete manifest models a workspace a previous run staged; the recording claims
                # script stands in for prepare-dms-claims.ps1, whose own fingerprint rejection of a
                # workspace staged without the switch is covered by BootstrapSchemaAndSecuritySelection.
                New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
                $script:callLog = Join-Path $script:repo.RepoRoot "call-log-e2e-claims.txt"
                New-RecordingPrepareScripts -Directory $script:repo.DockerComposeRoot -CallLogPath $script:callLog
                New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $script:callLog | Out-Null
                New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $script:callLog | Out-Null
                New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $script:callLog | Out-Null
                Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

                function script:Set-RecordingE2EPrepareClaimsScript {
                    param([string]$FailureMessage)

                    $failure = if ($FailureMessage) { "if (`$IncludeE2EClaimSets) { throw '$FailureMessage' }" } else { "" }
                    @"
param([switch] `$IncludeE2EClaimSets, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$($script:callLog)' -Value "prepare-claims IncludeE2EClaimSets=`$IncludeE2EClaimSets"
$failure
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "prepare-dms-claims.ps1") -Encoding utf8
                }
            }

            It "reruns claims staging with the switch even though the manifest already carries claims" {
                Set-RecordingE2EPrepareClaimsScript

                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnvironmentFile $script:repo.EnvFile -InfraOnly -IncludeE2EClaimSets

                $log = @(Get-Content -LiteralPath $script:callLog)
                $log | Should -Contain "prepare-claims IncludeE2EClaimSets=True"
                $claimsIndex = [array]::IndexOf($log, "prepare-claims IncludeE2EClaimSets=True")
                $startIndex = [array]::IndexOf($log, ($log | Where-Object { $_ -like "start-infra*" } | Select-Object -First 1))
                $startIndex | Should -BeGreaterThan $claimsIndex -Because "the matching E2E workspace is reused and startup proceeds"
            }

            It "throws the workspace-mismatch error before any start script runs when the staged workspace lacks the E2E claim sets" {
                Set-RecordingE2EPrepareClaimsScript -FailureMessage "Existing staged bootstrap workspace differs from requested inputs. Diverging field: claims fingerprint mismatch."

                { Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnvironmentFile $script:repo.EnvFile -InfraOnly -IncludeE2EClaimSets } |
                    Should -Throw -ExpectedMessage "*claims fingerprint mismatch*"

                $log = @(Get-Content -LiteralPath $script:callLog)
                $log | Should -Contain "prepare-claims IncludeE2EClaimSets=True"
                @($log | Where-Object { $_ -like "start-*" }) | Should -BeNullOrEmpty -Because "the mismatch must surface before infrastructure starts"
            }

            It "keeps the skip-when-staged behavior without the switch" {
                Set-RecordingE2EPrepareClaimsScript

                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnvironmentFile $script:repo.EnvFile -InfraOnly

                $log = @(Get-Content -LiteralPath $script:callLog)
                @($log | Where-Object { $_ -like "prepare-claims*" }) | Should -BeNullOrEmpty
                $log | Where-Object { $_ -like "start-infra*" } | Should -Not -BeNullOrEmpty
            }

            It "forwards no switch to prepare-dms-claims.ps1 when staging a fresh workspace without it" {
                Remove-Item -LiteralPath (Join-Path $script:repo.DockerComposeRoot ".bootstrap") -Recurse -Force
                Set-RecordingE2EPrepareClaimsScript

                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnvironmentFile $script:repo.EnvFile -InfraOnly

                @(Get-Content -LiteralPath $script:callLog) | Should -Contain "prepare-claims IncludeE2EClaimSets=False"
            }
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
            $log[0] | Should -Match "DmsBaseUrl= rebuild="

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
    # DMS-1292 - -AddSmokeTestCredentials delivery. The CMS returns the smoke application's secret
    #   once; configure returns it in its structured result, and the wrapper must print the
    #   operator summary exactly once at the end of every run that created it - including runs a
    #   later phase fails - on the success stream only.
    # =========================================================================
    Context "smoke-test credential delivery (DMS-1292)" {
        BeforeAll {
            $script:syntheticSmokeKey = 'SYNTH-KEY-1292'
            $script:syntheticSmokeSecret = 'SYNTH$SECRET-1292'
            $script:smokeSummaryHeader = "--- Smoke test credentials ---"
            $script:smokeIncompleteHeader = "--- Smoke test credentials (created before the failure; the stack is NOT ready) ---"
            $script:smokePlaceholderLine = "Replace <dms-base-url> with the IDE-hosted DMS URL once it is healthy."

            # Runs an entry script with every stream merged, collecting items as they arrive so the
            # output written before a terminating error (the summary) is kept along with the error.
            function script:Invoke-WrapperCapture {
                param([string]$ScriptPath, [hashtable]$Arguments)

                $collected = [System.Collections.Generic.List[object]]::new()
                $caught = $null
                try {
                    & $ScriptPath @Arguments *>&1 | ForEach-Object { $collected.Add($_) }
                }
                catch {
                    $caught = $_
                }

                $items = $collected.ToArray()
                return [pscustomobject]@{
                    Items = $items
                    Strings = @($items | Where-Object { $_ -is [string] })
                    Records = @($items | Where-Object { $_ -isnot [string] })
                    Error = $caught
                }
            }

            # The summary block: from its header to the end of the success strings. Asserts the header
            # appears exactly once, which also proves nothing follows a second copy.
            function script:Get-SummaryBlock {
                param([string[]]$Strings, [string]$Header)

                $headerIndexes = @(for ($i = 0; $i -lt $Strings.Count; $i++) { if ($Strings[$i] -like "--- Smoke test credentials*") { $i } })
                $headerIndexes.Count | Should -Be 1 -Because "the summary must be printed exactly once"
                $Strings[$headerIndexes[0]] | Should -BeExactly $Header
                return @($Strings[$headerIndexes[0]..($Strings.Count - 1)])
            }

            # Sets or removes the ambient DMS_MULTI_TENANCY. A $null passed to SetEnvironmentVariable
            # arrives as an empty string, which leaves the variable defined and, under Compose
            # precedence, overrides the env file; removal restores the unset state exactly.
            function script:Set-AmbientDmsMultiTenancy {
                param([AllowNull()]$Value)

                if ($null -eq $Value) {
                    Remove-Item -Path Env:DMS_MULTI_TENANCY -ErrorAction SilentlyContinue
                }
                else {
                    Set-Item -Path Env:DMS_MULTI_TENANCY -Value $Value
                }
            }

            function script:Get-CommandArgument {
                param([string]$Line)

                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseInput($Line.Trim(), [ref]$tokens, [ref]$errors)
                $errors.Count | Should -Be 0
                $command = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)
                $arguments = @{}
                $elements = @($command.CommandElements)
                for ($i = 1; $i -lt $elements.Count - 1; $i++) {
                    if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst]) {
                        $elements[$i + 1].StringConstantType | Should -Be 'SingleQuoted'
                        $arguments[$elements[$i].ParameterName] = $elements[$i + 1].Value
                    }
                }
                return $arguments
            }

            function script:Assert-NoCredentialLeak {
                param([object[]]$Records, [hashtable]$FilesBefore)

                foreach ($record in $Records) {
                    $text = $record | Out-String
                    $text | Should -Not -Match ([regex]::Escape($script:syntheticSmokeKey))
                    $text | Should -Not -Match ([regex]::Escape($script:syntheticSmokeSecret))
                }

                # Files this run created or modified; the staged configure stand-in embeds the synthetic
                # credential by design and is excluded by being in the baseline. -Force enumerates the
                # dot-prefixed .bootstrap tree on Linux.
                $changed = @(
                    Get-ChildItem -LiteralPath $script:repo.RepoRoot -Recurse -File -Force |
                        Where-Object { -not $FilesBefore.ContainsKey($_.FullName) -or $FilesBefore[$_.FullName] -ne (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
                )
                # This fixture has no bootstrap-manifest.psm1, so the run's effective env is the composed
                # data-standard overlay under the dot-prefixed .derived directory rather than
                # .bootstrap/.env.derived.
                @($changed | Where-Object { $_.Directory.Name -eq ".derived" }).Count | Should -BeGreaterThan 0 -Because "the run's derived env files must be among the scanned files, or the scan is vacuous"
                foreach ($file in $changed) {
                    Select-String -LiteralPath $file.FullName -SimpleMatch -Pattern $script:syntheticSmokeKey, $script:syntheticSmokeSecret |
                        Should -BeNullOrEmpty -Because "'$($file.FullName)' was written by this run"
                }
            }

            function script:Get-RepoFileSnapshot {
                $snapshot = @{}
                foreach ($file in @(Get-ChildItem -LiteralPath $script:repo.RepoRoot -Recurse -File -Force)) {
                    $snapshot[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                }
                return $snapshot
            }

            # A phase script that records its call and then fails with a specific exception type, so the
            # error the wrapper rethrows can be compared by identity with a no-flag control run.
            function script:New-FailingPhaseScript {
                param([string]$FileName, [string]$CallLogPath, [string]$Message, [switch]$OnlyWhenDmsOnly)

                $condition = if ($OnlyWhenDmsOnly) { "`$PSBoundParameters.ContainsKey('DmsOnly')" } else { "`$true" }
                @"
param([switch] `$DmsOnly, [switch] `$InfraOnly, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$CallLogPath' -Value "$FileName dmsOnly=`$DmsOnly"
if ($condition) { throw [System.IO.InvalidDataException]::new('$Message') }
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot $FileName) -Encoding utf8
            }
        }

        It "prints the summary once, last, with the Docker DMS command, for a <Wrapper> run" -ForEach @(
            @{ Wrapper = "local"; StartScript = "start-local-dms.ps1"; Entry = "WrapperScript" }
            @{ Wrapper = "published"; StartScript = "start-published-dms.ps1"; Entry = "PublishedWrapperScript" }
        ) {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-$Wrapper.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -FileName $StartScript | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            $before = Get-RepoFileSnapshot

            # Verbose and Debug are enabled so a credential written to either stream would be captured.
            $run = Invoke-WrapperCapture -ScriptPath $script:repo.$Entry -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true; Verbose = $true; Debug = $true }

            $run.Error | Should -BeNullOrEmpty
            @(Get-Content -LiteralPath $callLog) | Should -Contain "start-dms DmsBaseUrl= rebuild=False writerGuidanceSuppressed=False"
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader
            $summary | Should -Contain "Key:         SYNTH-KEY-1292"
            $summary | Should -Contain 'Secret:      SYNTH$SECRET-1292'
            $setLocation = Get-CommandArgument -Line ($summary | Where-Object { $_ -like "*Set-Location*" })
            $setLocation["LiteralPath"] | Should -BeExactly ([System.IO.Path]::GetFullPath((Join-Path $script:repo.RepoRoot "eng/smoke_test")))
            $command = Get-CommandArgument -Line ($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" })
            $command["BaseUrl"] | Should -BeExactly "http://localhost:18080"
            $command["OAuthUrl"] | Should -BeExactly "http://localhost:18080/oauth/token"
            $command["Key"] | Should -BeExactly $script:syntheticSmokeKey
            $command["Secret"] | Should -BeExactly $script:syntheticSmokeSecret
            $summary[-1] | Should -BeLike "Self-contained identity: set CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=-1*"
            Assert-NoCredentialLeak -Records $run.Records -FilesBefore $before
        }

        It "prints a placeholder summary after the IDE guidance for the -InfraOnly terminal shape" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-infraonly.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            $before = Get-RepoFileSnapshot

            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile; InfraOnly = $true; AddSmokeTestCredentials = $true; Verbose = $true; Debug = $true }

            $run.Error | Should -BeNullOrEmpty
            @(Get-Content -LiteralPath $callLog) | Where-Object { $_ -like "start-dms*" } | Should -BeNullOrEmpty
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader
            $command = Get-CommandArgument -Line ($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" })
            $command["BaseUrl"] | Should -BeExactly "<dms-base-url>"
            $summary | Should -Contain $script:smokePlaceholderLine
            $guidanceIndex = [array]::FindIndex($run.Items, [Predicate[object]] { param($item) "$item" -like "*IDE Workflow: Pre-DMS preparation complete*" })
            $headerIndex = [array]::IndexOf($run.Items, $script:smokeSummaryHeader)
            $guidanceIndex | Should -BeGreaterOrEqual 0
            $headerIndex | Should -BeGreaterThan $guidanceIndex -Because "the summary comes after the IDE next-step guidance"
            Assert-NoCredentialLeak -Records $run.Records -FilesBefore $before
        }

        It "points the summary at the IDE-hosted DMS for -InfraOnly -DmsBaseUrl (LoadSeedData=<LoadSeedData>)" -ForEach @(
            @{ LoadSeedData = $false }, @{ LoadSeedData = $true }
        ) {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-healthwait.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            $arguments = @{ EnvironmentFile = $script:repo.EnvFile; InfraOnly = $true; DmsBaseUrl = "http://localhost:5198"; AddSmokeTestCredentials = $true }
            if ($LoadSeedData) {
                $arguments.LoadSeedData = $true
                $arguments.SeedDataPath = $script:repo.DockerComposeRoot
            }

            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments $arguments

            $run.Error | Should -BeNullOrEmpty
            @(@(Get-Content -LiteralPath $callLog) | Where-Object { $_ -like "seed*" }).Count | Should -Be ([int]$LoadSeedData)
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader
            $command = Get-CommandArgument -Line ($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" })
            $command["BaseUrl"] | Should -BeExactly "http://localhost:5198"
            $command["Secret"] | Should -BeExactly $script:syntheticSmokeSecret
            $summary | Should -Not -Contain $script:smokePlaceholderLine
        }

        It "prints the summary after the seed phase of a Docker-hosted run" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-seed.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$callLog' -Value 'seed'
Write-Output 'seed-phase-output'
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "load-dms-seed-data.ps1") -Encoding utf8

            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile; LoadSeedData = $true; SeedDataPath = $script:repo.DockerComposeRoot; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            $seedIndex = [array]::IndexOf($run.Strings, "seed-phase-output")
            $headerIndex = [array]::IndexOf($run.Strings, $script:smokeSummaryHeader)
            $seedIndex | Should -BeGreaterOrEqual 0
            $headerIndex | Should -BeGreaterThan $seedIndex
            (Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader) | Should -Contain 'Secret:      SYNTH$SECRET-1292'
        }

        It "prints tenant-qualified target lines and no command for a tenant-only result when DMS multi-tenancy is enabled (<Case>)" -ForEach @(
            @{ Case = "env file"; FileLine = "DMS_MULTI_TENANCY=true"; Ambient = $null }
            @{ Case = "quoted env-file value"; FileLine = 'DMS_MULTI_TENANCY="true"'; Ambient = $null }
            @{ Case = "ambient value over env-file false"; FileLine = "DMS_MULTI_TENANCY=false"; Ambient = "true" }
        ) {
            # Compose resolves DMS_MULTI_TENANCY with the ambient value first and strips quotes, so the
            # summary must follow the value the DMS container actually receives.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-tenant.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -Tenant "tenant-a" | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            $dmsMultiTenantEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.dms-multitenant"
            Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $dmsMultiTenantEnvFile -Encoding utf8
            Add-Content -LiteralPath $dmsMultiTenantEnvFile -Value $FileLine

            $savedAmbient = [Environment]::GetEnvironmentVariable("DMS_MULTI_TENANCY")
            try {
                Set-AmbientDmsMultiTenancy -Value $Ambient
                $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $dmsMultiTenantEnvFile; AddSmokeTestCredentials = $true }
            }
            finally {
                Set-AmbientDmsMultiTenancy -Value $savedAmbient
            }

            $run.Error | Should -BeNullOrEmpty
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader
            $summary | Should -Contain "Tenant:      tenant-a"
            $summary | Should -Contain "DMS base:    http://localhost:18080/tenant-a"
            $summary | Should -Contain "Token URL:   http://localhost:18080/tenant-a/oauth/token"
            @($summary | Where-Object { $_ -like "Data store *" -or $_ -like "*Invoke-NonDestructiveApiTests.ps1*" }) | Should -BeNullOrEmpty
        }

        It "prints the unqualified runnable command for a tenant-only result when DMS multi-tenancy is disabled (<Case>)" -ForEach @(
            @{ Case = "unset"; FileLine = $null; Ambient = $null }
            @{ Case = "ambient value over env-file true"; FileLine = "DMS_MULTI_TENANCY=true"; Ambient = "false" }
        ) {
            # The configure result carries the CMS tenant, but DMS registers tenant-prefixed routes only
            # when its own multi-tenancy (DMS_MULTI_TENANCY) is enabled.
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-tenant-dms-single.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -Tenant "tenant-a" | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            $envFile = $script:repo.EnvFile
            if ($FileLine) {
                $envFile = Join-Path $script:repo.DockerComposeRoot ".env.dms-single"
                Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $envFile -Encoding utf8
                Add-Content -LiteralPath $envFile -Value $FileLine
            }

            $savedAmbient = [Environment]::GetEnvironmentVariable("DMS_MULTI_TENANCY")
            try {
                Set-AmbientDmsMultiTenancy -Value $Ambient
                $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $envFile; AddSmokeTestCredentials = $true }
            }
            finally {
                Set-AmbientDmsMultiTenancy -Value $savedAmbient
            }

            $run.Error | Should -BeNullOrEmpty
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader
            $command = Get-CommandArgument -Line ($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" })
            $command["BaseUrl"] | Should -BeExactly "http://localhost:18080"
            $command["OAuthUrl"] | Should -BeExactly "http://localhost:18080/oauth/token"
            @($summary | Where-Object { $_ -like "*tenant-a*" }) | Should -BeNullOrEmpty
        }

        It "prints one target line per school-year data store and no command" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-schoolyear.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -DataStoreIds @(101, 102) -SchoolYearContexts | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeSummaryHeader
            $summary | Should -Contain "Data store 101 (school year 2024): DMS base http://localhost:18080/2024  token http://localhost:18080/2024/oauth/token"
            $summary | Should -Contain "Data store 102 (school year 2025): DMS base http://localhost:18080/2025  token http://localhost:18080/2025/oauth/token"
            @($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" }) | Should -BeNullOrEmpty
        }

        It "prints no summary without the flag, including after a flagged run in the same session" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-unflagged.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $flagged = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true }
            $unflagged = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile }

            $flagged.Error | Should -BeNullOrEmpty
            $unflagged.Error | Should -BeNullOrEmpty
            Get-SummaryBlock -Strings $flagged.Strings -Header $script:smokeSummaryHeader | Out-Null
            @($unflagged.Items | Where-Object { "$_" -like "*Smoke test credentials*" -or "$_" -like "*SYNTH*" }) | Should -BeNullOrEmpty
            @(Get-Content -LiteralPath $callLog) | Where-Object { $_ -like "configure*" } | Should -Be @("configure smoke=True", "configure smoke=False")
        }

        It "stops before provisioning, without a summary, when configure returns no credential" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-contract.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog -OmitSmokeTestCredential | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -Not -BeNullOrEmpty
            $run.Error.Exception.Message | Should -BeExactly "configure-local-data-store.ps1 was run with -AddSmokeTestCredentials but returned no usable SmokeTestCredential; values are withheld."
            $log = @(Get-Content -LiteralPath $callLog)
            $log | Should -Contain "configure smoke=True"
            $log | Should -Not -Contain "provision"
            @($run.Items | Where-Object { "$_" -like "*Smoke test credentials*" }) | Should -BeNullOrEmpty
        }

        It "rejects a summary base URL with control characters before configure creates any credential" {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-bad-base.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null

            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments @{ EnvironmentFile = $script:repo.EnvFile; InfraOnly = $true; DmsBaseUrl = "http://localhost:5198`n"; AddSmokeTestCredentials = $true }

            $run.Error | Should -Not -BeNullOrEmpty
            $run.Error.Exception.Message | Should -BeExactly "The DMS base URL for the -AddSmokeTestCredentials summary contains control characters; the value is withheld."
            Test-Path -LiteralPath $callLog | Should -BeFalse -Because "no phase - in particular not configure, which creates the credential - may run"
            @($run.Items | Where-Object { "$_" -like "*Smoke test credentials*" -or "$_" -like "*SmokeTestCredential*" }) | Should -BeNullOrEmpty
        }

        It "prints the incomplete summary and rethrows the original error when <Phase> fails later" -ForEach @(
            @{ Phase = "provision"; FileName = "provision-dms-schema.ps1"; DmsOnly = $false; Seed = $false }
            @{ Phase = "DMS startup"; FileName = "start-local-dms.ps1"; DmsOnly = $true; Seed = $false }
            @{ Phase = "seed"; FileName = "load-dms-seed-data.ps1"; DmsOnly = $false; Seed = $true }
        ) {
            New-BootstrapManifestFile -DockerComposeRoot $script:repo.DockerComposeRoot | Out-Null
            $callLog = Join-Path $script:repo.RepoRoot "call-log-smoke-failure.txt"
            New-RecordingStartScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingConfigureScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingProvisionScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-RecordingSeedScript -Directory $script:repo.DockerComposeRoot -CallLogPath $callLog | Out-Null
            New-FailingPhaseScript -FileName $FileName -CallLogPath $callLog -Message "$Phase boom" -OnlyWhenDmsOnly:$DmsOnly
            $arguments = @{ EnvironmentFile = $script:repo.EnvFile; Verbose = $true; Debug = $true }
            if ($Seed) {
                $arguments.LoadSeedData = $true
                $arguments.SeedDataPath = $script:repo.DockerComposeRoot
            }

            # Baseline before both runs: the control run writes the derived env file first, and the
            # flagged run may rewrite it with identical content.
            $before = Get-RepoFileSnapshot
            $control = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments $arguments
            $flaggedArguments = $arguments.Clone()
            $flaggedArguments.AddSmokeTestCredentials = $true
            $run = Invoke-WrapperCapture -ScriptPath $script:repo.WrapperScript -Arguments $flaggedArguments

            $control.Error | Should -Not -BeNullOrEmpty
            @($control.Items | Where-Object { "$_" -like "*Smoke test credentials*" }) | Should -BeNullOrEmpty
            $run.Error | Should -Not -BeNullOrEmpty
            $run.Error.Exception | Should -BeOfType [System.IO.InvalidDataException]
            $run.Error.Exception.Message | Should -BeExactly "$Phase boom"
            $run.Error.FullyQualifiedErrorId | Should -BeExactly $control.Error.FullyQualifiedErrorId
            $run.Error.Exception.GetType() | Should -Be $control.Error.Exception.GetType()
            $summary = Get-SummaryBlock -Strings $run.Strings -Header $script:smokeIncompleteHeader
            $summary | Should -Contain "Key:         SYNTH-KEY-1292"
            $summary | Should -Contain 'Secret:      SYNTH$SECRET-1292'
            @($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" }) | Should -BeNullOrEmpty
            # The error itself must not carry the credential.
            ($run.Error | Out-String) + $run.Error.Exception.ToString() | Should -Not -Match 'SYNTH'
            Assert-NoCredentialLeak -Records $run.Records -FilesBefore $before
        }
    }

    # =========================================================================
    # DMS-1292 - direct start-published-dms.ps1 -AddSmokeTestCredentials. Runs the REAL start script
    #   with docker, Start-Sleep and the identity setup script stubbed and the Configuration Service
    #   calls answered by a recording Dms-Management.psm1 stand-in, so the real SmokeTest.psm1 creates
    #   and formats the credential. The application must be created after the data stores, for the
    #   data stores this run registered, in the configured tenant, and its summary printed.
    # =========================================================================
    Context "direct published start smoke-test credentials (DMS-1292)" {
        BeforeEach {
            foreach ($fileName in @('start-published-dms.ps1', 'bootstrap-manifest.psm1', 'bootstrap-claims-gate.psm1')) {
                Copy-DockerComposeFile -FileName $fileName -Destination $script:repo.DockerComposeRoot
            }
            $script:cmsCallLog = Join-Path $script:repo.RepoRoot "cms-calls.txt"
            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$($script:cmsCallLog)' -Value 'setup-openiddict'
`$global:LASTEXITCODE = 0
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "setup-openiddict.ps1") -Encoding utf8

            # Configuration Service stand-in, replacing the staged Dms-Management.psm1 that both the start
            # script and SmokeTest.psm1 import. It records what each call received - never a credential -
            # and fails data-store creation when the fail-datastore marker exists.
            @"
function Write-StubCall { param([string]`$Line) Add-Content -LiteralPath '$($script:cmsCallLog)' -Value `$Line }
function Add-CmsClient { param(`$CmsUrl, `$ClientId) Write-StubCall "Add-CmsClient `$ClientId" }
function Get-CmsToken { 'token' }
function Add-Tenant { param(`$CmsUrl, `$AccessToken, `$TenantName) Write-StubCall "Add-Tenant `$TenantName"; [long]1 }
function ConvertTo-PostgresCredential { param(`$UserName, `$Secret) [pscredential]::new(`$UserName, (ConvertTo-SecureString 'stub' -AsPlainText -Force)) }
function New-DataStoreConnectionString { 'stub-connection-string' }
function Add-DataStore {
    param(`$CmsUrl, `$AccessToken, `$Name, `$Tenant)
    Write-StubCall "Add-DataStore tenant=`$Tenant"
    if (Test-Path -LiteralPath '$(Join-Path $script:repo.RepoRoot "fail-datastore")') { throw 'data store rejected' }
    [long]303
}
function Add-DmsSchoolYearInstances {
    param(`$CmsUrl, `$AccessToken, `$StartYear, `$EndYear, `$Tenant)
    Write-StubCall "Add-DmsSchoolYearInstances `$StartYear-`$EndYear tenant=`$Tenant"
    @([pscustomobject]@{ DataStoreId = [long]101; Year = 2024 }, [pscustomobject]@{ DataStoreId = [long]102; Year = 2025 })
}
function Get-DataStore { param(`$CmsUrl, `$AccessToken, `$Tenant) Write-StubCall "Get-DataStore tenant=`$Tenant"; @([pscustomobject]@{ id = [long]77 }) }
function Find-CmsVendorByCompany { `$null }
function Add-Vendor { [long]9 }
function Add-Application {
    param(`$CmsUrl, `$ApplicationName, `$VendorId, `$DataStoreIds, `$Tenant)
    Write-StubCall "Add-Application ids=`$(`$DataStoreIds -join ',') tenant=`$Tenant"
    @{ Id = [long]5; Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH`$SECRET-1292' }
}
Export-ModuleMember -Function *
"@ | Set-Content -LiteralPath (Join-Path $script:repo.RepoRoot "eng/Dms-Management.psm1") -Encoding utf8

            $script:dockerCommands = [Collections.Generic.List[string]]::new()
            $recordedCommands = $script:dockerCommands
            $stub = {
                $recordedCommands.Add((@($args | ForEach-Object { $_ }) -join ' '))
                $global:LASTEXITCODE = 0
                if ($args[0] -eq 'network') { return 'dms' }
            }.GetNewClosure()
            Set-Item function:script:docker -Value $stub
            Mock Start-Sleep { }

            # The start script writes these into the process environment and does not restore them.
            $script:startScriptEnvironmentNames = @('CONFIGURATION_SERVICE_IDENTITY_PROVIDER', 'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY', 'DMS_JWT_METADATA_ADDRESS', 'CONFIGURATION_SERVICE_IDENTITY_AUTHORITY')
            $script:startScriptEnvironment = @{}
            foreach ($name in $script:startScriptEnvironmentNames) {
                $script:startScriptEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
            }

            $script:multiTenantEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.multitenant"
            Get-Content -LiteralPath $script:repo.EnvFile | Set-Content -LiteralPath $script:multiTenantEnvFile -Encoding utf8
            Add-Content -LiteralPath $script:multiTenantEnvFile -Value @("CONFIGURATION_SERVICE_MULTI_TENANCY=true", "DMS_CONFIGURATION_SERVICE_TENANT=tenant-a")
            # The same tenant with DMS multi-tenancy enabled too, so DMS serves tenant-prefixed routes.
            $script:dmsMultiTenantEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.dms-multitenant"
            Get-Content -LiteralPath $script:multiTenantEnvFile | Set-Content -LiteralPath $script:dmsMultiTenantEnvFile -Encoding utf8
            Add-Content -LiteralPath $script:dmsMultiTenantEnvFile -Value "DMS_MULTI_TENANCY=true"

            function script:Invoke-StartPublishedCapture {
                param([hashtable]$Arguments)

                $collected = [System.Collections.Generic.List[object]]::new()
                $caught = $null
                try {
                    & (Join-Path $script:repo.DockerComposeRoot "start-published-dms.ps1") @Arguments *>&1 | ForEach-Object { $collected.Add($_) }
                }
                catch {
                    $caught = $_
                }
                $items = $collected.ToArray()
                return [pscustomobject]@{
                    Strings = @($items | Where-Object { $_ -is [string] })
                    Records = @($items | Where-Object { $_ -isnot [string] })
                    Error = $caught
                    CmsCalls = @(if (Test-Path -LiteralPath $script:cmsCallLog) { Get-Content -LiteralPath $script:cmsCallLog })
                }
            }

            function script:Get-StartPublishedSummary {
                param([string[]]$Strings)

                $headerIndexes = @(for ($i = 0; $i -lt $Strings.Count; $i++) { if ($Strings[$i] -like "--- Smoke test credentials*") { $i } })
                $headerIndexes.Count | Should -Be 1 -Because "the summary must be printed exactly once"
                $headerIndexes[0] | Should -BeGreaterThan 0
                $Strings[$headerIndexes[0] - 1] | Should -BeExactly "Direct start does not provision the DMS schema; the database must already be provisioned (provision-dms-schema.ps1), or use bootstrap-published-dms.ps1."
                return @($Strings[$headerIndexes[0]..($Strings.Count - 1)])
            }
        }

        AfterEach {
            Remove-Item function:script:docker -Force -ErrorAction SilentlyContinue
            foreach ($name in $script:startScriptEnvironmentNames) {
                [Environment]::SetEnvironmentVariable($name, $script:startScriptEnvironment[$name])
            }
        }

        It "creates the credential after the default data store, for that store and the tenant, and prints the tenant summary" {
            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:dmsMultiTenantEnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            $dataStoreIndex = [array]::IndexOf($run.CmsCalls, "Add-DataStore tenant=tenant-a")
            $applicationIndex = [array]::IndexOf($run.CmsCalls, "Add-Application ids=303 tenant=tenant-a")
            $dataStoreIndex | Should -BeGreaterOrEqual 0
            $applicationIndex | Should -BeGreaterThan $dataStoreIndex -Because "the application must be created after the data store it is associated with"
            $summary = Get-StartPublishedSummary -Strings $run.Strings
            $summary | Should -Contain "Key:         SYNTH-KEY-1292"
            $summary | Should -Contain 'Secret:      SYNTH$SECRET-1292'
            $summary | Should -Contain "Tenant:      tenant-a"
            $summary | Should -Contain "DMS base:    http://localhost:18080/tenant-a"
            $summary | Should -Contain "Token URL:   http://localhost:18080/tenant-a/oauth/token"
            @($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" -or $_ -like "Data store *" }) | Should -BeNullOrEmpty
            $run.Strings | Should -Not -Contain "Credential values were returned to the caller and were not written to logs."
        }

        It "keeps the CMS tenant for the credential but prints the unqualified command when DMS multi-tenancy is disabled" {
            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:multiTenantEnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            $run.CmsCalls | Should -Contain "Add-Application ids=303 tenant=tenant-a"
            $summary = Get-StartPublishedSummary -Strings $run.Strings
            $summary | Should -Contain "  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl 'http://localhost:18080' -OAuthUrl 'http://localhost:18080/oauth/token' -Key 'SYNTH-KEY-1292' -Secret 'SYNTH`$SECRET-1292'"
            @($summary | Where-Object { $_ -like "*tenant-a*" }) | Should -BeNullOrEmpty
        }

        It "prints the runnable command for a single-tenant default run" {
            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            $run.CmsCalls | Should -Contain "Add-Application ids=303 tenant="
            $summary = Get-StartPublishedSummary -Strings $run.Strings
            $summary | Should -Contain "  Set-Location -LiteralPath '$([System.IO.Path]::GetFullPath((Join-Path $script:repo.RepoRoot 'eng/smoke_test')))'"
            $summary | Should -Contain "  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl 'http://localhost:18080' -OAuthUrl 'http://localhost:18080/oauth/token' -Key 'SYNTH-KEY-1292' -Secret 'SYNTH`$SECRET-1292'"
            $summary[-1] | Should -BeLike "Self-contained identity:*"
        }

        It "creates the credential for every school-year data store this run registered" {
            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:repo.EnvFile; SchoolYearRange = "2024-2025"; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            $run.CmsCalls | Should -Contain "Add-DmsSchoolYearInstances 2024-2025 tenant="
            $run.CmsCalls | Should -Contain "Add-Application ids=101,102 tenant="
            $summary = Get-StartPublishedSummary -Strings $run.Strings
            $summary | Should -Contain "Data store 101 (school year 2024): DMS base http://localhost:18080/2024  token http://localhost:18080/2024/oauth/token"
            $summary | Should -Contain "Data store 102 (school year 2025): DMS base http://localhost:18080/2025  token http://localhost:18080/2025/oauth/token"
            @($summary | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" }) | Should -BeNullOrEmpty
        }

        It "keeps the tenant-scoped first-data-store fallback with -NoDataStore" {
            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:dmsMultiTenantEnvFile; NoDataStore = $true; AddSmokeTestCredentials = $true }

            $run.Error | Should -BeNullOrEmpty
            @($run.CmsCalls | Where-Object { $_ -like "Add-DataStore*" -or $_ -like "Add-DmsSchoolYearInstances*" }) | Should -BeNullOrEmpty
            $run.CmsCalls | Should -Contain "Get-DataStore tenant=tenant-a"
            $run.CmsCalls | Should -Contain "Add-Application ids=77 tenant=tenant-a"
            (Get-StartPublishedSummary -Strings $run.Strings) | Should -Contain "Tenant:      tenant-a"
        }

        It "creates no credential and prints no summary without the flag" {
            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:repo.EnvFile }

            $run.Error | Should -BeNullOrEmpty
            $run.CmsCalls | Should -Contain "Add-DataStore tenant="
            @($run.CmsCalls | Where-Object { $_ -like "Add-Application*" }) | Should -BeNullOrEmpty
            @($run.Strings | Where-Object { $_ -like "*Smoke test credentials*" -or $_ -like "*SYNTH*" }) | Should -BeNullOrEmpty
        }

        It "rejects a DMS base URL with control characters before creating the credential" {
            $badBaseEnvFile = Join-Path $script:repo.DockerComposeRoot ".env.badbase"
            Get-Content -LiteralPath $script:repo.EnvFile | Where-Object { $_ -notlike "DMS_HTTP_PORTS=*" } | Set-Content -LiteralPath $badBaseEnvFile -Encoding utf8
            Add-Content -LiteralPath $badBaseEnvFile -Value "DMS_HTTP_PORTS=18080$([char]7)"

            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $badBaseEnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -Not -BeNullOrEmpty
            $run.Error.Exception.Message | Should -BeExactly "The DMS base URL for the -AddSmokeTestCredentials summary contains control characters; the value is withheld."
            @($run.CmsCalls | Where-Object { $_ -like "Add-Application*" }) | Should -BeNullOrEmpty
        }

        It "creates no orphan application when data-store registration fails" {
            "" | Set-Content -LiteralPath (Join-Path $script:repo.RepoRoot "fail-datastore")

            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true }

            $run.Error | Should -Not -BeNullOrEmpty
            $run.Error.Exception.Message | Should -BeLike "Failed to create data store(s): *data store rejected*"
            @($run.CmsCalls | Where-Object { $_ -like "Add-Application*" }) | Should -BeNullOrEmpty
        }

        It "keeps the credential off every non-success stream and out of files the run writes" {
            $before = @{}
            foreach ($file in @(Get-ChildItem -LiteralPath $script:repo.RepoRoot -Recurse -File -Force)) {
                $before[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }

            $run = Invoke-StartPublishedCapture -Arguments @{ EnvironmentFile = $script:repo.EnvFile; AddSmokeTestCredentials = $true; Verbose = $true; Debug = $true }

            $run.Error | Should -BeNullOrEmpty
            Get-StartPublishedSummary -Strings $run.Strings | Should -Contain 'Secret:      SYNTH$SECRET-1292'
            $run.Records.Count | Should -BeGreaterThan 0 -Because "the start script's diagnostics must have been captured, or the check proves nothing"
            foreach ($record in $run.Records) {
                $text = $record | Out-String
                $text | Should -Not -Match 'SYNTH-KEY-1292'
                $text | Should -Not -Match ([regex]::Escape('SYNTH$SECRET-1292'))
            }
            $changed = @(
                Get-ChildItem -LiteralPath $script:repo.RepoRoot -Recurse -File -Force |
                    Where-Object { -not $before.ContainsKey($_.FullName) -or $before[$_.FullName] -ne (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
            )
            @($changed | Where-Object { $_.FullName -eq $script:cmsCallLog }).Count | Should -Be 1 -Because "the run's own call log must be among the scanned files, or the scan is vacuous"
            foreach ($file in $changed) {
                Select-String -LiteralPath $file.FullName -SimpleMatch -Pattern 'SYNTH-KEY-1292', 'SYNTH$SECRET-1292' |
                    Should -BeNullOrEmpty -Because "'$($file.FullName)' was written by this run"
            }
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

            # The condition is computed once into $cmsIncludedInComposeSet and shared with the
            # CMS-participation gate, so the two cannot drift; assert the inclusion is gated on that
            # single source and that the source's definition still carries $bootstrapMode. Behavioral
            # coverage of each disjunct lives in CmsDatabaseTopology.Tests.ps1, which runs the script
            # and inspects the compose file set it actually builds.
            $startScript | Should -Match '(?s)if \(\$cmsIncludedInComposeSet\)\s*\{[^}]*?\$files \+= @\("-f", "published-config\.yml"\)' -Because "published bootstrap starts must include the Configuration Service compose file so staged claims mount with DMS ApiSchema"
            $startScript | Should -Match '\$cmsIncludedInComposeSet = [^\r\n]*\$bootstrapMode'
        }

        It "start-published-dms.ps1 keeps non-bootstrap keycloak published-config.yml opt-in behavior" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"
            ) -Raw

            $definitionMatch = [regex]::Match(
                $startScript,
                '\$cmsIncludedInComposeSet = (?<condition>[^\r\n]+)'
            )
            $definitionMatch.Success | Should -BeTrue

            $condition = $definitionMatch.Groups["condition"].Value
            $condition | Should -Be '$EnableConfig -or $InfraOnly -or $IdentityProvider -eq "self-contained" -or $bootstrapMode -or $SeparateConfigDatabase'
            $condition | Should -Not -Match "keycloak" -Because "non-bootstrap keycloak published starts remain opt-in through -EnableConfig"
        }
    }

    Context "MSSQL datastore engine compose selection and runtime isolation (DMS-1238, DMS-1279)" {
        It "isolates the SQL Server 2025 runtime from legacy SQL Server volumes (DMS-1279)" {
            $mssqlCompose = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "mssql.yml"
            ) -Raw

            $mssqlCompose | Should -Match '(?m)^\s*image:\s*\$\{MSSQL_IMAGE:-mcr\.microsoft\.com/mssql/server:2025-latest\}\s*$'
            $mssqlCompose | Should -Match '(?m)^\s*-\s*dms-mssql-2025:/var/opt/mssql\s*$'
            $mssqlCompose | Should -Not -Match '(?m)^\s*-\s*dms-mssql:/var/opt/mssql\s*$'
            $mssqlCompose | Should -Match '(?m)^  dms-mssql-2025:\s*$'
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

            # On SQL Server the OpenIddict stores live in the CMS database (the shared DMS
            # datastore in shared mode, or the dedicated Configuration Service database in
            # separate mode; created by -InitDb when missing); every invocation must splat the
            # shared engine-aware parameters, tracking the CMS topology seam via
            # CONFIGURATION_SERVICE_DATABASE_NAME rather than always targeting the DMS datastore.
            $startScript | Should -Match 'DbType = "MSSQL"; DbUser = "sa"; DbPort = "ENV:MSSQL_PORT"; DbName = "ENV:CONFIGURATION_SERVICE_DATABASE_NAME"'
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

        It "start-local-dms.ps1 permits MSSQL Kafka UI only through the explicit CDC infrastructure path" {
            $startScript = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            ) -Raw

            $startScript | Should -Match 'if \(\$EnableKafkaUI -and \(\$DatabaseEngine -eq "postgresql" -or \$CdcKafkaInfrastructure\)\)\s*\{[^}]*up \$upArgs kafka-ui'
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
            $wrapperSource | Should -Match '(?s)if \(\$composeDataStandardOverlay -and -not \$UseEnvironmentFileSchemaSettings\)\s*\{.*?Resolve-DataStandardEnvironmentFile'
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

            $ds52Overlay | Should -Match '(?m)^CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=5\.2$'
            $ds61Overlay | Should -Match '(?m)^CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=6\.1$'

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
                    # env-utility.psm1 imports this at module load, so any isolated copy needs it too.
                    "database-safety.psm1",
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
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
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

        # ModuleOwnershipProbe: the whole-file ownership children run exactly this test, because
        # it provably exercises the staged-import lifecycle - New-CompositionProbeRepo creates a
        # recorded workspace, stages env-utility.psm1/database-safety.psm1/bootstrap-wrapper.psm1
        # into it, and executing the staged wrapper imports those modules FROM the staged path.
        It "bootstrap-local-dms.ps1 composes the overlay by default, overriding a custom base SCHEMA_PACKAGES" -Tag "ModuleOwnershipProbe" {
            $script:compositionProbeRepo = New-CompositionProbeRepo `
                -WrapperEntryScriptName "bootstrap-local-dms.ps1" `
                -BaseSchemaPackagesValue '[{"name":"Custom.Base.Package","version":"9.9.9"}]'

            & $script:compositionProbeRepo.WrapperScript

            $capturedValues = ReadValuesFromEnvFile $script:compositionProbeRepo.CapturedEnvPath
            $capturedValues["CONFIGURATION_SERVICE_DATA_STANDARD_VERSION"] | Should -Be "5.2"
            $capturedValues["SCHEMA_PACKAGES"] | Should -BeLike "*EdFi.DataStandard52.ApiSchema*"
            $capturedValues["SCHEMA_PACKAGES"] | Should -Not -BeLike "*Custom.Base.Package*" -Because "the local entry point always composes the default overlay over a custom base SCHEMA_PACKAGES"
        }

        It "bootstrap-published-dms.ps1 composes the overlay when -DataStandardVersion is explicitly supplied" {
            $script:compositionProbeRepo = New-CompositionProbeRepo `
                -WrapperEntryScriptName "bootstrap-published-dms.ps1" `
                -BaseSchemaPackagesValue '[{"name":"Custom.Base.Package","version":"9.9.9"}]'

            & $script:compositionProbeRepo.WrapperScript -DataStandardVersion "6.1"

            $capturedValues = ReadValuesFromEnvFile $script:compositionProbeRepo.CapturedEnvPath
            $capturedValues["CONFIGURATION_SERVICE_DATA_STANDARD_VERSION"] | Should -Be "6.1"
            $capturedValues["SCHEMA_PACKAGES"] | Should -BeLike "*EdFi.DataStandard61.ApiSchema*"
            $capturedValues["SCHEMA_PACKAGES"] | Should -Not -BeLike "*Custom.Base.Package*" -Because "an explicit -DataStandardVersion composes the overlay even on the published entry point"
        }

        It "bootstrap-published-dms.ps1 does not compose the overlay and leaves a custom base SCHEMA_PACKAGES untouched when -DataStandardVersion is omitted" {
            $script:compositionProbeRepo = New-CompositionProbeRepo `
                -WrapperEntryScriptName "bootstrap-published-dms.ps1" `
                -BaseSchemaPackagesValue '[{"name":"Custom.Base.Package","version":"9.9.9"}]'

            & $script:compositionProbeRepo.WrapperScript

            $capturedValues = ReadValuesFromEnvFile $script:compositionProbeRepo.CapturedEnvPath
            $capturedValues.ContainsKey("CONFIGURATION_SERVICE_DATA_STANDARD_VERSION") | Should -BeFalse -Because "no overlay was composed, so no overlay-only key was introduced"
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
                    # env-utility.psm1 imports this at module load, so any isolated copy needs it too.
                    "database-safety.psm1",
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
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
DMS_DATASTORE=postgresql
SCHEMA_PACKAGES='[{"version":"1.0.335","name":"EdFi.DataStandard52.ApiSchema"},{"version":"1.0.335","name":"EdFi.DataStandard52.TPDM.ApiSchema"}]'
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
            $capturedValues["CONFIGURATION_SERVICE_DATA_STANDARD_VERSION"] | Should -Be "5.2"
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

        It "bootstrap-published-dms.ps1 declares -d and -v for governed deployment teardown" {
            $params = Get-DeclaredScriptParameters -Path (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            )
            $params | Should -Contain "d"
            $params | Should -Contain "v"
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
                'EnableConfig', 'AddExtensionSecurityMetadata', 'NoDataStore', 'AddSmokeTestCredentials', 'Rebuild',
                # -SeparateConfigDatabase changes which database CMS targets, never which compose
                # files a teardown must cover: local-config.yml is unconditional in
                # start-local-dms.ps1's compose set. So it is excluded, like the other
                # non-compose-shaping options.
                'SeparateConfigDatabase', 'DataStoreDatabaseName'
            )
            $rejectedCdc = @('EnableKafkaCdc', 'CdcSettingsPath', 'CdcBindingStatePath')

            # Completeness guard: every parameter the entry script declares must be classified here
            # as a teardown switch, forwarded, or excluded (and bound below), so a new parameter
            # fails this assertion and forces an explicit forwarding decision.
            $declared = Get-DeclaredScriptParameters -Path $script:repo.WrapperScript
            ($declared | Sort-Object) | Should -Be ((@('d', 'v') + $forwarded + $excluded + $rejectedCdc) | Sort-Object)

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
                -Rebuild `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName ignored_by_teardown `
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
                    # env-utility.psm1 imports this at module load, so any isolated copy needs it too.
                    "database-safety.psm1",
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
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
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
                    [string]$StartScriptName,

                    # The -DbOnly guards are reached with the default fixture because database-only
                    # startup deliberately bypasses bootstrap activation and identity parsing, so the
                    # malformed manifest and invalid identity values below prove that bypass. Shape
                    # guards that do NOT involve -DbOnly (e.g. -InfraOnly with -DmsOnly) sit after
                    # manifest processing and identity resolution, so reaching them needs a fixture
                    # where both succeed: this switch supplies valid identity settings and stages no
                    # bootstrap workspace at all. Every Docker invocation still lies beyond the guard.
                    [switch]$AllowApplicationStartup
                )

                $repoRoot = New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    $StartScriptName,
                    "bootstrap-manifest.psm1",
                    "bootstrap-claims-gate.psm1",
                    "env-utility.psm1",
                    # Both start scripts import the shared Compose-equivalent resolver.
                    "database-safety.psm1"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }

                $envFile = Join-Path $dockerComposeRoot ".env.guard"
                if ($AllowApplicationStartup) {
                    @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8
                }
                else {
                    @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=unsupported-for-db-only
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
# Deliberately invalid application-only settings. DbOnly parameter guards must still be reached,
# proving the database slice does not parse identity configuration before returning.
CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH=not-an-integer
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                    $bootstrapRoot = Join-Path $dockerComposeRoot ".bootstrap"
                    New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
                    Set-Content -LiteralPath (Join-Path $bootstrapRoot "bootstrap-manifest.json") -Value '{ malformed' -NoNewline
                }

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
                $source | Should -Match 'Resolve-DatabaseEngineEnvironmentFile[^\r\n]*-SkipMssqlCmsDatabaseValidation:\(\$databaseOnlyStartup -or \$d -or \$SeparateConfigDatabase\)' -Because "the non-participating call site keeps recording which shapes declined the CMS seam, even though the switch is now a documented no-op"
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
                $source | Should -Match '(?s)\$upArgs\s*=\s*@\("--detach"\).*?if \(-not \$databaseOnlyStartup -and -not \$DmsOnly -and -not \$CdcDatabaseInfrastructure\) \{.*?\$upArgs\s*\+=\s*"--remove-orphans"' -Because "DbOnly, DmsOnly, and managed database/CMS preparation must preserve running peer containers omitted from their reduced compose sets"
            }
        }

        It "start-local-dms.ps1 and start-published-dms.ps1 both reject -DbOnly with -InfraOnly at the param-parse level" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $guardRepo = New-StartScriptGuardRepo -StartScriptName $name
                try {
                    {
                        & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -DbOnly -InfraOnly
                    } | Should -Throw "*-DbOnly is mutually exclusive with -InfraOnly and -DmsOnly*"

                    # DMS-1270: introducing -SeparateConfigDatabase must not weaken or reorder the
                    # existing diagnostic for an invalid shape combination.
                    {
                        & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -DbOnly -InfraOnly -SeparateConfigDatabase
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

                    # DMS-1270: same diagnostic with the topology switch present.
                    {
                        & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -DbOnly -DmsOnly -SeparateConfigDatabase
                    } | Should -Throw "*-DbOnly is mutually exclusive with -InfraOnly and -DmsOnly*"
                }
                finally {
                    Remove-Item -LiteralPath $guardRepo.RepoRoot -Recurse -Force
                }
            }
        }

        It "start-local-dms.ps1 and start-published-dms.ps1 both reject -InfraOnly with -DmsOnly, with and without -SeparateConfigDatabase" {
            # This combination's guard sits after bootstrap activation and identity resolution, so it
            # needs the fixture variant where both succeed. Introducing -SeparateConfigDatabase must
            # not weaken, reorder, or re-word the existing diagnostic for an invalid shape - the switch
            # widens the CMS compose set, which is exactly the kind of change that could plausibly
            # short-circuit ahead of a shape check.
            #
            # Reaching this guard means the identity block above it has already run, and that block
            # publishes identity settings into the AMBIENT process environment for Compose to
            # interpolate. Left behind, those values would silently change the outcome of any later
            # Compose-precedence-sensitive test in the same session, so the whole environment is
            # snapshotted and restored around these invocations. Added variables are removed with
            # Remove-Item rather than set to $null, which on some platforms leaves an empty variable
            # rather than truly unsetting it.
            $environmentBefore = @{}
            foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
                $environmentBefore[[string]$entry.Key] = [string]$entry.Value
            }
            try {
                foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                    $guardRepo = New-StartScriptGuardRepo -StartScriptName $name -AllowApplicationStartup
                    try {
                        {
                            & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -InfraOnly -DmsOnly
                        } | Should -Throw "*-InfraOnly and -DmsOnly are mutually exclusive*"

                        {
                            & $guardRepo.StartScript -EnvironmentFile $guardRepo.EnvFile -InfraOnly -DmsOnly -SeparateConfigDatabase
                        } | Should -Throw "*-InfraOnly and -DmsOnly are mutually exclusive*"
                    }
                    finally {
                        Remove-Item -LiteralPath $guardRepo.RepoRoot -Recurse -Force
                    }
                }
            }
            finally {
                foreach ($currentName in @([System.Environment]::GetEnvironmentVariables().Keys | ForEach-Object { [string]$_ })) {
                    if (-not $environmentBefore.ContainsKey($currentName)) {
                        Remove-Item -LiteralPath "Env:\$currentName" -ErrorAction SilentlyContinue
                    }
                }
                foreach ($entry in $environmentBefore.GetEnumerator()) {
                    if ([System.Environment]::GetEnvironmentVariable($entry.Key) -ne $entry.Value) {
                        [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
                    }
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

            $buildScript | Should -Match 'StartEnvironment \{ Invoke-Step \{ Start-BootstrapDockerEnvironment -UsePublishedImage:\$UsePublishedImage -SkipDockerBuild:\$SkipDockerBuild -LoadSeedData:\$LoadSeedData -DatabaseEngine \$DatabaseEngine -SeparateConfigDatabase:\$SeparateConfigDatabase -IdentityProvider \$IdentityProvider -DataStandardVersion \$DataStandardVersion -DataStandardVersionSupplied:\$dataStandardVersionSupplied \} \}'
        }

        It "Start-BootstrapDockerEnvironment forwards -SeparateConfigDatabase to the bootstrap wrapper only when requested" {
            $buildScript = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "build-dms.ps1"
            ) -Raw

            $buildScript | Should -Match '(?s)if \(\$SeparateConfigDatabase\) \{\s*\$bootstrapArgs\.SeparateConfigDatabase = \$true\s*\}'
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

    Context "public entry-point -SeparateConfigDatabase surface and published-wrapper forwarding (DMS-1270 Phase 3)" {
        BeforeAll {
            # Isolated published-wrapper fixture, modeled on New-CompositionProbeRepo: only the
            # published entry script, the shared wrapper module, and the composition prerequisites
            # are staged, so the wrapper's isolated-fixture degrade path returns right after the
            # single start invocation. The stubbed start-published-dms.ps1 records the engine and
            # -SeparateConfigDatabase state it was bound with, so these tests prove the switch
            # survives the public-wrapper -> start-script boundary rather than merely appearing in
            # a hashtable.
            function script:New-PublishedForwardProbeRepo {
                $repoRoot = New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    "bootstrap-wrapper.psm1",
                    "bootstrap-published-dms.ps1",
                    "env-utility.psm1",
                    "database-safety.psm1",
                    ".env.bootstrap.ds52",
                    ".env.bootstrap.ds61",
                    # A -DatabaseEngine mssql wrapper run composes this engine overlay onto the base
                    # env before any phase command, so the fixture needs it to reach the start stub.
                    ".env.mssql"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }

                $envFile = Join-Path $dockerComposeRoot ".env.example"
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
SCHEMA_PACKAGES='[{"name":"Custom.Base.Package","version":"9.9.9"}]'
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                $forwardLogPath = Join-Path $repoRoot "forward.log"
                @"
param(
    [string] `$EnvironmentFile,
    [string] `$IdentityProvider,
    [string] `$DatabaseEngine,
    [switch] `$SeparateConfigDatabase,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$forwardLogPath' -Value "engine=`$DatabaseEngine separate=`$(`$SeparateConfigDatabase.IsPresent)"
"@ | Set-Content -LiteralPath (Join-Path $dockerComposeRoot "start-published-dms.ps1") -Encoding utf8

                return [pscustomobject]@{
                    RepoRoot       = $repoRoot
                    WrapperScript  = Join-Path $dockerComposeRoot "bootstrap-published-dms.ps1"
                    ForwardLogPath = $forwardLogPath
                }
            }
        }

        AfterEach {
            if ($null -ne $script:publishedForwardRepo -and (Test-Path -LiteralPath $script:publishedForwardRepo.RepoRoot)) {
                Remove-Item -LiteralPath $script:publishedForwardRepo.RepoRoot -Recurse -Force
            }
            $script:publishedForwardRepo = $null
        }

        # The load-bearing surface check for all three public entry points: parses each script's
        # top-level param block from the AST, so removing the public declaration fails here even if
        # internal references to the variable remain (which the text-matching forwarding tests
        # elsewhere would not notice).
        It "declares -SeparateConfigDatabase on its public parameter surface: <_>" -ForEach @(
            'eng/docker-compose/bootstrap-local-dms.ps1',
            'eng/docker-compose/bootstrap-published-dms.ps1',
            'build-dms.ps1'
        ) {
            $params = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceRepoRoot $_)
            $params | Should -Contain "SeparateConfigDatabase" -Because "the switch is part of this entry point's public contract (DMS-1270 Phase 3)"
        }

        It "bootstrap-published-dms.ps1 forwards -SeparateConfigDatabase to the start script for <_>" -ForEach @('postgresql', 'mssql') {
            $script:publishedForwardRepo = New-PublishedForwardProbeRepo

            & $script:publishedForwardRepo.WrapperScript -DatabaseEngine $_ -SeparateConfigDatabase *>&1 | Out-Null

            @(Get-Content -LiteralPath $script:publishedForwardRepo.ForwardLogPath) |
                Should -Contain "engine=$_ separate=True" -Because "the start script must receive both the engine and the topology switch the public wrapper was invoked with"
        }

        It "bootstrap-published-dms.ps1 does not forward -SeparateConfigDatabase when it was not requested, for <_>" -ForEach @('postgresql', 'mssql') {
            $script:publishedForwardRepo = New-PublishedForwardProbeRepo

            & $script:publishedForwardRepo.WrapperScript -DatabaseEngine $_ *>&1 | Out-Null

            @(Get-Content -LiteralPath $script:publishedForwardRepo.ForwardLogPath) |
                Should -Contain "engine=$_ separate=False" -Because "shared mode must remain the default all the way through the public wrapper"
        }
    }

    Context "getting-started local bootstrap guidance (DMS-1427)" {
        BeforeAll {
            $script:gettingStarted = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "GETTING_STARTED.md"
            ) -Raw
        }

        It "documents the normal wrapper and explicit local-image rebuild commands" {
            $script:gettingStarted | Should -Match 'bootstrap-local-dms\.ps1'
            $script:gettingStarted | Should -Match 'bootstrap-local-dms\.ps1\s+-Rebuild'
            $script:gettingStarted | Should -Match '(?is)`-Rebuild`.{0,100}`-r`\s+alias'
        }

        It "documents the Discovery-to-DMS HTTP Basic token flow" {
            $script:gettingStarted | Should -Match '(?i)Discovery'
            $script:gettingStarted | Should -Match '(?i)urls\.oauth'
            $script:gettingStarted | Should -Match '(?i)Authorization:\s*Basic'
            $script:gettingStarted | Should -Match 'grant_type=client_credentials'
            $script:gettingStarted | Should -Match '(?i)not combined with a Basic\s+header'
        }

    }
}

# Unload exactly the module instances staged under the workspaces THIS run created and
# recorded - the roots in $script:ownedWorkspaceRoot - and nothing else. Containment respects
# directory boundaries (exact root, or root plus a separator), so a lookalike sibling such as
# '<owned-root>-other' or a caller's own 'dms-1153-*' directory never matches. The staged
# instances are usually NESTED inside a staged wrapper module, so enumeration must be
# Get-Module -All. Cleanup failure is loud: owned residue fails this suite with the surviving
# paths named, because later suites that bind -ModuleName mocks would otherwise fail on state
# this file left behind.
AfterAll {
    $ownedRoots = @($script:ownedWorkspaceRoot | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    # Windows paths compare case-insensitively; elsewhere they do not.
    $pathComparison = if ($IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
    function Test-PathWithinOwnedRoot {
        param([string]$CandidatePath)
        if ([string]::IsNullOrEmpty($CandidatePath) -or -not [System.IO.Path]::IsPathRooted($CandidatePath)) { return $false }
        $canonical = [System.IO.Path]::GetFullPath($CandidatePath)
        foreach ($root in $ownedRoots) {
            if ([string]::Equals($canonical, $root, $pathComparison)) { return $true }
            if ($canonical.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, $pathComparison)) { return $true }
        }
        return $false
    }
    foreach ($module in @(Get-Module -All)) {
        if (Test-PathWithinOwnedRoot -CandidatePath ([string]$module.Path)) {
            $module | Remove-Module -Force -ErrorAction SilentlyContinue
        }
    }
    $ownedResidue = @(Get-Module -All | Where-Object { Test-PathWithinOwnedRoot -CandidatePath ([string]$_.Path) })
    if ($ownedResidue.Count -gt 0) {
        $residueList = @($ownedResidue | ForEach-Object { "'$($_.Path)'" }) -join ", "
        throw "BootstrapEntryPointWorkflow.Tests.ps1 cleanup: module instances staged under this run's own recorded workspaces survived removal ($residueList). Refusing to hand the session back dirty."
    }
}

Describe "whole-file module-table ownership (post-Invoke-Pester, isolated children)" {
    # Both halves of the exact-ownership invariant, proven AFTER Invoke-Pester returns: owned
    # staged instances are gone, and a caller-owned module beneath a LOOKALIKE-named directory
    # survives untouched. The children exclude this tag, so there is no recursion; launches go
    # through the resolved PowerShell launcher, including dotnet-tool installations.

    BeforeAll {
        $script:ownershipChildWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1153-ownership-child-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:ownershipChildWork -Force | Out-Null
    }

    AfterAll {
        Remove-Item -LiteralPath $script:ownershipChildWork -Recurse -Force -ErrorAction SilentlyContinue
    }

    It "a caller-owned module beneath a lookalike dms-1153-* directory survives the complete file lifecycle" -Tag "WholeFileModuleOwnership" {
        # The review-measured regression this pins: a cleanup that inferred ownership from the
        # directory-name prefix deleted exactly this module while the suite stayed green.
        $childScript = Join-Path $script:ownershipChildWork "lookalike-survival.ps1"
        @(
            "`$callerRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('dms-1153-callerowned-' + [Guid]::NewGuid().ToString('N'))",
            "New-Item -ItemType Directory -Path `$callerRoot -Force | Out-Null",
            "`$callerModulePath = Join-Path `$callerRoot 'CallerOwned.psm1'",
            "Set-Content -LiteralPath `$callerModulePath -Value 'function Get-CallerOwnedSentinel { ''caller-owned'' }'",
            "Import-Module `$callerModulePath -Force",
            "try {",
            "    `$result = Invoke-Pester -Path '$PSCommandPath' -TagFilter 'ModuleOwnershipProbe' -ExcludeTagFilter 'WholeFileModuleOwnership' -Output None -PassThru",
            "    `$survivor = @(Get-Module -Name CallerOwned -All)",
            "    `$command = Get-Command Get-CallerOwnedSentinel -ErrorAction SilentlyContinue",
            "    [pscustomobject]@{",
            "        Failed = `$result.FailedCount",
            "        PassedCount = `$result.PassedCount",
            "        PassedName = @(`$result.Passed | ForEach-Object { `$_.ExpandedPath }) -join ';'",
            "        SurvivorCount = `$survivor.Count",
            "        SurvivorPath = @(`$survivor | ForEach-Object { `$_.Path }) -join ';'",
            "        ExpectedPath = `$callerModulePath",
            "        ExportsSentinel = (`$survivor.Count -eq 1 -and `$survivor[0].ExportedCommands.ContainsKey('Get-CallerOwnedSentinel'))",
            "        SentinelOutput = if (`$command) { Get-CallerOwnedSentinel } else { 'command-gone' }",
            "    } | ConvertTo-Json -Compress",
            "}",
            "finally { Remove-Item -LiteralPath `$callerRoot -Recurse -Force -ErrorAction SilentlyContinue }"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ((Get-Command pwsh -CommandType Application | Select-Object -First 1).Source) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        # Execution proof first: the probe must have RUN and PASSED - discovery counts prove
        # nothing, and a probe that never reached its staged import would make survival vacuous.
        $childState.Failed | Should -Be 0 -Because "the staged-import probe must complete cleanly around the caller's module"
        $childState.PassedCount | Should -Be 1 -Because "exactly the one tagged staged-import probe runs in the child"
        $childState.PassedName | Should -BeLike "*composes the overlay by default, overriding a custom base SCHEMA_PACKAGES*" -Because "the passing test must be the staged-import probe itself"
        $childState.SurvivorCount | Should -Be 1 -Because "a lookalike directory name establishes no ownership; the caller's module is not this file's to remove"
        $childState.SurvivorPath | Should -Be $childState.ExpectedPath -Because "the surviving instance must be the caller's own, at its own path"
        $childState.ExportsSentinel | Should -BeTrue
        $childState.SentinelOutput | Should -Be 'caller-owned'
    }

    It "removes every module instance beneath the exact roots this run created" -Tag "WholeFileModuleOwnership" {
        # The other half: after the run, no NEW module instance rooted under the temp root -
        # where every workspace this run creates lives - may survive. New instances elsewhere
        # (repository modules, engine modules the run loads lazily) are legitimate. The temp
        # filter is a DETECTION oracle in a controlled clean child, never an ownership rule,
        # and the before/after set difference makes this fail even if the in-file cleanup
        # postcondition is deleted outright.
        $childScript = Join-Path $script:ownershipChildWork "own-removal.ps1"
        @(
            "`$before = @{}",
            "foreach (`$m in @(Get-Module -All)) { if (`$m.Path) { `$before[[string]`$m.Path] = `$true } }",
            "`$result = Invoke-Pester -Path '$PSCommandPath' -TagFilter 'ModuleOwnershipProbe' -ExcludeTagFilter 'WholeFileModuleOwnership' -Output None -PassThru",
            "`$comparison = if (`$IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }",
            "`$tempRoot = [System.IO.Path]::GetTempPath()",
            "`$newResidue = @(Get-Module -All | Where-Object {",
            "    `$p = [string]`$_.Path",
            "    `$p -and [System.IO.Path]::IsPathRooted(`$p) -and -not `$before.ContainsKey(`$p) -and",
            "    ([System.IO.Path]::GetFullPath(`$p)).StartsWith(`$tempRoot, `$comparison)",
            "})",
            "[pscustomobject]@{",
            "    Failed = `$result.FailedCount",
            "    PassedCount = `$result.PassedCount",
            "    PassedName = @(`$result.Passed | ForEach-Object { `$_.ExpandedPath }) -join ';'",
            "    ResidueCount = `$newResidue.Count",
            "    ResiduePath = @(`$newResidue | ForEach-Object { `$_.Path }) -join ';'",
            "} | ConvertTo-Json -Compress"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ((Get-Command pwsh -CommandType Application | Select-Object -First 1).Source) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        # Execution proof first: the residue check is meaningful only if the staged-import probe
        # really ran and passed - a run that never imported a staged module has nothing to clean.
        $childState.Failed | Should -Be 0 -Because "the staged-import probe must complete cleanly"
        $childState.PassedCount | Should -Be 1 -Because "exactly the one tagged staged-import probe runs in the child"
        $childState.PassedName | Should -BeLike "*composes the overlay by default, overriding a custom base SCHEMA_PACKAGES*" -Because "the passing test must be the staged-import probe itself"
        $childState.ResidueCount | Should -Be 0 -Because "every instance staged under this run's recorded workspaces must be unloaded before the file hands the session back (residue: $($childState.ResiduePath))"
    }
}
