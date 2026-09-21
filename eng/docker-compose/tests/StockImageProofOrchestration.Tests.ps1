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
        # After the module above, whose own nested import would otherwise unload this one from the
        # caller's session. ReadValuesFromEnvFile is how the phase commands read the file, so the
        # effective values below are read the way the deployment reads them.
        Import-Module (Join-Path $PSScriptRoot '../env-utility.psm1') -DisableNameChecking

        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

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

        It 'refuses when an inventory could not be taken at all' {
            # An empty result from a command that failed is not an empty inventory. Reading it as one
            # is how a preflight reports a clear host because Docker was unreachable.
            $result = Get-OccupiedHostResource -InventoryFailure @('docker ps -a exited 1')

            $result.Available | Should -BeFalse
            $result.Blocker -join ' ' | Should -Match 'not an empty one'
        }

        It 'refuses on a failed inventory even when everything else looks clear' {
            (Get-OccupiedHostResource -ExistingContainerName @() -ProjectContainer @() `
                    -InventoryFailure @('docker volume ls failed')).Available |
                Should -BeFalse
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

    Context 'the paths this run may own and delete' {
        BeforeAll {
            $script:repositoryRoot = [IO.Path]::GetFullPath((Join-Path $script:composeRoot '../..'))
            $script:safeWorkspace = Join-Path ([IO.Path]::GetTempPath()) 'dms1502-safe-workspace'
            $script:safeEvidence = Join-Path ([IO.Path]::GetTempPath()) 'dms1502-safe-evidence'

            function script:Test-Safety {
                param([string] $Workspace = $script:safeWorkspace, [string] $Evidence = $script:safeEvidence, [bool] $Exists = $false)

                return Test-ProofPathSafety -WorkspacePath $Workspace -EvidencePath $Evidence `
                    -RepositoryRoot $script:repositoryRoot -ComposeRoot $script:composeRoot -WorkspaceExists $Exists
            }
        }

        It 'allows two separate paths that do not exist yet' {
            (Test-Safety).Safe | Should -BeTrue
        }

        It 'refuses a workspace that already exists, rather than clearing it' {
            # Naming a directory does not make its contents this run's to delete. This is the
            # difference between owning a resource and pointing at one.
            $verdict = Test-Safety -Exists $true

            $verdict.Safe | Should -BeFalse
            $verdict.Blocker -join ' ' | Should -Match 'does not make its contents'
        }

        It 'refuses a workspace containing <_>, which removing it would take with it' -ForEach @(
            'the repository', 'the compose directory'
        ) {
            $workspace = if ($_ -eq 'the repository') { $script:repositoryRoot } else { $script:composeRoot }
            $verdict = Test-Safety -Workspace (Split-Path -Parent $workspace)

            $verdict.Safe | Should -BeFalse
            $verdict.Blocker -join ' ' | Should -Match 'would take'
        }

        It 'refuses the repository itself as a workspace' {
            (Test-Safety -Workspace $script:repositoryRoot).Safe | Should -BeFalse
        }

        It 'refuses a filesystem root' {
            $root = [IO.Path]::GetPathRoot([IO.Path]::GetTempPath())

            $verdict = Test-Safety -Workspace $root

            $verdict.Safe | Should -BeFalse
            $verdict.Blocker -join ' ' | Should -Match 'filesystem root'
        }

        It 'refuses a relative path, whose meaning depends on the working directory' {
            (Test-Safety -Workspace 'scratch').Safe | Should -BeFalse
        }

        It 'refuses evidence inside the workspace, which cleanup would erase' {
            $verdict = Test-Safety -Evidence (Join-Path $script:safeWorkspace 'evidence')

            $verdict.Safe | Should -BeFalse
            $verdict.Blocker -join ' ' | Should -Match 'record of why the run failed'
        }

        It 'refuses a workspace inside the evidence directory' {
            (Test-Safety -Workspace (Join-Path $script:safeEvidence 'workspace')).Safe | Should -BeFalse
        }

        It 'refuses the same path for both' {
            (Test-Safety -Evidence $script:safeWorkspace).Safe | Should -BeFalse
        }

        It 'permits a deletion inside an owned directory' {
            Test-OwnedDeletionPath -Path (Join-Path $script:safeWorkspace 'feed') -OwnedDirectory @($script:safeWorkspace) |
                Should -BeTrue
        }

        It 'permits deleting the owned directory itself' {
            Test-OwnedDeletionPath -Path $script:safeWorkspace -OwnedDirectory @($script:safeWorkspace) | Should -BeTrue
        }

        It 'refuses a deletion outside everything owned' {
            Test-OwnedDeletionPath -Path $script:repositoryRoot -OwnedDirectory @($script:safeWorkspace) | Should -BeFalse
        }

        It 'refuses a sibling whose name merely starts the same' {
            # "workspace-other" must not be admitted by a prefix comparison against "workspace".
            Test-OwnedDeletionPath -Path "$($script:safeWorkspace)-other" -OwnedDirectory @($script:safeWorkspace) |
                Should -BeFalse
        }

        It 'refuses anything when nothing is owned' {
            Test-OwnedDeletionPath -Path $script:safeWorkspace -OwnedDirectory @() | Should -BeFalse
        }

        It 'refuses a workspace reached through a link, rather than claiming to canonicalize it' {
            # Resolving a provider path does not follow a junction to its target, so the name this
            # checks and the directory a delete would reach can be different places. The ancestry is
            # refused instead of a canonicalization being asserted.
            $root = Join-Path ([IO.Path]::GetTempPath()) "dms1502-link-$([guid]::NewGuid().ToString('N'))"
            $target = Join-Path $root 'real'
            $link = Join-Path $root 'via-link'
            New-Item -ItemType Directory -Path $target -Force | Out-Null

            $linkCreated = $false
            try {
                try {
                    New-Item -ItemType Junction -Path $link -Target $target -ErrorAction Stop | Out-Null
                    $linkCreated = $true
                }
                catch {
                    Set-ItResult -Inconclusive -Because 'this machine does not permit creating a junction'
                    return
                }

                $verdict = Test-Safety -Workspace (Join-Path $link 'workspace')

                $verdict.Safe | Should -BeFalse
                $verdict.Blocker -join ' ' | Should -Match 'link or junction'
            }
            finally {
                if ($linkCreated) { Remove-Item -LiteralPath $link -Force -Recurse -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'the shared external network, absent versus unreadable' {
        It 'reports no attachments when the enumeration succeeded and did not name it' {
            $result = Get-NetworkAttachmentInventory -NetworkName 'dms' -ListExitCode 0 -ListedNetwork @('bridge', 'host')

            $result.Failure | Should -BeNullOrEmpty
            $result.Attachment | Should -HaveCount 0
        }

        It 'reports the attachments when it exists and can be inspected' {
            $result = Get-NetworkAttachmentInventory -NetworkName 'dms' -ListExitCode 0 -ListedNetwork @('dms') `
                -InspectExitCode 0 -InspectedAttachment @('someone-elses-api')

            $result.Failure | Should -BeNullOrEmpty
            $result.Attachment | Should -Contain 'someone-elses-api'
        }

        It 'fails when the network exists but cannot be inspected' {
            # A permission or daemon error fails the inspect too, and reading that as "no
            # neighbours" is how a run starts on a host it was never allowed to see.
            $result = Get-NetworkAttachmentInventory -NetworkName 'dms' -ListExitCode 0 -ListedNetwork @('dms') `
                -InspectExitCode 1

            $result.Failure | Should -Match 'could not be inspected'
        }

        It 'fails when the network list itself could not be read' {
            (Get-NetworkAttachmentInventory -NetworkName 'dms' -ListExitCode 1).Failure |
                Should -Match 'whether .* exists is unknown'
        }
    }

    Context 'a port actually being free, not merely unpublished by Docker' {
        It 'reports a free port as available' {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $free = $listener.LocalEndpoint.Port
            $listener.Stop()

            Test-LocalPortAvailable -Port $free | Should -BeTrue
        }

        It 'reports a port held by a non-Docker listener as unavailable' {
            # Docker's own publication list cannot see this, which is the case the container
            # inventory misses entirely.
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()

            try {
                Test-LocalPortAvailable -Port $listener.LocalEndpoint.Port | Should -BeFalse
            }
            finally {
                $listener.Stop()
            }
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

    Context 'the governed keys nothing else may decide' {
        It 'governs both image variables, the schema set and every plugin input' {
            $governed = Get-GovernedEnvironmentKey

            foreach ($key in @(
                    'DMS_STOCK_IMAGE_REFERENCE', 'DMS_CONFIG_DOCKER_IMAGE', 'DMS_IMAGE_TAG'
                    'SCHEMA_PACKAGES', 'DMS_CONFIG_DATA_STANDARD_VERSION'
                    'DMS_PLUGINS_COMPOSE_FILES', 'DMS_PLUGINS_ALLOWED', 'DMS_PLUGINS_MOUNT_SOURCE'
                    'PLUGIN_FEED_SOURCE', 'PLUGIN_PACKAGE_URL', 'PLUGIN_PACKAGE_SHA256', 'PLUGIN_NAME')) {
                $governed | Should -Contain $key
            }
        }

        It 'reports an ambient <_>, which Compose would prefer over the env file' -ForEach @(
            'SCHEMA_PACKAGES', 'DMS_CONFIG_DOCKER_IMAGE', 'DMS_IMAGE_TAG', 'PLUGIN_PACKAGE_SHA256'
        ) {
            # A process variable wins over --env-file, so an ambient one of these decides what the
            # proof runs against and the pin becomes a description of something else.
            Get-AmbientOverride -ProcessEnvironment @{ $_ = 'anything' } | Should -Contain $_
        }

        It 'reports an ambient key set to empty, which still wins' {
            Get-AmbientOverride -ProcessEnvironment @{ 'SCHEMA_PACKAGES' = '' } | Should -Contain 'SCHEMA_PACKAGES'
        }

        It 'ignores ambient variables it does not govern' {
            Get-AmbientOverride -ProcessEnvironment @{ 'PATH' = '/usr/bin'; 'POSTGRES_PASSWORD' = 'abc' } |
                Should -HaveCount 0
        }

        It 'reports nothing for a clean environment' {
            Get-AmbientOverride -ProcessEnvironment @{} | Should -HaveCount 0
        }

        It 'refuses an ambient <_> although its value is kept from the base file' -ForEach @(
            'DMS_HTTP_PORTS', 'DMS_CONFIG_ASPNETCORE_HTTP_PORTS', 'POSTGRES_PORT'
        ) {
            # Not governed, because moving them would move the port the container listens on. Still
            # refused ambiently, because Compose and Get-EnvValue both prefer a process variable
            # while the preflight and the URLs read only the file.
            Get-GovernedEnvironmentKey | Should -Not -Contain $_
            Get-AmbientRefusedKey | Should -Contain $_
            Get-AmbientOverride -ProcessEnvironment @{ $_ = '9999' } | Should -Contain $_
        }
    }

    Context 'the ports read out of the environment file' {
        It 'accepts three declared, in-range ports' {
            $verdict = Test-ProofPort -Key @('A', 'B') -EnvironmentValue @{ A = '8080'; B = '5435' }

            $verdict.Valid | Should -BeTrue
            $verdict.Port['A'] | Should -Be 8080
        }

        It 'refuses a key the file does not declare' {
            # An absent key reads as an empty string and would become port 0, which binds to
            # whatever the operating system hands out and makes the preflight's answer meaningless.
            $verdict = Test-ProofPort -Key @('A') -EnvironmentValue @{}

            $verdict.Valid | Should -BeFalse
            $verdict.Blocker -join ' ' | Should -Match 'declares no A'
        }

        It 'refuses <Case>' -ForEach @(
            @{ Case = 'an empty value'; Value = '' }
            @{ Case = 'a non-numeric value'; Value = 'eighty-eighty' }
            @{ Case = 'zero'; Value = '0' }
            @{ Case = 'a negative port'; Value = '-1' }
            @{ Case = 'a port above the range'; Value = '65536' }
        ) {
            (Test-ProofPort -Key @('A') -EnvironmentValue @{ A = $Value }).Valid | Should -BeFalse
        }

        It 'reports every unusable port rather than the first' {
            $verdict = Test-ProofPort -Key @('A', 'B', 'C') -EnvironmentValue @{ A = '0'; B = 'x'; C = '8080' }

            $verdict.Blocker | Should -HaveCount 2
        }
    }

    Context 'the environment file the deployment runs from' {
        BeforeAll {
            # The real base file, not a stub: it already declares DMS_IMAGE_TAG and a MULTI-LINE
            # SCHEMA_PACKAGES block, which is exactly the case an appended override would leave
            # ambiguous.
            $script:baseContent = Get-Content -Raw -LiteralPath (Join-Path $script:composeRoot '.env.e2e')
            $script:content = Get-StockProofEnvironmentContent -Pin (New-Pin) `
                -BaseContent $script:baseContent `
                -PluginComposeFiles 'plugins-dms.yml;tests/plugin-deployment/plugins-allowed-dms.yml' `
                -PluginMountSource '/scratch/plugins' `
                -AllowedPlugins 'Acme.CustomValidationProof'

            # Read back the way the phase commands read it, so these are effective values rather
            # than lines that happen to be present.
            $script:effectiveFile = Join-Path ([IO.Path]::GetTempPath()) "dms1502-env-$([guid]::NewGuid().ToString('N')).env"
            Set-Content -LiteralPath $script:effectiveFile -Value $script:content -Encoding utf8
            $script:effective = ReadValuesFromEnvFile $script:effectiveFile
        }

        AfterAll {
            Remove-Item -LiteralPath $script:effectiveFile -Force -ErrorAction SilentlyContinue
        }

        It 'keeps what the base file said about everything it does not govern' {
            $script:effective['POSTGRES_DB_NAME'] | Should -Not -BeNullOrEmpty
            $script:effective.ContainsKey('ROUTE_QUALIFIER_SEGMENTS') | Should -BeTrue
        }

        It 'pins the two images independently, from their own repositories' {
            $script:effective['DMS_STOCK_IMAGE_REFERENCE'] |
                Should -BeExactly ('edfialliance/ed-fi-api:8.0.1-alpha.0.7@sha256:' + ('a' * 64))
            $script:effective['DMS_CONFIG_DOCKER_IMAGE'] |
                Should -BeExactly ('edfialliance/ed-fi-api-configuration-service@sha256:' + ('b' * 64))
        }

        It 'removes the base file''s DMS_IMAGE_TAG rather than leaving it to be preferred' {
            # .env.e2e declares DMS_IMAGE_TAG=pre. A digest belongs to one repository, so nothing in
            # this file may still be able to answer the image question.
            $script:baseContent | Should -Match '(?m)^DMS_IMAGE_TAG='
            $script:effective.ContainsKey('DMS_IMAGE_TAG') | Should -BeFalse
        }

        It 'declares each governed key exactly once in the composed file' {
            # The base declares some of them already, so "appended last" is not the same as "said
            # once", and which one wins would depend on the reader.
            foreach ($key in @('SCHEMA_PACKAGES', 'DMS_CONFIG_DOCKER_IMAGE', 'DMS_STOCK_IMAGE_REFERENCE', 'DMS_PLUGINS_ALLOWED')) {
                @($script:content -split "`n" | Where-Object { $_ -match ('^' + [regex]::Escape($key) + '=') }) |
                    Should -HaveCount 1 -Because "$key must be declared once"
            }
        }

        It 'replaces the base file''s multi-line schema block with the pin''s set' {
            # The base carries SCHEMA_PACKAGES as a quoted block spanning many lines. The effective
            # value has to be the pin's, on one line, and none of the base's packages.
            $script:baseContent | Should -Match "(?m)^SCHEMA_PACKAGES='\[\s*$"
            $script:effective['SCHEMA_PACKAGES'] | Should -Match '^''\['
            $script:effective['SCHEMA_PACKAGES'] | Should -Match '\]''$'
            $script:effective['SCHEMA_PACKAGES'] | Should -Match 'EdFi\.DataStandard52\.ApiSchema'
            $script:effective['SCHEMA_PACKAGES'] | Should -Not -Match 'TPDM'
        }

        It 'keeps a single schema package an array rather than an object' {
            # ConvertTo-Json renders a one-element set as an object, and SCHEMA_PACKAGES is always an
            # array, so a one-package pin is exactly where this would break.
            $json = $script:effective['SCHEMA_PACKAGES'] -replace "^'", '' -replace "'$", ''

            @($json | ConvertFrom-Json) | Should -HaveCount 1
        }

        It 'carries the plugin overlay list and the allowlist through' {
            $script:effective['DMS_PLUGINS_COMPOSE_FILES'] |
                Should -BeExactly 'plugins-dms.yml;tests/plugin-deployment/plugins-allowed-dms.yml'
            $script:effective['DMS_PLUGINS_ALLOWED'] | Should -BeExactly 'Acme.CustomValidationProof'
        }

        It 'writes the mount source it was given and omits the recipe 2 values it was not' {
            $script:effective['DMS_PLUGINS_MOUNT_SOURCE'] | Should -BeExactly '/scratch/plugins'
            $script:effective.ContainsKey('PLUGIN_PACKAGE_URL') | Should -BeFalse
            $script:effective.ContainsKey('PLUGIN_FEED_SOURCE') | Should -BeFalse
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

        It 'governs no port key, so the stack keeps the ports its environment file declares' {
            # DMS_HTTP_PORTS publishes '127.0.0.1:${VAR}:${VAR}', one variable on both sides, so
            # moving it would move the port the container itself listens on while six addresses in
            # the base file name ed-fi-api-config:8081 directly. The preflight reads these declared
            # values instead of a second list.
            Get-GovernedEnvironmentKey | Should -Not -Contain 'DMS_HTTP_PORTS'
            Get-GovernedEnvironmentKey | Should -Not -Contain 'DMS_CONFIG_ASPNETCORE_HTTP_PORTS'

            $script:effective['DMS_HTTP_PORTS'] | Should -BeExactly '8080'
            $script:effective['DMS_CONFIG_ASPNETCORE_HTTP_PORTS'] | Should -BeExactly '8081'
        }

        It 'writes an empty allowlist rather than omitting it' {
            # An allowlisted-nothing deployment has to differ from one that never set the key.
            $content = Get-StockProofEnvironmentContent -Pin (New-Pin) -BaseContent 'X=1' `
                -PluginComposeFiles 'plugins-dms.yml' -PluginMountSource '/scratch/plugins'

            ($content -split "`n") | Should -Contain 'DMS_PLUGINS_ALLOWED='
        }
    }
}
