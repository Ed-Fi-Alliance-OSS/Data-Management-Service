# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1502: the orchestration decisions, driven against synthetic inventories. What this proof is
# allowed to touch on a shared host, what it must put back after a partial failure, and how the pin
# reaches the deployment are each answerable without starting anything.

Describe 'Stock image proof orchestration' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot 'plugin-deployment/stock-image-proof.psm1') -Force

        function script:New-Pin {
            return [pscustomobject]@{
                edFiApi              = [pscustomobject]@{
                    repository = 'edfialliance/ed-fi-api'
                    tag        = '8.0.1-alpha.0.7'
                    digest     = 'sha256:' + ('a' * 64)
                }
                configurationService = [pscustomobject]@{
                    repository = 'edfialliance/ed-fi-api-configuration-service'
                    digest     = 'sha256:' + ('b' * 64)
                }
                provisioning         = [pscustomobject]@{
                    schemaToolsPackageVersion = '8.0.1-alpha.0.7'
                    dataStandardVersion       = '5.2'
                    schemaPackages            = @(
                        [pscustomobject]@{ name = 'EdFi.DataStandard52.ApiSchema'; version = '1.0.335'; feedUrl = 'https://feed/index.json' }
                    )
                }
            }
        }
    }

    Context 'the occupied-host preflight' {
        It 'allows a host with nothing in the way' {
            (Get-OccupiedHostResource -RequiredPort @(18080) -BoundPort @(5432)).Available | Should -BeTrue
        }

        It 'refuses the fixed container name <_>, which no project name can move' -ForEach @(
            'dms-postgresql', 'ed-fi-api-config-service', 'ed-fi-api-swagger-ui', 'dms-keycloak', 'ed-fi-api'
        ) {
            # postgresql.yml and its siblings hard-code container_name, so a running one of these
            # collides whatever compose project this run uses.
            $result = Get-OccupiedHostResource -ExistingContainerName @($_)

            $result.Available | Should -BeFalse
            $result.Blocker -join ' ' | Should -Match ([regex]::Escape($_))
        }

        It 'ignores an unrelated container that shares no fixed name' {
            (Get-OccupiedHostResource -ExistingContainerName @('someone-elses-postgres', 'hophome-db')).Available |
                Should -BeTrue
        }

        It 'refuses a leftover container from the compose project' {
            $result = Get-OccupiedHostResource -ProjectContainer @('dms-published-dms-1')

            $result.Available | Should -BeFalse
            $result.Blocker -join ' ' | Should -Match 'not torn down'
        }

        It 'refuses a leftover volume, which would carry state into this run' {
            $result = Get-OccupiedHostResource -ProjectVolume @('dms-published_plugins')

            $result.Available | Should -BeFalse
            $result.Blocker -join ' ' | Should -Match 'carry state'
        }

        It 'refuses a foreign container attached to the shared external network' {
            $result = Get-OccupiedHostResource -NetworkAttachment @('someone-elses-api')

            $result.Available | Should -BeFalse
            $result.Blocker -join ' ' | Should -Match 'was not created by this run'
        }

        It 'does not refuse the shared external network for merely existing' {
            # It is pre-existing infrastructure other stacks join, and this run neither creates nor
            # removes it. An empty network is not a neighbour.
            (Get-OccupiedHostResource -NetworkAttachment @()).Available | Should -BeTrue
        }

        It 'refuses an existing bootstrap workspace' {
            $result = Get-OccupiedHostResource -BootstrapPresent $true

            $result.Available | Should -BeFalse
            $result.Blocker -join ' ' | Should -Match 'fixed path shared with the local stack'
        }

        It 'refuses a bound port it needs, and ignores a bound port it does not' {
            (Get-OccupiedHostResource -RequiredPort @(18080) -BoundPort @(18080)).Available | Should -BeFalse
            (Get-OccupiedHostResource -RequiredPort @(18080) -BoundPort @(8080)).Available | Should -BeTrue
        }

        It 'reports every blocker rather than only the first' {
            # An operator clearing one obstruction at a time and rerunning is the worst outcome here.
            $result = Get-OccupiedHostResource -ExistingContainerName @('dms-postgresql') `
                -ProjectVolume @('dms-published_plugins') -BootstrapPresent $true `
                -RequiredPort @(18080) -BoundPort @(18080)

            $result.Available | Should -BeFalse
            $result.Blocker.Count | Should -Be 4
        }
    }

    Context 'ownership and the cleanup that has to survive a partial failure' {
        It 'plans nothing when nothing was claimed' {
            # The preflight-refusal case: a workflow always() step must not tear down a stack this
            # run never created.
            Get-CleanupPlan -Ownership (Get-StockProofOwnership) | Should -HaveCount 0
        }

        It 'plans nothing for a null ownership record' {
            Get-CleanupPlan -Ownership $null | Should -HaveCount 0
        }

        It 'plans cleanup for a resource claimed before the operation that failed to finish' {
            # Ownership is taken ahead of the attempt precisely so a compose up that created
            # containers and then timed out is still torn down.
            $ownership = Get-StockProofOwnership
            Add-OwnedResource -Ownership $ownership -Kind 'ComposeProject' -Name 'dms-published' | Out-Null

            $plan = Get-CleanupPlan -Ownership $ownership

            $plan | Should -HaveCount 1
            $plan[0].Name | Should -BeExactly 'dms-published'
        }

        It 'tears the compose project down before the directories its containers hold open' {
            $ownership = Get-StockProofOwnership
            Add-OwnedResource -Ownership $ownership -Kind 'Directory' -Name '/scratch/workspace' | Out-Null
            Add-OwnedResource -Ownership $ownership -Kind 'ComposeProject' -Name 'dms-published' | Out-Null

            $plan = Get-CleanupPlan -Ownership $ownership

            $plan[0].Kind | Should -BeExactly 'ComposeProject'
            $plan[-1].Kind | Should -BeExactly 'Directory'
        }

        It 'claims a resource once however many times it is recorded' {
            $ownership = Get-StockProofOwnership
            Add-OwnedResource -Ownership $ownership -Kind 'ComposeProject' -Name 'dms-published' | Out-Null
            Add-OwnedResource -Ownership $ownership -Kind 'ComposeProject' -Name 'dms-published' | Out-Null

            Get-CleanupPlan -Ownership $ownership | Should -HaveCount 1
        }
    }

    Context 'restoring exactly what the pin names' {
        It 'brackets a version so NuGet resolves it rather than treating it as a floor' {
            Get-ContractPackageReference -Version '1.0.0' | Should -BeExactly '[1.0.0]'
        }

        It 'brackets a prerelease version too' {
            Get-ContractPackageReference -Version '1.0.0-alpha.3' | Should -BeExactly '[1.0.0-alpha.3]'
        }

        It 'refuses a version that is already range syntax rather than doubling the brackets' {
            { Get-ContractPackageReference -Version '[1.0.0]' } | Should -Throw '*already NuGet range syntax*'
        }

        It 'accepts a restore that produced the pinned version' {
            (Test-RestoredPackageVersion -PackageId 'EdFi.Api.Plugins' -ExpectedVersion '1.0.0' -RestoredVersion '1.0.0').Verified |
                Should -BeTrue
        }

        It 'refuses a restore that produced something else' {
            # Bracketing asks; this establishes that the ask was honoured.
            $verdict = Test-RestoredPackageVersion -PackageId 'EdFi.Api.Plugins' -ExpectedVersion '1.0.0' -RestoredVersion '1.1.0'

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'restored as 1\.1\.0'
        }

        It 'refuses an unobserved restore rather than assuming it worked' {
            (Test-RestoredPackageVersion -PackageId 'EdFi.Api.Plugins' -ExpectedVersion '1.0.0' -RestoredVersion '').Verified |
                Should -BeFalse
        }

        It 'installs the released SchemaTools at an exact version into its own tool path' {
            $argument = Get-SchemaToolInstallArgument -Version '8.0.1-alpha.0.7' -ToolPath '/scratch/tools' -FeedUrl 'https://feed/index.json'

            $argument -join ' ' | Should -BeExactly 'tool install EdFi.Api.SchemaTools --tool-path /scratch/tools --version 8.0.1-alpha.0.7 --add-source https://feed/index.json'
        }

        It 'requires every install input rather than defaulting one' {
            { Get-SchemaToolInstallArgument -Version '' -ToolPath '/t' -FeedUrl 'https://f' } | Should -Throw
            { Get-SchemaToolInstallArgument -Version '1.0.0' -ToolPath '' -FeedUrl 'https://f' } | Should -Throw
            { Get-SchemaToolInstallArgument -Version '1.0.0' -ToolPath '/t' -FeedUrl '' } | Should -Throw
        }
    }

    Context 'the environment file the deployment runs from' {
        BeforeAll {
            $script:content = Get-StockProofEnvironmentContent -Pin (New-Pin) `
                -BaseContent "POSTGRES_PASSWORD=abc`nDMS_HTTP_PORTS=8080" `
                -PluginComposeFiles 'plugins-dms.yml;tests/plugin-deployment/plugins-allowed-dms.yml' `
                -PluginMountSource '/scratch/plugins' `
                -AllowedPlugins 'Acme.CustomValidationProof'
            $script:line = $script:content -split "`n"
        }

        It 'keeps what the base file already said' {
            $script:line | Should -Contain 'POSTGRES_PASSWORD=abc'
        }

        It 'pins the two images independently, from their own repositories' {
            $script:line | Should -Contain ('DMS_STOCK_IMAGE_REFERENCE=edfialliance/ed-fi-api:8.0.1-alpha.0.7@sha256:' + ('a' * 64))
            $script:line | Should -Contain ('DMS_CONFIG_DOCKER_IMAGE=edfialliance/ed-fi-api-configuration-service@sha256:' + ('b' * 64))
        }

        It 'never writes DMS_IMAGE_TAG, which would select both images at once' {
            # A digest belongs to one repository, so the shared variable cannot express this pin.
            $script:content | Should -Not -Match '(?m)^DMS_IMAGE_TAG='
        }

        It 'writes SCHEMA_PACKAGES exactly once, so preparation and the container agree' {
            @($script:line | Where-Object { $_ -like 'SCHEMA_PACKAGES=*' }) | Should -HaveCount 1
        }

        It 'writes the schema package set as a single-line quoted JSON array' {
            $schema = @($script:line | Where-Object { $_ -like 'SCHEMA_PACKAGES=*' })[0]

            $schema | Should -Match "^SCHEMA_PACKAGES='\["
            $schema | Should -Match "\]'$"
            $schema | Should -Match 'EdFi\.DataStandard52\.ApiSchema'
            $schema | Should -Match '1\.0\.335'
        }

        It 'keeps a single schema package an array rather than an object' {
            # ConvertTo-Json renders a one-element set as an object, and SCHEMA_PACKAGES is always an
            # array, so a one-package pin is exactly where this would break.
            $schema = @($script:line | Where-Object { $_ -like 'SCHEMA_PACKAGES=*' })[0]
            $json = $schema -replace "^SCHEMA_PACKAGES='", '' -replace "'$", ''

            ($json | ConvertFrom-Json) | Should -HaveCount 1
        }

        It 'carries the plugin overlay list and the allowlist through' {
            $script:line | Should -Contain 'DMS_PLUGINS_COMPOSE_FILES=plugins-dms.yml;tests/plugin-deployment/plugins-allowed-dms.yml'
            $script:line | Should -Contain 'DMS_PLUGINS_ALLOWED=Acme.CustomValidationProof'
        }

        It 'writes the mount source it was given and omits the recipe 2 values it was not' {
            $script:line | Should -Contain 'DMS_PLUGINS_MOUNT_SOURCE=/scratch/plugins'
            $script:content | Should -Not -Match '(?m)^PLUGIN_PACKAGE_URL='
            $script:content | Should -Not -Match '(?m)^PLUGIN_FEED_SOURCE='
        }

        It 'writes the recipe 2 values when they are supplied' {
            $content = Get-StockProofEnvironmentContent -Pin (New-Pin) -BaseContent 'X=1' `
                -PluginComposeFiles 'plugins-fetch-dms.yml' `
                -PluginFeedSource '/scratch/feed' `
                -PluginPackageUrl 'http://plugin-feed:8080/a/1.0.0/a.1.0.0.nupkg' `
                -PluginPackageSha256 ('0' * 64) `
                -PluginName 'Acme.CustomValidationProof'

            $line = $content -split "`n"
            $line | Should -Contain 'PLUGIN_FEED_SOURCE=/scratch/feed'
            $line | Should -Contain ('PLUGIN_PACKAGE_SHA256=' + ('0' * 64))
            $line | Should -Contain 'PLUGIN_NAME=Acme.CustomValidationProof'
        }

        It 'writes an empty allowlist rather than omitting it' {
            # The disabled-plugin deployment asserts an otherwise identical boot, so the key has to
            # be present and empty rather than absent.
            $content = Get-StockProofEnvironmentContent -Pin (New-Pin) -BaseContent 'X=1' `
                -PluginComposeFiles 'plugins-dms.yml' -PluginMountSource '/scratch/plugins'

            ($content -split "`n") | Should -Contain 'DMS_PLUGINS_ALLOWED='
        }
    }
}
