# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Get-DeclaredScriptParameters matches the helper name the sibling bootstrap suites already use.')]
param()

Describe "DMS-1323 CDC infrastructure opt-in" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:startScriptPath = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
        $script:startScriptText = Get-Content -LiteralPath $script:startScriptPath -Raw

        function script:Get-StartScriptAst {
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $script:startScriptPath, [ref]$null, [ref]$parseErrors
            )

            if (@($parseErrors).Count -gt 0) {
                throw "Failed to parse '$script:startScriptPath': $(@($parseErrors)[0].Message)"
            }

            return $ast
        }

        function script:Get-DeclaredScriptParameters {
            return @(
                (Get-StartScriptAst).ParamBlock.Parameters |
                    ForEach-Object { $_.Name.VariablePath.UserPath } |
                    Select-Object -Unique
            )
        }

        # start-local-dms.ps1 is a straight-line script that starts Docker, so the state-root
        # resolver is lifted out of the real file and exercised directly - the same technique the
        # sibling suites use to reach in-script helpers.
        function script:Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)]
                [string]
                $FunctionName
            )

            $functionAst = (Get-StartScriptAst).FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $FunctionName },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$script:startScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        . ([scriptblock]::Create((Get-ScriptFunctionText -FunctionName "Resolve-CdcBindingStateRoot")))
    }

    Context "parameter surface" {
        It "declares -EnableKafkaCdc and -CdcBindingStatePath" {
            $params = Get-DeclaredScriptParameters

            $params | Should -Contain "EnableKafkaCdc"
            $params | Should -Contain "CdcBindingStatePath"
        }

        It "rejects -CdcBindingStatePath on a run that neither opts into CDC nor tears one down" {
            # The early validation block throws before any module import or Docker activity, so the
            # rejection path is reachable from a real invocation.
            {
                & $script:startScriptPath -CdcBindingStatePath "some-state-root"
            } | Should -Throw "*-CdcBindingStatePath requires -EnableKafkaCdc*"
        }
    }

    Context "binding state store root resolution" {
        It "defaults to the Git-ignored eng/docker-compose/.cdc-state root" {
            $resolved = Resolve-CdcBindingStateRoot `
                -Path "" `
                -DockerComposeRoot $script:sourceDockerComposeRoot `
                -WorkingDirectory ([System.IO.Path]::GetTempPath())

            $resolved | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:sourceDockerComposeRoot ".cdc-state")))

            $ignored = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot ".gitignore") -Raw
            $ignored | Should -Match 'eng/docker-compose/\.cdc-state'
        }

        It "keeps an absolute path the caller supplied" {
            $absolute = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1323-state-root"

            Resolve-CdcBindingStateRoot `
                -Path $absolute `
                -DockerComposeRoot $script:sourceDockerComposeRoot `
                -WorkingDirectory ([System.IO.Path]::GetTempPath()) |
                Should -Be ([System.IO.Path]::GetFullPath($absolute))
        }

        It "resolves a relative path against the caller's working directory, not the compose directory" {
            $workingDirectory = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())

            $resolved = Resolve-CdcBindingStateRoot `
                -Path "cdc-state" `
                -DockerComposeRoot $script:sourceDockerComposeRoot `
                -WorkingDirectory $workingDirectory

            $resolved | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $workingDirectory "cdc-state")))
            $resolved | Should -Not -BeLike "*docker-compose*"
        }
    }

    Context "Kafka and Kafka Connect infrastructure" {
        It "treats -EnableKafkaCdc as an infrastructure opt-in alongside -EnableKafka and -EnableKafkaUI" {
            $script:startScriptText |
                Should -Match '\$enableKafkaInfrastructure = \$EnableKafka -or \$EnableKafkaUI -or \$EnableKafkaCdc'
        }

        It "selects and starts Kafka and Kafka Connect on either database engine" {
            $script:startScriptText |
                Should -Match 'if \(\$enableKafkaInfrastructure\)\s*\{[^}]*\$files \+= @\("-f", "kafka\.yml"\)'
            $script:startScriptText |
                Should -Match '(?s)if \(\$enableKafkaInfrastructure\) \{.*?up \$upArgs kafka kafka-postgresql-source'
            $script:startScriptText | Should -Not -Match '\$enableKafkaInfrastructure -and \$DatabaseEngine'
            $script:startScriptText | Should -Not -Match 'Skipping Kafka'
        }

        It "keeps the kafka-postgresql-source Connect service name" {
            # The name predates the engine-neutral workflow. Renaming it would break existing local
            # workflows and any external reference to the container name.
            $script:startScriptText | Should -Match 'up \$upArgs kafka kafka-postgresql-source'
        }

        It "adds only the Kafka UI for -EnableKafkaUI, on either engine" {
            $script:startScriptText |
                Should -Match 'if \(\$EnableKafkaUI\)\s*\{[^}]*\$files \+= @\("-f", "kafka-ui\.yml"\)'
            $script:startScriptText | Should -Match 'if \(\$EnableKafkaUI\)\s*\{[^}]*up \$upArgs kafka-ui'
            $script:startScriptText | Should -Not -Match '\$EnableKafkaUI -and \$DatabaseEngine'
        }

        It "never lets the Kafka UI opt-in imply the CDC opt-in" {
            # -EnableKafkaUI implies the Kafka infrastructure, as it always has. It must not also
            # select the CDC workflow: every CDC-specific branch is guarded by -EnableKafkaCdc alone.
            foreach ($match in [regex]::Matches($script:startScriptText, '(?m)^\s*if \([^)]*\$EnableKafkaCdc[^)]*\)')) {
                $match.Value | Should -Not -Match '\$EnableKafkaUI'
            }

            $script:startScriptText | Should -Match 'if \(\$EnableKafkaCdc\)'
        }
    }

    Context "infrastructure opt-in is not authority to project or capture" {
        It "configures no DocumentCache projection target" {
            # cdc-streaming.md: infrastructure opt-in must not implicitly select a projection target.
            # The target is written by the CDC opt-in's own configuration step, not by starting Kafka.
            $script:startScriptText | Should -Not -Match 'DocumentCache__Targets'
            $script:startScriptText | Should -Not -Match 'DocumentCache:Targets'
        }

        It "enables CDC on no data store" {
            # Starting DMS is never authority to enable tracking, so the infrastructure phase invokes
            # no control-plane verb at all.
            # Neither the tool nor the one-shot container the bootstrap CDC phase runs it in is
            # reachable from here; this script starts infrastructure and stops. The one control-plane
            # invocation this script reaches - the destructive teardown's retirement - lives in
            # cdc-teardown.psm1 and is guarded by -d -v, which is asserted separately below.
            $script:startScriptText | Should -Not -Match 'dms-document-cache'
            $script:startScriptText | Should -Not -Match 'cdc-setup'
        }

        It "says so in the output the opt-in prints" {
            $script:startScriptText |
                Should -Match 'no projection target is configured and no data store has CDC enabled by this step'
        }
    }
}

Describe "DMS-1323 bootstrap CDC phase" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:wrapperPath = Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
        $script:wrapperText = Get-Content -LiteralPath $script:wrapperPath -Raw
        $script:entryScriptPath = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
        $script:entryScriptText = Get-Content -LiteralPath $script:entryScriptPath -Raw

        Import-Module $script:wrapperPath -Force
        Import-Module (Join-Path $script:sourceDockerComposeRoot "cdc-enable.psm1") -Force

        # Searches the wrapper and the CDC phase module. The CDC phase's own behavior lives in
        # cdc-enable.psm1 and the sequencing in bootstrap-wrapper.psm1, and a test that asserts on a
        # function's text should not have to know which of the two it ended up in.
        function script:Get-WrapperFunctionText {
            param(
                [Parameter(Mandatory)]
                [string]
                $FunctionName
            )

            $searchPaths = @(
                $script:wrapperPath,
                (Join-Path (Split-Path -Parent $script:wrapperPath) "cdc-enable.psm1")
            )

            foreach ($searchPath in $searchPaths) {
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $searchPath, [ref]$null, [ref]$parseErrors
                )

                if (@($parseErrors).Count -gt 0) {
                    throw "Failed to parse '$searchPath': $(@($parseErrors)[0].Message)"
                }

                $functionAst = $ast.FindAll(
                    { param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $FunctionName },
                    $true
                ) | Select-Object -First 1

                if ($null -ne $functionAst) {
                    return $functionAst.Extent.Text
                }
            }

            throw "Function '$FunctionName' was not found in $($searchPaths -join ' or ')."
        }
    }

    Context "entry-script forwarding" {
        It "declares -EnableKafkaCdc and -CdcBindingStatePath" {
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $script:entryScriptPath, [ref]$null, [ref]$parseErrors
            )
            @($parseErrors).Count | Should -Be 0

            $declared = @(
                $ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
            )

            $declared | Should -Contain "EnableKafkaCdc"
            $declared | Should -Contain "CdcBindingStatePath"
        }

        It "forwards both to the wrapper on the start path" {
            # The start path splats every bound parameter, so declaring them is the forwarding.
            $script:entryScriptText | Should -Match '\$wrapperArgs = @\{\} \+ \$PSBoundParameters'
            $script:wrapperText | Should -Match '\[Switch\]\$EnableKafkaCdc'
            $script:wrapperText | Should -Match '\[string\]\$CdcBindingStatePath'
        }

        It "forwards both to teardown, which rebuilds the compose set" {
            $script:entryScriptText |
                Should -Match 'foreach \(\$name in ''EnvironmentFile'', ''IdentityProvider'', ''EnableKafkaUI'', ''EnableKafkaCdc'', ''CdcBindingStatePath'', ''AbandonCdcBindingState'', ''EnableSwaggerUI'', ''DatabaseEngine''\)'
        }

        It "offers the explicit binding-state abandonment only for the destructive teardown" {
            # A failed retirement never infers it, so the entry point has to be able to express it -
            # and a start run that named it is refused rather than carrying an unread permission.
            $script:entryScriptText | Should -Match '\[Switch\]\$AbandonCdcBindingState'
            $script:entryScriptText |
                Should -Match '(?s)if \(\$AbandonCdcBindingState -and -not \(\$d -and \$v\)\) \{[^}]*throw'
            $script:entryScriptText | Should -Match '\$wrapperArgs\.Remove\("AbandonCdcBindingState"\)'
        }

        It "forwards the opt-in to the start script's infrastructure and DMS invocations" {
            $script:wrapperText | Should -Match '\$startArgs\.EnableKafkaCdc = \$true'
            $script:wrapperText | Should -Match '\$dmsStartArgs\.EnableKafkaCdc = \$true'
        }
    }

    Context "opt-in shapes the wrapper refuses" {
        It "rejects -EnableKafkaCdc on the published start path, which does not declare it" {
            # The wrapper is shared between both start scripts but the opt-in is local-only, so
            # without this the switch would be forwarded to a start script that has no such
            # parameter and fail parameter binding inside the infrastructure phase - after Docker
            # and CMS state exist, rather than before anything starts.
            {
                Invoke-BootstrapWrapper -StartScriptName "start-published-dms.ps1" -EnableKafkaCdc
            } | Should -Throw "*-EnableKafkaCdc is supported on the local start path only*"
        }

        It "rejects -EnableKafkaCdc with -InfraOnly" {
            {
                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnableKafkaCdc -InfraOnly
            } | Should -Throw "*-EnableKafkaCdc is not valid with -InfraOnly*"
        }

        It "rejects -EnableKafkaCdc under the keycloak identity provider" {
            {
                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnableKafkaCdc -IdentityProvider "keycloak"
            } | Should -Throw "*self-contained identity provider only*"
        }

        It "rejects -EnableKafkaCdc with -NoDataStore, which reuses a data store this run did not create" {
            {
                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnableKafkaCdc -NoDataStore
            } | Should -Throw "*-EnableKafkaCdc is not valid with -NoDataStore*"
        }

        It "rejects -EnableKafkaCdc with -SchoolYearRange, which configures several data stores" {
            {
                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -EnableKafkaCdc -SchoolYearRange "2024-2025"
            } | Should -Throw "*-EnableKafkaCdc is not valid with -SchoolYearRange*"
        }

        It "rejects -CdcBindingStatePath without the opt-in" {
            {
                Invoke-BootstrapWrapper -StartScriptName "start-local-dms.ps1" -CdcBindingStatePath "state"
            } | Should -Throw "*-CdcBindingStatePath requires -EnableKafkaCdc*"
        }
    }

    Context "runtime settings written before the DMS start" {
        It "writes the projection target, the status role, and the state root" {
            $overrides = Get-CdcRuntimeEnvOverride `
                -TenantKey "district-a" `
                -DataStoreId 7 `
                -BindingStateRootPath "C:/state"

            $overrides["DMS_CDC_TARGET_TENANT_KEY"] | Should -Be "district-a"
            $overrides["DMS_CDC_TARGET_DATA_STORE_ID"] | Should -Be "7"
            $overrides["DMS_DOCUMENTCACHE_STATUS_REQUIRED_ROLE"] | Should -Be "dms-document-cache-operator"
            $overrides["DMS_CDC_BINDING_STATE_PATH"] | Should -Be "C:/state"
        }

        It "keeps the default tenant's blank key" {
            (Get-CdcRuntimeEnvOverride -TenantKey "" -DataStoreId 1 -BindingStateRootPath "/state")["DMS_CDC_TARGET_TENANT_KEY"] |
                Should -BeNullOrEmpty
        }

        It "writes them into the effective env file ahead of the DMS start" {
            # Both settings are read at DMS startup: the target because the enable workflow's first
            # proof is that it is configured, the role because the status endpoint is not mapped
            # without it and the caught-up read would 404.
            $writeIndex = $script:wrapperText.IndexOf('Get-CdcRuntimeEnvOverride `')
            $dmsStartIndex = $script:wrapperText.IndexOf('$dmsStartArgs = @{')

            $writeIndex | Should -BeGreaterThan -1
            $dmsStartIndex | Should -BeGreaterThan $writeIndex
        }

        It "never writes them into the caller's own env file" {
            # The wrapper's contract is that the caller's env file is left untouched, so an opt-in
            # that has no per-run derived file to write to fails closed instead.
            $script:wrapperText |
                Should -Match 'requires the per-run derived environment file; the run resolved to the caller''s own env file'
        }
    }

    Context "phase ordering" {
        It "keeps the phase's own behavior out of the orchestration wrapper" {
            # command-boundaries.md gives bootstrap-local-dms.ps1 orchestration only: it sequences
            # phase commands and forwards parameters, and must not synthesize credentials, inspect
            # database state, or absorb a concern a phase owns. The CDC enable resolves credentials,
            # gates on endpoint authorization, and provisions a database principal - all phase work,
            # and all of it belongs to enable-kafka-cdc.ps1.
            (Join-Path $script:sourceDockerComposeRoot "enable-kafka-cdc.ps1") | Should -Exist

            $script:wrapperText | Should -Not -Match 'Get-DmsToken'
            $script:wrapperText | Should -Not -Match 'provision-cdc-principal\.ps1'
            $script:wrapperText | Should -Not -Match 'cdc-setup\.yml'
            $script:wrapperText | Should -Not -Match '"cdc", "enable"'
            $script:wrapperText | Should -Not -Match 'health/document-cache'
        }

        It "reads the phase result structurally rather than parsing its output" {
            # The same contract configure-local-data-store.ps1 has with the wrapper: a phase returns
            # an object, and the caller never scrapes human-readable output to recover it.
            $script:wrapperText | Should -Match '\$cdcResult = & "\$PSScriptRoot/enable-kafka-cdc\.ps1" @cdcArgs'
            $script:wrapperText | Should -Match '\$cdcResult\.Status -ne "Enabled"'

            $phaseScriptText = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "enable-kafka-cdc.ps1"
            ) -Raw
            $phaseScriptText | Should -Match 'Invoke-CdcEnablePhase'

            $phaseModuleText = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "cdc-enable.psm1"
            ) -Raw
            $phaseModuleText | Should -Match '(?m)^\s+Status\s+= "Enabled"'
        }

        It "runs the CDC phase after the DMS start and before any seed delivery" {
            $dmsStartIndex = $script:wrapperText.IndexOf('& "$PSScriptRoot/$StartScriptName" @dmsStartArgs')
            $cdcPhaseIndex = $script:wrapperText.IndexOf('& "$PSScriptRoot/enable-kafka-cdc.ps1" @cdcArgs')
            $lastSeedIndex = $script:wrapperText.LastIndexOf('load-dms-seed-data.ps1" @seedArgs')

            $dmsStartIndex | Should -BeGreaterThan -1
            $cdcPhaseIndex | Should -BeGreaterThan $dmsStartIndex
            $lastSeedIndex | Should -BeGreaterThan $cdcPhaseIndex
        }

        It "proves the status endpoint answers for the operator credential before enabling anything" {
            $phaseText = Get-WrapperFunctionText -FunctionName "Invoke-CdcEnablePhase"

            $tokenIndex = $phaseText.IndexOf('Get-DmsToken `')
            $preflightIndex = $phaseText.IndexOf('Assert-CdcDocumentCacheStatusEndpoint `')
            $enableIndex = $phaseText.IndexOf('Get-CdcEnableArgument `')

            $tokenIndex | Should -BeGreaterThan -1
            $preflightIndex | Should -BeGreaterThan $tokenIndex
            $enableIndex | Should -BeGreaterThan $preflightIndex
        }

        It "names the two configuration faults the preflight can see" {
            $preflightText = Get-WrapperFunctionText -FunctionName "Assert-CdcDocumentCacheStatusEndpoint"

            $preflightText | Should -Match 'returned 404'
            $preflightText | Should -Match 'RequiredRole did not reach the DMS container'
            $preflightText | Should -Match 'returned 403'
        }
    }

    Context "instance-database creation evidence" {
        It "asserts creation only when the datastore volume did not already exist" {
            # docker is shadowed inside the module scope so the probe reads this fixture rather than
            # the developer's real Docker state.
            InModuleScope -ModuleName "bootstrap-wrapper" -ScriptBlock {
                Mock docker { $global:LASTEXITCODE = 0; return "" }

                Test-WrapperDataStoreVolumeAbsent `
                    -DatabaseEngine "postgresql" `
                    -ComposeProjectName "dms-local" |
                    Should -BeTrue

                Mock docker { $global:LASTEXITCODE = 0; return "dms-local_dms-postgresql" }

                Test-WrapperDataStoreVolumeAbsent `
                    -DatabaseEngine "postgresql" `
                    -ComposeProjectName "dms-local" |
                    Should -BeFalse
            }
        }

        It "withholds the assertion when Docker cannot answer" {
            InModuleScope -ModuleName "bootstrap-wrapper" -ScriptBlock {
                Mock docker { $global:LASTEXITCODE = 1; return "" }

                Test-WrapperDataStoreVolumeAbsent `
                    -DatabaseEngine "postgresql" `
                    -ComposeProjectName "dms-local" |
                    Should -BeFalse
            }
        }

        It "looks for the major-versioned SQL Server volume the compose file declares" {
            $mssqlVolumeName = (Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "mssql.yml") -Raw)
            $mssqlVolumeName | Should -Match '(?m)^volumes:\s*
?
  dms-mssql-2025:'

            Get-WrapperFunctionText -FunctionName "Test-WrapperDataStoreVolumeAbsent" |
                Should -Match 'dms-mssql-2025'
        }

        It "observes the volume before the stack is started, not after" {
            # Once the engine container is up the volume exists whichever run created it, so the
            # observation is worthless unless it is taken ahead of the start phase.
            $wrapperText = $script:wrapperText
            $observationIndex = $wrapperText.IndexOf('$cdcDataStoreVolumeWasAbsent = Test-WrapperDataStoreVolumeAbsent')
            $startIndex = $wrapperText.IndexOf('& "$PSScriptRoot/$StartScriptName" @startArgs')

            $observationIndex | Should -BeGreaterThan 0
            $startIndex | Should -BeGreaterThan 0
            $observationIndex | Should -BeLessThan $startIndex
        }

        It "never derives the creation assertion from the -NoDataStore switch" {
            # -NoDataStore selects whether CMS metadata is reused; it says nothing about whether a
            # physical database was created, and -EnableKafkaCdc rejects it outright, so deriving the
            # assertion from it made the assertion a constant $true.
            $script:wrapperText | Should -Not -Match '-DatabaseCreatedByThisRun \(-not \$NoDataStore\)'
            $script:wrapperText | Should -Not -Match 'DatabaseCreatedByThisRun = \(-not \$NoDataStore\)'
            $script:wrapperText | Should -Match 'DatabaseCreatedByThisRun = \$cdcDataStoreVolumeWasAbsent'
        }
    }

    Context "cdc enable invocation" {
        BeforeAll {
            $script:createdRunArguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env.derived" `
                -TenantKey "" `
                -DataStoreId 1 `
                -DatabaseEngine "postgresql" `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken "token-value" `
                -SourceDatabaseName "edfi_datamanagementservice"
        }

        It "runs the tool as a one-shot container on the dms network" {
            # The instance database is registered in CMS under its container alias and the broker
            # advertises dms-kafka1:9092, so a host-side process reaches neither.
            ($script:createdRunArguments -join " ") | Should -BeLike "*compose -f cdc-setup.yml*"
            ($script:createdRunArguments -join " ") | Should -BeLike "*run --rm --build*"
            $script:createdRunArguments | Should -Contain "cdc-setup"
            $script:createdRunArguments | Should -Contain "enable"
        }

        It "carries the target, the local deployment policy, and the mounted state root" {
            $joined = $script:createdRunArguments -join " "

            $joined | Should -BeLike "*--data-store-id 1*"
            $joined | Should -BeLike "*--deployment-key local*"
            $joined | Should -BeLike "*--instance-key ds1*"
            $joined | Should -BeLike "*--generation 1*"
            $joined | Should -BeLike "*--kafka-bootstrap-servers dms-kafka1:9092*"
            $joined | Should -BeLike "*--connect-base-url http://kafka-postgresql-source:8083*"
            $joined | Should -BeLike "*--durability-profile local*"
            $joined | Should -BeLike "*--cdc-binding-state-path /state*"
            $joined | Should -BeLike "*-p dms-local*"
            $joined | Should -BeLike "*--env-file /tmp/.env.derived*"
        }

        It "omits --tenant-key for the default tenant" {
            $script:createdRunArguments | Should -Not -Contain "--tenant-key"
        }

        It "passes an explicit tenant key when one is configured" {
            $arguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env.derived" `
                -TenantKey "district-a" `
                -DataStoreId 2 `
                -DatabaseEngine "postgresql" `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken "token-value" `
                -SourceDatabaseName "edfi_datamanagementservice"

            ($arguments -join " ") | Should -BeLike "*--tenant-key district-a*"
        }

        It "supplies the evidence flags only for a database this run created" {
            ($script:createdRunArguments -join " ") |
                Should -BeLike "*--database-creation-mode created-for-initial-cdc-provisioning*"
            ($script:createdRunArguments -join " ") | Should -BeLike "*--write-admission closed-never-opened*"

            $reusedArguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env.derived" `
                -TenantKey "" `
                -DataStoreId 1 `
                -DatabaseEngine "postgresql" `
                -DatabaseCreatedByThisRun $false `
                -DmsBearerToken "token-value" `
                -SourceDatabaseName "edfi_datamanagementservice"

            $reusedArguments | Should -Not -Contain "--database-creation-mode"
            $reusedArguments | Should -Not -Contain "--write-admission"
        }

        It "runs the provider setup as the engine's own administrative principal" {
            ($script:createdRunArguments -join " ") |
                Should -BeLike "*DataManagement__DocumentCache__Cdc__SetupPrincipal=postgres*"

            $mssqlArguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env.derived" `
                -TenantKey "" `
                -DataStoreId 1 `
                -DatabaseEngine "mssql" `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken "token-value" `
                -SourceDatabaseName "edfi_datamanagementservice"

            ($mssqlArguments -join " ") |
                Should -BeLike "*DataManagement__DocumentCache__Cdc__SetupPrincipal=sa*"
        }

        It "supplies the connector principal every cdc verb requires" {
            # The provider-setup input factory refuses any verb without it, because both the create
            # pass and the validate-only pass report the grants this principal holds. It is required
            # whether or not a broker authorizer is enabled.
            ($script:createdRunArguments -join " ") |
                Should -BeLike "*DataManagement__DocumentCache__Cdc__ConnectorPrincipal=dms_connector*"
        }

        It "supplies every provider connection property the connector template requires" {
            # The connector reaches the source directly rather than through the DMS connection
            # string, so these are container-internal names, and the template rejects a rendered
            # configuration that is missing any of them.
            $joined = $script:createdRunArguments -join " "

            $joined | Should -BeLike "*ProviderConnectionProperties__database.hostname=dms-postgresql*"
            $joined | Should -BeLike "*ProviderConnectionProperties__database.port=5432*"
            $joined | Should -BeLike "*ProviderConnectionProperties__database.user=dms_connector*"
            $joined | Should -BeLike "*ProviderConnectionProperties__database.dbname=edfi_datamanagementservice*"
        }

        It "names the SQL Server catalog property and host for the mssql engine" {
            $mssqlArguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env.derived" `
                -TenantKey "" `
                -DataStoreId 1 `
                -DatabaseEngine "mssql" `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken "token-value" `
                -SourceDatabaseName "edfi_datamanagementservice"

            $joined = $mssqlArguments -join " "

            $joined | Should -BeLike "*ProviderConnectionProperties__database.hostname=dms-mssql*"
            $joined | Should -BeLike "*ProviderConnectionProperties__database.port=1433*"
            $joined | Should -BeLike "*ProviderConnectionProperties__database.names=edfi_datamanagementservice*"
            # PostgreSQL's catalog property is not the SQL Server one, and the template allows only
            # the property belonging to the provider.
            $joined | Should -Not -BeLike "*database.dbname*"
        }

        It "references the connector password rather than rendering it" {
            # The registered configuration is read back and compared during validation, so a
            # rendered password would be a secret in the worker's own config topic.
            $joined = $script:createdRunArguments -join " "

            $joined | Should -BeLike '*ProviderConnectionProperties__database.password=${env:CDC_DATABASE_PASSWORD}*'
            $joined | Should -Not -BeLike "*EdFi_Dms1!*"
        }

        It "creates the connector database principal before the enable" {
            # Provider setup grants this principal its capture access but never creates it: the SQL
            # Server pass throws outright when it is absent.
            $phaseText = Get-WrapperFunctionText -FunctionName "Invoke-CdcEnablePhase"

            $principalIndex = $phaseText.IndexOf('provision-cdc-principal.ps1')
            $enableIndex = $phaseText.IndexOf('Get-CdcEnableArgument `')

            $principalIndex | Should -BeGreaterThan -1
            $enableIndex | Should -BeGreaterThan $principalIndex
        }

        It "resolves the captured database from the caller, else the engine's configured datastore name" {
            # An explicit name wins because the E2E wrapper provisions its own database; otherwise
            # it must be the database a plain bootstrap run registered in CMS.
            Resolve-CdcSourceDatabaseName `
                -EnvValues @{ POSTGRES_DB_NAME = "from_env" } `
                -DatabaseEngine "postgresql" `
                -SourceDatabaseName "explicit" | Should -Be "explicit"

            Resolve-CdcSourceDatabaseName `
                -EnvValues @{ POSTGRES_DB_NAME = "from_env" } `
                -DatabaseEngine "postgresql" | Should -Be "from_env"

            Resolve-CdcSourceDatabaseName `
                -EnvValues @{ MSSQL_DB_NAME = "from_mssql_env" } `
                -DatabaseEngine "mssql" | Should -Be "from_mssql_env"

            Resolve-CdcSourceDatabaseName `
                -EnvValues @{} `
                -DatabaseEngine "postgresql" | Should -Be "edfi_datamanagementservice"
        }

        It "hands the operator token to the tool through the environment, not the command line" {
            $tokenIndex = [array]::IndexOf($script:createdRunArguments, "DataManagement__DocumentCache__Cdc__DmsBearerToken=token-value")
            $tokenIndex | Should -BeGreaterThan 0
            $script:createdRunArguments[$tokenIndex - 1] | Should -Be "-e"
            ($script:createdRunArguments | Where-Object { $_ -like "--*token*" }) | Should -BeNullOrEmpty
        }
    }

    Context "compose delivery for the DocumentCache settings" {
        It "passes the projection target and status role to the dms service on both stacks" {
            foreach ($name in @("local-dms.yml", "published-dms.yml")) {
                $composeText = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot $name) -Raw

                $composeText | Should -Match 'DataManagement__DocumentCache__Targets__0__TenantKey: \$\{DMS_CDC_TARGET_TENANT_KEY:-\}'
                $composeText | Should -Match 'DataManagement__DocumentCache__Targets__0__DataStoreId: \$\{DMS_CDC_TARGET_DATA_STORE_ID:-\}'
                $composeText | Should -Match 'DataManagement__DocumentCache__Status__RequiredRole: \$\{DMS_DOCUMENTCACHE_STATUS_REQUIRED_ROLE:-\}'
            }
        }

        It "passes the projection target to the cdc setup container as well as to the dms service" {
            # The enable workflow's first step proves the target against the UNMODIFIED configuration
            # of the process running the verb, so the setup container has to receive the same pair the
            # projector was started with. Reading the same two variables is what keeps them one pair:
            # delivering them to the dms service alone left every enable refusing at step 1 with the
            # target section absent, while DMS itself was configured correctly.
            $cdcSetupText = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "cdc-setup.yml") -Raw

            $cdcSetupText | Should -Match 'DataManagement__DocumentCache__Targets__0__TenantKey: \$\{DMS_CDC_TARGET_TENANT_KEY:-\}'
            $cdcSetupText | Should -Match 'DataManagement__DocumentCache__Targets__0__DataStoreId: \$\{DMS_CDC_TARGET_DATA_STORE_ID:-\}'
            # The status role configures the DMS endpoint itself; the verb only reads that endpoint,
            # so naming it here would suggest this container maps it.
            $cdcSetupText | Should -Not -Match 'DataManagement__DocumentCache__Status__RequiredRole'
        }

        It "leaves every per-run CDC variable blank in the tracked environment sample" {
            # These three are written by the CDC opt-in for the run that enables it. Blank is what
            # binds to no projection target and leaves the status endpoint unmapped, which is what a
            # run without the opt-in must get. The static deployment defaults - the connector
            # principal and its password - are values rather than per-run decisions and are asserted
            # against the shared resolver instead.
            $envExample = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot ".env.example") -Raw

            $envExample | Should -Match '(?m)^DMS_CDC_TARGET_TENANT_KEY=\s*$'
            $envExample | Should -Match '(?m)^DMS_CDC_TARGET_DATA_STORE_ID=\s*$'
            $envExample | Should -Match '(?m)^DMS_DOCUMENTCACHE_STATUS_REQUIRED_ROLE=\s*$'
        }

        It "keeps the CDC setup container out of every compose command but its own" {
            $cdcSetupText = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "cdc-setup.yml") -Raw

            $cdcSetupText | Should -Match '(?m)^\s+profiles:'
            $cdcSetupText | Should -Match '(?m)^\s+- cdc\s*$'
            $cdcSetupText | Should -Match 'dockerfile: DocumentCacheAdmin\.Dockerfile'
            $cdcSetupText | Should -Match '\$\{DMS_CDC_BINDING_STATE_PATH:-\./\.cdc-state\}:/state'
            $cdcSetupText | Should -Match 'DataManagement__DocumentCache__Cdc__DmsBaseUrl'
            # The per-run decisions belong to the phase, not to the file. The connector principal
            # and its connection properties join them: the captured database is a per-run value, and
            # a compose default for it would silently capture the wrong database.
            $cdcSetupText | Should -Not -Match 'DmsBearerToken'
            $cdcSetupText | Should -Not -Match 'SetupPrincipal'
            $cdcSetupText | Should -Not -Match 'ConnectorPrincipal'
            $cdcSetupText | Should -Not -Match 'ProviderConnectionProperties'
        }
    }

    Context "bootstrap manifest boundary" {
        It "writes nothing CDC-related into the bootstrap manifest" {
            # The binding record outlives any one bootstrap run; the manifest is prepared-input
            # handoff, not CDC control-plane state.
            $manifestModuleText = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "bootstrap-manifest.psm1"
            ) -Raw

            $manifestModuleText | Should -Not -Match 'Cdc'
            $manifestModuleText | Should -Not -Match 'DMS_CDC'
            $script:wrapperText | Should -Not -Match 'bootstrap-manifest\.json[^\r\n]*[Cc]dc'
        }
    }
}

Describe "DMS-1323 Connect pinning, metrics bridge, and destructive teardown" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:startScriptPath = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
        $script:startScriptText = Get-Content -LiteralPath $script:startScriptPath -Raw
        $script:kafkaComposeText = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "kafka.yml") -Raw
        $script:cdcSetupComposeText = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "cdc-setup.yml") -Raw
        $script:envExampleText = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot ".env.example") -Raw
        $script:documentCacheAdminDockerfileText = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/DocumentCacheAdmin.Dockerfile") -Raw
        $script:teardownModulePath = Join-Path $script:sourceDockerComposeRoot "cdc-teardown.psm1"
        $script:teardownModuleText = Get-Content -LiteralPath $script:teardownModulePath -Raw

        Import-Module $script:teardownModulePath -Force
        # The connector-principal resolver and the local deployment policy are the shared
        # authorities the Connect worker's declared secret, the enable phase's emitted reference,
        # and both invocations' endpoint arguments are asserted against.
        Import-Module (Join-Path $script:sourceDockerComposeRoot "env-utility.psm1") -Force
        # The enable-phase argument builder is compared against the teardown's, so this Describe
        # imports it rather than depending on an earlier one having done so.
        Import-Module (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Force
        Import-Module (Join-Path $script:sourceDockerComposeRoot "cdc-enable.psm1") -Force

        function script:Get-StartScriptFunctionText {
            param(
                [Parameter(Mandatory)]
                [string]
                $FunctionName
            )

            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $script:startScriptPath, [ref]$null, [ref]$parseErrors
            )

            if (@($parseErrors).Count -gt 0) {
                throw "Failed to parse '$script:startScriptPath': $(@($parseErrors)[0].Message)"
            }

            $functionAst = $ast.FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $FunctionName },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$script:startScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        # The digest guard is lifted out of the straight-line start script and exercised directly -
        # the same technique the state-root resolver above uses.
        . ([scriptblock]::Create((Get-StartScriptFunctionText -FunctionName "Assert-CdcConnectImagePinnedByDigest")))

        function script:New-BindingRecordFile {
            param(
                [Parameter(Mandatory)]
                [string]
                $BindingStateRoot,

                [Parameter(Mandatory)]
                [string]
                $DeploymentKey,

                [Parameter(Mandatory)]
                [string]
                $InstanceKey,

                [Parameter(Mandatory)]
                [long]
                $Generation,

                [Parameter(Mandatory)]
                [hashtable]
                $Record
            )

            $directory = Join-Path (Join-Path (Join-Path $BindingStateRoot "bindings") $DeploymentKey) $InstanceKey
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $path = Join-Path $directory "$Generation.json"
            ($Record | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $path -Encoding utf8
            return $path
        }

        function script:New-TemporaryStateRoot {
            $root = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1323-teardown-$([System.Guid]::NewGuid().ToString('n'))"
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            return $root
        }
    }

    Context "Kafka Connect image pinned by digest" {
        It "takes the CDC image from an environment variable and keeps the :pre default for the non-CDC path" {
            $script:kafkaComposeText |
                Should -Match 'image: \$\{DMS_CDC_CONNECT_IMAGE:-edfialliance/ed-fi-kafka-connect:pre\}'
        }

        It "hardcodes no digest anywhere" {
            # The digest is operator-supplied and contract-validated, never a repo constant. A
            # concrete digest is what is banned, not the word - the guard's own message shows the
            # expected shape.
            $script:kafkaComposeText | Should -Not -Match 'ed-fi-kafka-connect@sha256:[0-9a-f]{64}'
            $script:startScriptText | Should -Not -Match 'ed-fi-kafka-connect@sha256:[0-9a-f]{64}'
            $script:envExampleText | Should -Match '(?m)^DMS_CDC_CONNECT_IMAGE=\s*$'
        }

        It "rejects an unset image rather than falling back to a tag" {
            { Assert-CdcConnectImagePinnedByDigest -Image "" } |
                Should -Throw "*-EnableKafkaCdc requires DMS_CDC_CONNECT_IMAGE*"
        }

        It "rejects a tag-qualified image" {
            { Assert-CdcConnectImagePinnedByDigest -Image "edfialliance/ed-fi-kafka-connect:pre" } |
                Should -Throw "*must name the Kafka Connect image by immutable digest*"
        }

        It "claims only the digest form it actually enforces" {
            # Both published builds are ed-fi-kafka-connect and only the digest separates the
            # qualified one, so the gate cannot verify identity. A digest for another image passes
            # here and fails inside connector validation, and the messages say so rather than
            # reading as an identity check that was made.
            $guard = Get-StartScriptFunctionText -FunctionName "Assert-CdcConnectImagePinnedByDigest"
            $throwMessages = [regex]::Matches($guard, 'throw "(?<message>[^"]+)"') |
                ForEach-Object { $_.Groups['message'].Value }

            @($throwMessages).Count | Should -Be 2
            foreach ($message in $throwMessages) {
                $message | Should -BeLike "*immutable digest*"
                $message | Should -BeLike "*inside connector validation*"
                $message | Should -Not -BeLike "*identify the qualified*"
            }
        }

        It "accepts a digest-qualified image" {
            $digestImage = "edfialliance/ed-fi-kafka-connect@sha256:$('a' * 64)"

            Assert-CdcConnectImagePinnedByDigest -Image $digestImage | Should -Be $digestImage
        }

        It "validates the value Compose would resolve, not the env file's own text" {
            # An ambient shell value wins over the env file during interpolation, so the guard reads
            # through the shared Compose-equivalent resolver.
            $script:startScriptText |
                Should -Match 'Get-ComposeResolvedEnvValue -EnvironmentValues \$envValues -Name "DMS_CDC_CONNECT_IMAGE"'
        }

        It "requires the digest only for the CDC opt-in" {
            $script:startScriptText |
                Should -Match '(?s)if \(\$EnableKafkaCdc\) \{(?:(?!\r?\n    \}).)*Assert-CdcConnectImagePinnedByDigest'
        }
    }

    Context "JMX-over-HTTP metrics bridge" {
        It "enables Jolokia on the Connect service" {
            $script:kafkaComposeText | Should -Match "ENABLE_JOLOKIA: 'true'"
        }

        It "publishes the bridge on loopback only" {
            $script:kafkaComposeText |
                Should -Match "- '127\.0\.0\.1:\`$\{CONNECT_JOLOKIA_PORT:-8778\}:8778'"
            $script:envExampleText | Should -Match '(?m)^CONNECT_JOLOKIA_PORT=8778\s*$'
        }

        It "never uses the Prometheus JMX exporter branch" {
            # That entrypoint branch targets port 9404, but no jmx_prometheus_javaagent jar ships in
            # either the Connect or the Kafka base image, so the branch resolves to nothing. The
            # setting is what must be absent; kafka.yml says why in a comment.
            $script:kafkaComposeText | Should -Not -Match '(?m)^\s+ENABLE_JMX_EXPORTER:'
            $script:kafkaComposeText | Should -Not -Match ':9404'
        }
    }

    Context "one engine-neutral Connect service, parameterized in place" {
        It "keeps the kafka-postgresql-source service name" {
            $script:kafkaComposeText | Should -Match '(?m)^  kafka-postgresql-source:'
            $script:kafkaComposeText | Should -Not -Match 'kafka-mssql-source'
            $script:kafkaComposeText | Should -Not -Match 'kafka-sqlserver-source'
        }

        It "declares one Connect service for both engines" {
            ([regex]::Matches($script:kafkaComposeText, 'ed-fi-kafka-connect')).Count | Should -Be 1
        }

        It "names the worker group and its offset store identically to the control plane" {
            $script:kafkaComposeText | Should -Match 'GROUP_ID: \$\{DMS_CDC_CONNECT_WORKER_KEY:-1\}'
            $script:kafkaComposeText |
                Should -Match 'OFFSET_STORAGE_TOPIC: \$\{DMS_CDC_CONNECT_OFFSET_STORAGE_TOPIC:-debezium_source_offset\}'
            $script:cdcSetupComposeText |
                Should -Match 'ConnectWorkerKey: \$\{DMS_CDC_CONNECT_WORKER_KEY:-1\}'
            $script:cdcSetupComposeText |
                Should -Match 'ConnectOffsetStorageTopic: \$\{DMS_CDC_CONNECT_OFFSET_STORAGE_TOPIC:-debezium_source_offset\}'
        }
    }

    Context "shared Connect offset store provisioned before the worker" {
        It "declares the provisioning step" {
            (Get-StartScriptFunctionText -FunctionName "Initialize-CdcConnectOffsetStore") |
                Should -Not -BeNullOrEmpty
        }

        It "provisions the store before every up that starts the Connect worker" {
            # cdc-streaming.md: bootstrap pre-creates and validates the configured shared offset
            # topic BEFORE it starts local Kafka Connect, and never relies on Connect topic
            # auto-creation or broker defaults. A worker that gets there first sets only
            # cleanup.policy and leaves min.insync.replicas to the broker default, which the control
            # plane refuses and does not repair.
            $script:startScriptText |
                Should -Match '(?s)Initialize-CdcConnectOffsetStore.*?up \$upArgs kafka kafka-postgresql-source'

            # Both start paths: the infrastructure-only one names the two services, and the
            # full-stack up starts the worker along with everything else.
            ([regex]::Matches($script:startScriptText, 'Initialize-CdcConnectOffsetStore `')).Count |
                Should -Be 2
        }

        It "starts Kafka on its own first and provisions the store against it" {
            $provisioning = Get-StartScriptFunctionText -FunctionName "Initialize-CdcConnectOffsetStore"

            $provisioning | Should -Match 'up --detach kafka'
            $provisioning | Should -Not -Match 'kafka-postgresql-source'
        }

        It "sets the explicit topic-level policy the control plane validates" {
            $provisioning = Get-StartScriptFunctionText -FunctionName "Initialize-CdcConnectOffsetStore"
            $policy = Get-LocalCdcDeploymentPolicy

            # Created with the values, and then set on the topic whether or not the create found it
            # present: a stack stopped without -v keeps a store an earlier non-CDC run let the worker
            # create.
            $provisioning | Should -Match 'kafka-topics\.sh'
            $provisioning | Should -Match '--config cleanup\.policy=compact'
            $provisioning | Should -Match 'min\.insync\.replicas=\$minInSyncReplicas'
            $provisioning | Should -Match 'kafka-configs\.sh'
            $provisioning | Should -Match '--add-config'

            $policy.OffsetStoreReplicationFactor | Should -Be 1
            $policy.OffsetStoreMinInSyncReplicas | Should -Be 1
            $policy.OffsetStorePartitionCount | Should -Be 25
            $policy.OffsetStoreTopicDefault | Should -Be "debezium_source_offset"
        }

        It "names the same topic the worker and the control plane name" {
            # One variable, one default: the store the script provisions has to be the store the
            # worker uses and the store the cdc verbs validate.
            $script:startScriptText | Should -Match 'DMS_CDC_CONNECT_OFFSET_STORAGE_TOPIC'
            $script:startScriptText | Should -Match 'OffsetStoreTopicDefault'
        }
    }

    Context "connector source credential" {
        It "activates the env config provider on the Connect worker" {
            # Without it the worker cannot resolve the ${env:...} reference the connector
            # configuration carries in place of the password.
            $script:kafkaComposeText | Should -Match '(?m)^\s+CONNECT_CONFIG_PROVIDERS: env\s*$'
            $script:kafkaComposeText |
                Should -Match 'CONNECT_CONFIG_PROVIDERS_ENV_CLASS: org\.apache\.kafka\.common\.config\.provider\.EnvVarConfigProvider'
        }

        It "exposes the referenced secret under the name the connector configuration references" {
            # The reference the enable phase emits and the variable the worker declares are one
            # name; a drift between them leaves the connector unable to authenticate.
            $connectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues @{}

            $connectorPrincipal.PasswordReference | Should -Be '${env:CDC_DATABASE_PASSWORD}'
            $script:kafkaComposeText |
                Should -Match "(?m)^\s+$($connectorPrincipal.PasswordEnvVariable): "
        }

        It "keeps the worker's password default and the principal's password default in step" {
            # The worker starts in the infrastructure phase, before the CDC phase writes any derived
            # env value, so its default is what a run that set nothing actually gets. If these two
            # disagree, the principal is created with one password and the connector authenticates
            # with another.
            $connectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues @{}

            $script:kafkaComposeText |
                Should -Match "CDC_DATABASE_PASSWORD: \`$\{DMS_CDC_CONNECTOR_PASSWORD:-$([regex]::Escape($connectorPrincipal.Password))\}"
            $script:envExampleText |
                Should -Match "(?m)^DMS_CDC_CONNECTOR_PASSWORD=$([regex]::Escape($connectorPrincipal.Password))\s*$"
            $script:envExampleText |
                Should -Match "(?m)^DMS_CDC_CONNECTOR_PRINCIPAL=$([regex]::Escape($connectorPrincipal.PrincipalName))\s*$"
        }

        It "creates the principal as a dedicated login rather than the administrative one" {
            # Debezium would otherwise read the source as a superuser, and on SQL Server `sa`
            # resolves to `dbo`, which cannot be added to the gating role provider setup grants.
            $principalScriptText = Get-Content -LiteralPath (
                Join-Path $script:sourceDockerComposeRoot "provision-cdc-principal.ps1"
            ) -Raw

            $principalScriptText | Should -Match 'CREATE ROLE %I WITH LOGIN REPLICATION NOSUPERUSER'
            $principalScriptText | Should -Match 'CREATE LOGIN'
            $principalScriptText | Should -Match 'CREATE USER'
            # Idempotent: an existing principal keeps its password, because rotating it here would
            # break a connector already registered against it.
            $principalScriptText | Should -Match 'IF NOT EXISTS \(SELECT 1 FROM pg_catalog\.pg_roles'
            $principalScriptText | Should -Match 'IF SUSER_ID'
            $principalScriptText | Should -Match 'IF USER_ID'
            $principalScriptText | Should -Not -Match 'ALTER ROLE'
            $principalScriptText | Should -Not -Match 'ALTER LOGIN'
        }
    }

    Context "binding state store root the setup container writes through" {
        # Three declarations name one container path: cdc-setup.yml mounts the host store there,
        # cdc-setup.yml sets DMS_CDC_STATE_ROOT to it so the image tightens the right directory, and
        # env-utility.psm1 passes it as --cdc-binding-state-path. Drift between any two is silent -
        # the verb would write through one path while another was mounted or tightened - so the
        # three are reconciled against the policy resolver here rather than agreed by hand.
        It "mounts, declares, and passes one and the same container path" {
            $policy = Get-LocalCdcDeploymentPolicy
            $mountSuffix = [regex]::Escape("}:" + $policy.BindingStatePath)
            $stateRoot = [regex]::Escape($policy.BindingStatePath)

            $script:cdcSetupComposeText | Should -Match "(?m)^\s+- .*$mountSuffix\s*$"
            $script:cdcSetupComposeText | Should -Match "(?m)^\s+DMS_CDC_STATE_ROOT: $stateRoot\s*$"
        }

        It "tightens the mounted root before the tool runs" {
            # Docker Desktop presents a bind mount as world-writable whatever the host permissions
            # are - including a directory the host itself created - and the binding state store
            # refuses a group- or world-writable root, so without this every cdc verb fails at its
            # first binding read with LocalStateUnavailable. The mount point is the one directory in
            # the store's tree the store never creates for itself.
            $script:documentCacheAdminDockerfileText | Should -Match 'chmod g-w,o-w'
            $script:documentCacheAdminDockerfileText | Should -Match 'DMS_CDC_STATE_ROOT'
        }

        It "clears only the two bits the store rejects" {
            # An absolute mode would strip the owner and read bits too, which on a native Linux bind
            # mount is the host user's own access - the access the destructive teardown's host-side
            # binding discovery reads the records through.
            $script:documentCacheAdminDockerfileText | Should -Not -Match 'chmod [0-7][0-7][0-7] "\$DMS_CDC_STATE_ROOT"'
        }
    }

    Context "destructive teardown ordering" {
        It "retires the bindings before the compose down removes the volumes" {
            $retireIndex = $script:startScriptText.IndexOf('Invoke-CdcDestructiveTeardown `')
            $downIndex = $script:startScriptText.IndexOf('-p dms-local down $downArgs')

            $retireIndex | Should -BeGreaterThan -1
            $downIndex | Should -BeGreaterThan $retireIndex
        }

        It "retires only on volume removal, so a normal stop retains every artifact" {
            ([regex]::Matches($script:startScriptText, 'Invoke-CdcDestructiveTeardown')).Count | Should -Be 1
            $script:startScriptText |
                Should -Match '(?s)if \(\$v\) \{(?:(?!\r?\n    \}).)*Invoke-CdcDestructiveTeardown'
        }

        It "takes the local endpoints and record-size policy from one shared resolver" {
            # Stated in both callers, the two invocations agreed only by hand: a Connect alias or
            # port changed in the enable path alone would leave `cdc retire` reaching nothing while
            # the compose down removed its artifacts anyway.
            $policy = Get-LocalCdcDeploymentPolicy

            $wrapperArguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile ".env" `
                -TenantKey "" `
                -DataStoreId 1 `
                -DatabaseEngine "postgresql" `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken "token" `
                -SourceDatabaseName "edfi_datamanagementservice"
            $retireArguments = Get-CdcRetireArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile ".env" `
                -BindingRecord ([pscustomobject]@{
                    DataStoreId   = 1
                    TenantKey     = ""
                    DeploymentKey = "local"
                    InstanceKey   = "ds1"
                    Generation    = 1
                }) `
                -DatabaseEngine "postgresql"

            foreach ($arguments in @($wrapperArguments, $retireArguments)) {
                $joined = $arguments -join " "
                $joined | Should -BeLike "*--kafka-bootstrap-servers $($policy.KafkaBootstrapServers)*"
                $joined | Should -BeLike "*--connect-base-url $($policy.ConnectBaseUrl)*"
                $joined | Should -BeLike "*--max-record-bytes $($policy.MaxRecordBytes)*"
                $joined | Should -BeLike "*--durability-profile $($policy.DurabilityProfile)*"
                $joined | Should -BeLike "*--cdc-binding-state-path $($policy.BindingStatePath)*"
            }

            # Neither caller restates them.
            $script:teardownModuleText | Should -Not -Match "dms-kafka1:9092'"
            $wrapperModuleText = Get-Content `
                -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Raw
            $wrapperModuleText | Should -Not -Match '"--kafka-bootstrap-servers", "dms-kafka1:9092"'
        }

        It "names the binding state root it retires from when the path was not supplied" {
            # An omitted -CdcBindingStatePath is permitted on a teardown run, so a stack started
            # with a custom root would otherwise be retired from the empty default silently.
            $script:startScriptText |
                Should -Match '(?s)if \(\[string\]::IsNullOrWhiteSpace\(\$CdcBindingStatePath\)\) \{(?:(?!
?
        \}).)*default binding state store at'
        }

        It "hands the retirement the run's resolved state root, engine, and compose project" {
            $script:startScriptText | Should -Match '-BindingStateRoot \$cdcBindingStateRoot'
            $script:startScriptText | Should -Match '-ComposeProjectName "dms-local"'
            $script:startScriptText | Should -Match '-DatabaseEngine \$DatabaseEngine'
        }

        It "delegates the ordered sequence to the control plane rather than restating it" {
            # cdc retire stops the connector, deletes its committed offsets while it is stopped,
            # deletes the connector, then the governed topics and ACLs, then the provider capture
            # artifacts, then the terminal incident state and the binding record last. A second,
            # unverified ordering next to that one is what this asserts against.
            $script:teardownModuleText | Should -Not -Match 'connectors/'
            $script:teardownModuleText | Should -Not -Match 'kafka-topics'
            $script:teardownModuleText | Should -Not -Match 'DROP PUBLICATION'
            $script:teardownModuleText | Should -Not -Match 'pg_drop_replication_slot'
            $script:teardownModuleText | Should -Not -Match 'Remove-Item -LiteralPath \$binding'
        }

        It "refuses the teardown when the binding store cannot be enumerated" {
            # The container writes the store owner-only, and on a native Linux bind mount a
            # root-owned bindings/ tree is one the invoking user cannot descend into. Get-ChildItem
            # errors are non-terminating by default, so that used to yield nothing, read as "no
            # bindings", and let the caller go on to `down -v` - destroying the governed artifacts the
            # records it could not see still named.
            $script:teardownModuleText | Should -Match 'Get-ChildItem[^
]*-ErrorAction Stop'
            $script:teardownModuleText | Should -Match 'could not be enumerated'
        }

        It "runs the one-shot container as the host user on Linux so the store stays readable" {
            $userArgumentText = Get-WrapperFunctionText -FunctionName "Get-CdcContainerUserArgument"

            $userArgumentText | Should -Match '\$IsLinux'
            $userArgumentText | Should -Match '"--user"'

            # Both invocation paths carry it, or the enable phase writes records the retirement
            # cannot read. They carry it because both are built by the one shared builder, which is
            # also what keeps the setup principal, the compose service, and the policy flags from
            # drifting between them.
            #
            # Re-imported rather than relied on from the Describe: the teardown module imports this
            # one -Force from inside its own functions, which unloads the copy this session holds, so
            # any test that runs after one of those invocations has to bring it back itself.
            Import-Module (Join-Path $script:sourceDockerComposeRoot "cdc-enable.psm1") -Force

            $enableArguments = Get-CdcEnableArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile ".env" `
                -TenantKey "" `
                -DataStoreId 1 `
                -DatabaseEngine "postgresql" `
                -DatabaseCreatedByThisRun $true `
                -DmsBearerToken "token" `
                -SourceDatabaseName "edfi_datamanagementservice"
            $retireArguments = Get-CdcRetireArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile ".env" `
                -BindingRecord ([pscustomobject]@{
                    DataStoreId   = 1
                    TenantKey     = ""
                    DeploymentKey = "local"
                    InstanceKey   = "ds1"
                    Generation    = 1
                }) `
                -DatabaseEngine "postgresql"

            $expectedUserArgument = @(Get-CdcContainerUserArgument)
            foreach ($arguments in @($enableArguments, $retireArguments)) {
                $joined = $arguments -join " "
                if ($expectedUserArgument.Count -gt 0) {
                    $joined | Should -BeLike "*$($expectedUserArgument -join " ")*"
                }

                $joined | Should -BeLike "*DataManagement__DocumentCache__Cdc__SetupPrincipal=postgres*"
            }

            $script:teardownModuleText | Should -Match 'Get-CdcSetupComposeArgument'
            (Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "cdc-enable.psm1") -Raw) |
                Should -Match '\$composeArguments \+= Get-CdcContainerUserArgument'
        }

        It "retains the binding record of a failed retirement and fails the teardown with it" {
            # Removing the volumes around a surviving binding record destroys the connector, offsets,
            # topics, and capture artifacts it still names - the one outcome the cleanup rule forbids,
            # and the state an idempotent retirement retry would have had to act on.
            $root = New-TemporaryStateRoot
            try {
                $recordPath = New-BindingRecordFile `
                    -BindingStateRoot $root -DeploymentKey "local" -InstanceKey "ds1" -Generation 1 -Record @{
                    deploymentKey = "local"
                    tenantKey     = ""
                    dataStoreId   = "1"
                    instanceKey   = "ds1"
                    generation    = 1
                }
                $environmentFile = Join-Path $root ".env"
                "" | Set-Content -LiteralPath $environmentFile -Encoding utf8

                Mock -ModuleName cdc-teardown docker { $global:LASTEXITCODE = 1 }

                {
                    Invoke-CdcDestructiveTeardown `
                        -BindingStateRoot $root `
                        -ComposeProjectName "dms-local" `
                        -EnvironmentFile $environmentFile `
                        -DatabaseEngine "postgresql" `
                        -WarningAction SilentlyContinue
                } | Should -Throw -ExpectedMessage "*did not retire*"

                Test-Path -LiteralPath $recordPath | Should -BeTrue
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "reports an unretired binding and proceeds only when the operator abandons the state" {
            $root = New-TemporaryStateRoot
            try {
                New-BindingRecordFile `
                    -BindingStateRoot $root -DeploymentKey "local" -InstanceKey "ds1" -Generation 1 -Record @{
                    deploymentKey = "local"
                    tenantKey     = ""
                    dataStoreId   = "1"
                    instanceKey   = "ds1"
                    generation    = 1
                } | Out-Null
                $environmentFile = Join-Path $root ".env"
                "" | Set-Content -LiteralPath $environmentFile -Encoding utf8

                Mock -ModuleName cdc-teardown docker { $global:LASTEXITCODE = 1 }

                $results = @(
                    Invoke-CdcDestructiveTeardown `
                        -BindingStateRoot $root `
                        -ComposeProjectName "dms-local" `
                        -EnvironmentFile $environmentFile `
                        -DatabaseEngine "postgresql" `
                        -AbandonBindingState `
                        -WarningAction SilentlyContinue
                )

                $results.Count | Should -Be 1
                $results[0].Retired | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "binding discovery from the durable state store" {
        It "returns nothing when the state store holds no binding" {
            $root = New-TemporaryStateRoot
            try {
                @(Get-CdcRetirableBinding -BindingStateRoot $root).Count | Should -Be 0
                @(Get-CdcRetirableBinding -BindingStateRoot (Join-Path $root "absent")).Count | Should -Be 0
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "reads the target from the record rather than from its path" {
            $root = New-TemporaryStateRoot
            try {
                New-BindingRecordFile -BindingStateRoot $root -DeploymentKey "local" -InstanceKey "ds7" -Generation 1 -Record @{
                    deploymentKey = "local"
                    tenantKey     = "district-a"
                    dataStoreId   = "7"
                    instanceKey   = "ds7"
                    generation    = 1
                    connectorName = "edfi-dms-local-ds7-1"
                } | Out-Null

                $bindings = @(Get-CdcRetirableBinding -BindingStateRoot $root)

                $bindings.Count | Should -Be 1
                $bindings[0].DeploymentKey | Should -Be "local"
                $bindings[0].TenantKey | Should -Be "district-a"
                $bindings[0].DataStoreId | Should -Be "7"
                $bindings[0].InstanceKey | Should -Be "ds7"
                $bindings[0].Generation | Should -Be 1
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "retires a superseding generation before the one it superseded" {
            $root = New-TemporaryStateRoot
            try {
                foreach ($generation in 1, 2, 3) {
                    New-BindingRecordFile -BindingStateRoot $root -DeploymentKey "local" -InstanceKey "ds1" -Generation $generation -Record @{
                        deploymentKey = "local"
                        tenantKey     = ""
                        dataStoreId   = "1"
                        instanceKey   = "ds1"
                        generation    = $generation
                    } | Out-Null
                }

                @(Get-CdcRetirableBinding -BindingStateRoot $root | ForEach-Object { $_.Generation }) |
                    Should -Be @(3, 2, 1)
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "refuses the discovery for a record it cannot read instead of skipping it" {
            # Unreadable is not absent. Skipping the record would let the caller remove the volumes
            # holding the artifacts it still names, which is exactly the case where the target cannot
            # be inferred well enough to retire them first.
            $root = New-TemporaryStateRoot
            try {
                $directory = Join-Path (Join-Path (Join-Path $root "bindings") "local") "ds1"
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
                "{ not json" | Set-Content -LiteralPath (Join-Path $directory "1.json") -Encoding utf8

                { Get-CdcRetirableBinding -BindingStateRoot $root } |
                    Should -Throw -ExpectedMessage "*is not a readable binding record*"
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "refuses the discovery for a record that names no complete binding target" {
            $root = New-TemporaryStateRoot
            try {
                New-BindingRecordFile -BindingStateRoot $root -DeploymentKey "local" -InstanceKey "ds2" -Generation 1 -Record @{
                    deploymentKey = "local"
                    tenantKey     = ""
                    instanceKey   = "ds2"
                    generation    = 1
                } | Out-Null

                { Get-CdcRetirableBinding -BindingStateRoot $root } |
                    Should -Throw -ExpectedMessage "*does not name a complete binding target*"
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "cdc retire invocation" {
        BeforeAll {
            $script:retireBinding = [pscustomobject]@{
                DeploymentKey = "local"
                TenantKey     = ""
                DataStoreId   = "1"
                InstanceKey   = "ds1"
                Generation    = 2
                RecordPath    = "C:/state/bindings/local/ds1/2.json"
            }

            $script:retireArguments = Get-CdcRetireArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env" `
                -BindingRecord $script:retireBinding `
                -DatabaseEngine "postgresql"
        }

        It "runs the tool as a one-shot container on the dms network" {
            ($script:retireArguments -join " ") | Should -BeLike "*compose -f cdc-setup.yml*"
            ($script:retireArguments -join " ") | Should -BeLike "*run --rm --build*"
            $script:retireArguments | Should -Contain "cdc-setup"
            $script:retireArguments | Should -Contain "retire"
        }

        It "carries the exact retirement confirmation token" {
            ($script:retireArguments -join " ") | Should -BeLike "*--confirm cdcBindingRetirement*"
        }

        It "carries the connector principal, which every cdc verb requires" {
            # Retirement runs the validate-only provider pass, which reports the grants this
            # principal holds, so the input factory refuses the verb without it.
            ($script:retireArguments -join " ") |
                Should -BeLike "*DataManagement__DocumentCache__Cdc__ConnectorPrincipal=dms_connector*"
        }

        It "carries no connector source-connection properties" {
            # A retirement registers no connector and reads none, and the captured database name is
            # a per-run value this module has no authority over: supplying a guess would put a wrong
            # value where nothing reads a right one.
            ($script:retireArguments -join " ") | Should -Not -BeLike "*ProviderConnectionProperties*"
        }

        It "names the generation and artifact keys the record carries" {
            $joined = $script:retireArguments -join " "

            $joined | Should -BeLike "*--data-store-id 1*"
            $joined | Should -BeLike "*--deployment-key local*"
            $joined | Should -BeLike "*--instance-key ds1*"
            $joined | Should -BeLike "*--generation 2*"
            $joined | Should -BeLike "*--cdc-binding-state-path /state*"
            $script:retireArguments | Should -Not -Contain "--tenant-key"
        }

        It "passes an explicit tenant key when the record names one" {
            $tenantBinding = [pscustomobject]@{
                DeploymentKey = "local"
                TenantKey     = "district-a"
                DataStoreId   = "3"
                InstanceKey   = "ds3"
                Generation    = 1
                RecordPath    = "C:/state/bindings/local/ds3/1.json"
            }

            (Get-CdcRetireArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env" `
                -BindingRecord $tenantBinding `
                -DatabaseEngine "postgresql") -join " " |
                Should -BeLike "*--tenant-key district-a*"
        }

        It "asserts no provisioning evidence, which is an enablement claim" {
            $script:retireArguments | Should -Not -Contain "--database-creation-mode"
            $script:retireArguments | Should -Not -Contain "--write-admission"
        }

        It "asserts the connector may already be absent, because the same pass removes the broker" {
            # Retirement otherwise refuses a connector the worker does not hold: its committed
            # offsets outlive the configuration and a 404 cannot tell "never registered" from
            # "deleted out from under the record". Only the destructive teardown builds this list,
            # and its very next act removes the broker with its volumes, so those offsets are going
            # either way. Without the assertion, a binding whose connector was never registered -
            # an enable interrupted between the durable record and the connector registration -
            # survives the down naming artifacts that no longer exist, with no stack left to retire
            # it against.
            $script:retireArguments | Should -Contain "--connector-already-absent"
        }

        It "runs the provider teardown as the engine's own administrative principal" {
            ($script:retireArguments -join " ") |
                Should -BeLike "*DataManagement__DocumentCache__Cdc__SetupPrincipal=postgres*"

            (Get-CdcRetireArgument `
                -ComposeProjectName "dms-local" `
                -EnvironmentFile "/tmp/.env" `
                -BindingRecord $script:retireBinding `
                -DatabaseEngine "mssql") -join " " |
                Should -BeLike "*DataManagement__DocumentCache__Cdc__SetupPrincipal=sa*"
        }

        It "carries no operator credential at all" {
            # Retirement reads no projection status. It used to carry a bearer token only because the
            # control plane validated the projection-status settings for every verb before running any
            # step; the collector now refuses for itself instead, so the credential has no reader on
            # this path and putting one here would be handing out a secret for nothing.
            ($script:retireArguments -join " ") | Should -Not -BeLike "*DmsBearerToken*"
            ($script:retireArguments | Where-Object { $_ -like "--*token*" }) | Should -BeNullOrEmpty
        }

        It "does not skip retirement when no operator token can be obtained" {
            # The DMS is normally already gone when a destructive teardown runs, which is exactly when
            # a token cannot be minted. Skipping there left binding records naming artifacts the very
            # next `down -v` destroyed.
            $script:teardownModuleText | Should -Not -Match 'Get-DmsToken'
            $script:teardownModuleText | Should -Not -Match 'no DocumentCache operator token could be obtained'
        }
    }
}

Describe "DMS-1323 E2E harness CDC opt-in" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:e2eSetupPath = Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"
        $script:e2eSetupText = Get-Content -LiteralPath $script:e2eSetupPath -Raw
        $script:e2eTeardownPath = Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1"
        $script:e2eTeardownText = Get-Content -LiteralPath $script:e2eTeardownPath -Raw
        $script:provisionScriptText = Get-Content -LiteralPath (
            Join-Path $script:sourceDockerComposeRoot "provision-e2e-database.ps1"
        ) -Raw

        Import-Module (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Force
        Import-Module (Join-Path $script:sourceDockerComposeRoot "cdc-enable.psm1") -Force
        Import-Module (Join-Path $script:sourceDockerComposeRoot "dms-schema-environment.psm1") -Force

        function script:Get-E2ESetupDeclaredParameters {
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $script:e2eSetupPath, [ref]$null, [ref]$parseErrors
            )

            if (@($parseErrors).Count -gt 0) {
                throw "Failed to parse '$script:e2eSetupPath': $(@($parseErrors)[0].Message)"
            }

            return @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
        }
    }

    Context "parameter surface and the unchanged default run" {
        It "declares -EnableKafkaCdc" {
            Get-E2ESetupDeclaredParameters | Should -Contain "EnableKafkaCdc"
        }

        It "adds nothing to a run that does not ask for CDC" {
            # The splat is empty unless the switch is supplied, so both start phases receive exactly
            # the arguments they always have.
            $script:e2eSetupText | Should -Match '(?m)^\s*\$cdcStartArgs = @\{\}\s*$'
            $script:e2eSetupText |
                Should -Match '(?s)if \(\$EnableKafkaCdc\) \{(?:(?!\n    \}).)*\$cdcStartArgs\.EnableKafkaCdc = \$true'
        }

        It "never enables Kafka unconditionally" {
            # Pins the pre-existing contract: no start-local-dms.ps1 line carries a literal Kafka
            # switch, so the opt-in cannot become the default by editing an invocation.
            $script:e2eSetupText | Should -Not -Match 'start-local-dms\.ps1[^\r\n]*-EnableKafka'
        }
    }

    Context "forwarded to both start phases" {
        It "forwards the opt-in to the infrastructure phase and the DMS phase" {
            $script:e2eSetupText |
                Should -Match 'start-local-dms\.ps1 -InfraOnly[^\r\n]*-AddExtensionSecurityMetadata @cdcStartArgs'
            $script:e2eSetupText |
                Should -Match 'start-local-dms\.ps1 -DmsOnly[^\r\n]*-AddExtensionSecurityMetadata @cdcStartArgs'
        }

        It "binds the splatted switch through the shared schema guard" {
            # The phases run inside Invoke-WithDmsEnvironmentFileSchemaAuthority, which invokes the
            # caller's script block from inside its own module - so this proves the splat reaches the
            # phase as a bound switch rather than a literal argument string.
            $cdcStartArgs = @{ EnableKafkaCdc = $true }
            function script:Invoke-FakeStartPhase {
                param(
                    [switch] $EnableKafkaCdc,
                    [string] $EnvironmentFile
                )

                return "$($EnableKafkaCdc.IsPresent)|$EnvironmentFile"
            }

            $bound = Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
                Invoke-FakeStartPhase -EnvironmentFile ".env.e2e" @cdcStartArgs
            }
            $empty = @{}
            $unbound = Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
                Invoke-FakeStartPhase -EnvironmentFile ".env.e2e" @empty
            }

            $bound | Should -Be "True|.env.e2e"
            $unbound | Should -Be "False|.env.e2e"
        }
    }

    Context "runtime settings before the DMS start, without touching the tracked env file" {
        It "configures the projection target and status role ahead of the DMS phase" {
            $settingsIndex = $script:e2eSetupText.IndexOf('Get-CdcRuntimeEnvOverride `')
            # LastIndexOf: the .DESCRIPTION block quotes the same phase commands, and the invocation
            # is the later occurrence.
            $dmsStartIndex = $script:e2eSetupText.LastIndexOf('start-local-dms.ps1 -DmsOnly')

            $settingsIndex | Should -BeGreaterThan -1
            $dmsStartIndex | Should -BeGreaterThan $settingsIndex
        }

        It "delivers them through the process environment and restores it afterward" {
            # .env.e2e is a tracked file this wrapper must leave exactly as it found it, and Compose
            # gives an ambient value precedence over --env-file.
            $script:e2eSetupText | Should -Match '\[System\.Environment\]::SetEnvironmentVariable\(\$settingName'
            $script:e2eSetupText | Should -Match '(?s)finally \{.*\$cdcRuntimeEnvironmentSnapshot'
            $script:e2eSetupText | Should -Not -Match 'Write-DerivedEnvFile'
            $script:e2eSetupText | Should -Not -Match 'Set-Content[^\r\n]*\$resolvedEnvironmentFile'
            $script:e2eSetupText | Should -Not -Match 'Out-File[^\r\n]*\$resolvedEnvironmentFile'
        }

        It "names the CDC target from the configure phase's structured result" {
            # The data store the binding covers is the one the configure phase selected; nothing else
            # may imply it.
            $script:e2eSetupText |
                Should -Match '\$dataStoreConfiguration = Invoke-WithDmsEnvironmentFileSchemaAuthority'
            $script:e2eSetupText | Should -Match 'Resolve-WrapperSelectedDataStoreIds -ConfigureResult \$configuredDataStore'
        }

        It "refuses the shapes a single binding cannot cover" {
            $script:e2eSetupText |
                Should -Match '-EnableKafkaCdc requires exactly one configured data store'
            $script:e2eSetupText |
                Should -Match '-EnableKafkaCdc does not support route-qualified data stores'
            $script:e2eSetupText |
                Should -Match '-EnableKafkaCdc is supported on the self-contained identity provider only'
        }
    }

    Context "capture registered against the freshly provisioned database" {
        It "runs the enable after the DMS start and before setup reports completion" {
            # LastIndexOf: the .DESCRIPTION block quotes the same phase commands, and the invocation
            # is the later occurrence.
            $dmsStartIndex = $script:e2eSetupText.LastIndexOf('start-local-dms.ps1 -DmsOnly')
            $enableIndex = $script:e2eSetupText.IndexOf('enable-kafka-cdc.ps1" `')
            $completeIndex = $script:e2eSetupText.IndexOf('DMS E2E environment setup complete!')

            $enableIndex | Should -BeGreaterThan $dmsStartIndex
            $completeIndex | Should -BeGreaterThan $enableIndex
        }

        It "asserts a database this run created, which the provision phase resets" {
            # The evidence flags are only true because provision-e2e-database.ps1 drops and recreates
            # the E2E database earlier in this same sequence.
            $script:e2eSetupText | Should -Match '-DatabaseCreatedByThisRun \$true'
            $script:provisionScriptText | Should -Match 'drops if present, then recreates'

            $provisionIndex = $script:e2eSetupText.LastIndexOf('provision-e2e-database.ps1')
            $enableIndex = $script:e2eSetupText.IndexOf('enable-kafka-cdc.ps1" `')

            $provisionIndex | Should -BeGreaterThan -1
            $enableIndex | Should -BeGreaterThan $provisionIndex
        }

        It "runs the shared CDC phase command rather than a copy of the workflow" {
            # command-boundaries.md gives the wrapper orchestration only, so the enable workflow is a
            # phase command both callers invoke - not a function the E2E harness has to import an
            # orchestration module to reach.
            (Join-Path $script:sourceDockerComposeRoot "enable-kafka-cdc.ps1") | Should -Exist
            $script:e2eSetupText | Should -Match 'enable-kafka-cdc\.ps1'

            $script:e2eSetupText | Should -Not -Match 'cdc-setup\.yml'
            $script:e2eSetupText | Should -Not -Match '"cdc", "enable"'
            $script:e2eSetupText | Should -Not -Match 'Get-DmsToken'
        }
    }

    Context "teardown retires what the setup bound" {
        It "keeps the teardown wrapper a delegation with no CDC logic of its own" {
            $script:e2eTeardownText | Should -Match 'Invoke-E2EEngineAwareTeardown'
            $script:e2eTeardownText | Should -Not -Match 'cdc-setup\.yml'
            $script:e2eTeardownText | Should -Not -Match 'Invoke-CdcDestructiveTeardown'
        }

        It "documents that the destructive teardown retires the binding" {
            $script:e2eTeardownText | Should -Match 'binding record last'
            $script:e2eSetupText | Should -Match 'also retires any CDC binding this run created'
        }
    }
}

Describe "DMS-1323 operator and story documentation" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:composeReadmeText = Get-Content -LiteralPath (
            Join-Path $script:sourceDockerComposeRoot "README.md"
        ) -Raw
        $script:storyDocText = Get-Content -LiteralPath (
            Join-Path $script:sourceRepoRoot "reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md"
        ) -Raw
        $script:streamingDesignText = Get-Content -LiteralPath (
            Join-Path $script:sourceRepoRoot "reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md"
        ) -Raw
    }

    Context "the compose README describes the shipped opt-in" {
        It "no longer says connector registration is pending an implementation" {
            # The acceptance criterion for this task is that the stale claim is gone: an operator
            # reading it would not look for the switch that is now the documented path.
            $script:composeReadmeText | Should -Not -Match 'pending a separate implementation'
            $script:composeReadmeText | Should -Not -Match 'until that implementation lands'
            $script:composeReadmeText | Should -Not -Match 'does not register DMS source connectors\.'
        }

        It "documents the opt-in, its prerequisites, and where the binding state lives" {
            $script:composeReadmeText | Should -Match '(?m)^## Deployment-owned CDC \(Kafka Connect\)'
            $script:composeReadmeText | Should -Match '`-EnableKafkaCdc`'
            $script:composeReadmeText | Should -Match '`-CdcBindingStatePath`'
            $script:composeReadmeText | Should -Match 'DMS_CDC_CONNECT_IMAGE'
            $script:composeReadmeText | Should -Match 'immutable digest'
            $script:composeReadmeText | Should -Match '`\.cdc-state`'
        }

        It "documents that a normal stop retains the binding and -d -v retires it" {
            $script:composeReadmeText | Should -Match '\*\*retains\*\* the binding record'
            $script:composeReadmeText | Should -Match 'deletes the binding record last'
        }

        It "keeps the engine-neutral statement consistent on the MSSQL path" {
            # -EnableKafkaCdc starts the same services and registers a SQL Server connector, so the
            # MSSQL section must not read as "no CDC on this engine".
            $script:composeReadmeText | Should -Not -Match 'No Debezium CDC'
            $script:composeReadmeText | Should -Match 'registers a SQL Server connector on this engine'
        }
    }

    Context "the story file records the resolved contract" {
        It "carries a resolved-scope section in the sibling story's style" {
            $script:storyDocText |
                Should -Match '(?m)^## Resolved Bootstrap CDC Scope and Integration Contract'

            # Placement, CLI home, contract reuse, operator evidence, lag source, and engine
            # neutrality are the decisions a later reader of this story needs.
            $script:storyDocText | Should -Match '(?m)^### Placement and Boundary'
            $script:storyDocText | Should -Match '(?m)^### Command Surface and Operator Evidence'
            $script:storyDocText | Should -Match '(?m)^### Explicit Projection Target Evidence'
            $script:storyDocText | Should -Match '(?m)^### Lag and the Metrics Bridge'
            $script:storyDocText | Should -Match '(?m)^### Retirement and Teardown Ordering'
            $script:storyDocText | Should -Match '(?m)^### Local Bootstrap and E2E Entry Points'
        }

        It "states the decisions rather than gesturing at them" {
            $script:storyDocText | Should -Match '`Backend\.Cdc\.Control`'
            $script:storyDocText | Should -Match '`dms-document-cache` with a `cdc` verb group'
            $script:storyDocText | Should -Match 'created-for-initial-cdc-provisioning'
            $script:storyDocText | Should -Match 'MilliSecondsBehindSource'
            $script:storyDocText | Should -Match 'ENABLE_JOLOKIA=true'
            $script:storyDocText | Should -Match 'engine-neutral'
        }

        It "defers the normative rules to the owning design rather than restating them" {
            # cdc-streaming.md owns configuration, integration, deployment, readiness and
            # operations, and says stories must not repeat algorithms, fixed values, readiness
            # conditions or recovery rules. A second copy here is a second contract: it can drift
            # from the owner, and a reader cannot tell which one binds. The story records what was
            # built and links to the rule.
            $script:storyDocText |
                Should -Match '(?m)^### Downstream Publication History for the E18 Administrative Gate'
            $script:storyDocText | Should -Match 'design-docs/cdc/cdc-streaming\.md'
            $script:storyDocText | Should -Match 'is not restated here'

            # The rule itself lives with its owner.
            $owningDesignText = Get-Content -LiteralPath (
                Join-Path $script:sourceRepoRoot "reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md"
            ) -Raw
            $owningDesignText | Should -Match 'Internal-only is proved from durable deployment state'
            $owningDesignText | Should -Match 'Retirement therefore records the generation it'
        }

        It "places the section before the acceptance evidence, as the sibling story does" {
            $resolvedIndex = $script:storyDocText.IndexOf('## Resolved Bootstrap CDC Scope')
            $evidenceIndex = $script:storyDocText.IndexOf('## Acceptance Evidence')

            $resolvedIndex | Should -BeGreaterThan -1
            $evidenceIndex | Should -BeGreaterThan $resolvedIndex
        }
    }

    Context "the normative design document is left alone" {
        It "carries no implementation-state caveat about this story" {
            # Deferral and implementation-state notes belong next to the artifact - a metadata file,
            # a test comment, the story file - never in the canonical spec. The spec may (and does)
            # name the opt-in it requires, and may point at a story that owns a decision; what it
            # must not carry is a note about when something shipped or is still missing.
            $script:streamingDesignText | Should -Not -Match 'not yet implemented'
            $script:streamingDesignText | Should -Not -Match 'once (this|that) (ships|lands)'
            $script:streamingDesignText | Should -Not -Match 'pending a separate implementation'
            $script:streamingDesignText | Should -Not -Match 'until that implementation lands'
        }
    }
}
