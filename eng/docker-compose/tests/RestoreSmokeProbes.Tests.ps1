# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Behavioral tests for the restore smoke's decision and evidence helpers (RestoreSmokeProbes.psm1)
# and for the smoke's complete foreign-stack rejection path. No test reaches a real Docker daemon
# or a real CMS/DMS: module tests mock docker/dotnet/pwsh and the CMS/DMS helpers, and the
# rejection-path tests run a sandbox copy of the smoke in a child pwsh whose start/bootstrap
# scripts are logging stubs and whose docker/dotnet are logging fakes.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'Mock bodies execute in the mocked module''s session state, where test-scope locals are invisible; global variables are the documented crossing mechanism and are removed in AfterAll.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Test helpers mirror the module''s plural-noun contracts.')]
param()

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1") -Force

    # Pester's Mock needs a resolvable command; environments without docker get an inert global
    # function the mocks then replace.
    $script:createdDockerFallback = $false
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
        $script:createdDockerFallback = $true
    }

    function script:Invoke-TestGit {
        # A fixed identity and no signing, so commits work on any runner.
        param([string]$Directory, [string[]]$Arguments)

        $output = & git -C $Directory -c user.name=restore-smoke-test -c user.email=restore-smoke-test@example.invalid -c commit.gpgsign=false @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed in '$Directory': $($output | Out-String)"
        }
        return $output
    }

    function script:New-CleanTestRepository {
        param([string]$Directory)

        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        $null = Invoke-TestGit -Directory $Directory -Arguments @("init", "-q")
        $null = Invoke-TestGit -Directory $Directory -Arguments @("config", "core.autocrlf", "false")
        $null = Invoke-TestGit -Directory $Directory -Arguments @("add", "-A")
        $null = Invoke-TestGit -Directory $Directory -Arguments @("commit", "-q", "--allow-empty", "-m", "sandbox")
        return ([string](Invoke-TestGit -Directory $Directory -Arguments @("rev-parse", "HEAD"))).Trim()
    }
}

AfterAll {
    if ($script:createdDockerFallback) {
        Remove-Item function:global:docker -ErrorAction SilentlyContinue
    }
    Remove-Variable -Name RestoreSmokeTestDocker, RestoreSmokeTestTargetDir, RestoreSmokeTestPwshExit, RestoreSmokeImageTestState, RestoreSmokeTestVolumes, RestoreSmokeTestVolumeCalls, RestoreSmokeApiTest -Scope Global -ErrorAction SilentlyContinue
}

Describe "Get-RestoreSmokeWrapperProfile" {
    It "maps <wrapper> to its bootstrap wrapper, teardown script, and compose project" -ForEach @(
        @{ Wrapper = "local"; Bootstrap = "bootstrap-local-dms.ps1"; Teardown = "start-local-dms.ps1"; Project = "dms-local" }
        @{ Wrapper = "published"; Bootstrap = "bootstrap-published-dms.ps1"; Teardown = "start-published-dms.ps1"; Project = "dms-published" }
    ) {
        $wrapperProfile = Get-RestoreSmokeWrapperProfile -Wrapper $Wrapper

        $wrapperProfile.BootstrapScriptName | Should -Be $Bootstrap
        $wrapperProfile.TeardownScriptName | Should -Be $Teardown
        $wrapperProfile.ComposeProject | Should -Be $Project
    }
}

Describe "Get-RestoreSmokeWrapperArgumentSet" {
    It "forwards the local default 5.2 when the caller did not supply -DataStandardVersion" {
        $arguments = Get-RestoreSmokeWrapperArgumentSet -Arguments @{ DatabaseEngine = "postgresql" } -Wrapper local -DataStandardVersion "5.2" -DataStandardVersionSupplied $false

        $arguments.DataStandardVersion | Should -Be "5.2"
    }

    It "forwards an explicit 6.1 to the local wrapper" {
        $arguments = Get-RestoreSmokeWrapperArgumentSet -Arguments @{} -Wrapper local -DataStandardVersion "6.1" -DataStandardVersionSupplied $true

        $arguments.DataStandardVersion | Should -Be "6.1"
    }

    It "omits DataStandardVersion for the published wrapper when the caller did not supply it" {
        $arguments = Get-RestoreSmokeWrapperArgumentSet -Arguments @{ DatabaseEngine = "mssql" } -Wrapper published -DataStandardVersion "5.2" -DataStandardVersionSupplied $false

        $arguments.ContainsKey("DataStandardVersion") | Should -BeFalse
        $arguments.DatabaseEngine | Should -Be "mssql"
    }

    It "forwards an explicit <version> to the published wrapper, even when it equals the default" -ForEach @(
        @{ Version = "5.2" }
        @{ Version = "6.1" }
    ) {
        $arguments = Get-RestoreSmokeWrapperArgumentSet -Arguments @{} -Wrapper published -DataStandardVersion $Version -DataStandardVersionSupplied $true

        $arguments.DataStandardVersion | Should -Be $Version
    }

    It "does not modify the caller's hashtable" {
        $original = @{ RestoreTemplate = "Minimal" }

        $null = Get-RestoreSmokeWrapperArgumentSet -Arguments $original -Wrapper local -DataStandardVersion "5.2" -DataStandardVersionSupplied $false

        $original.Keys | Should -Be @("RestoreTemplate")
    }

    It "rejects a DataStandardVersion that bypasses the selection rule" {
        { Get-RestoreSmokeWrapperArgumentSet -Arguments @{ DataStandardVersion = "6.1" } -Wrapper published -DataStandardVersion "5.2" -DataStandardVersionSupplied $false } |
            Should -Throw "*must not carry DataStandardVersion directly*"
    }
}

Describe "Resolve-RestoreSmokeStandardVersion" {
    It "defaults the package segment from the Data Standard version" {
        Resolve-RestoreSmokeStandardVersion -StandardVersion "" -DataStandardVersion "6.1" | Should -Be "6.1.0"
        Resolve-RestoreSmokeStandardVersion -StandardVersion "" -DataStandardVersion "5.2" | Should -Be "5.2.0"
    }

    It "keeps an explicit -StandardVersion" {
        Resolve-RestoreSmokeStandardVersion -StandardVersion "5.2.1" -DataStandardVersion "6.1" | Should -Be "5.2.1"
    }
}

Describe "Get-RestoreSmokeForeignStackInventory and Assert-RestoreSmokeNoForeignStack" {
    BeforeAll {
        function script:Set-DockerInventoryMock {
            param([hashtable]$State, [switch]$Fail)

            $global:RestoreSmokeTestDocker = @{ State = $State; Fail = [bool]$Fail }
            Mock docker -ModuleName RestoreSmokeProbes {
                $filter = [string]($args | Where-Object { "$_" -like "label=com.docker.compose.project=*" } | Select-Object -First 1)
                $project = $filter.Substring("label=com.docker.compose.project=".Length)
                if ($global:RestoreSmokeTestDocker.Fail) {
                    $global:LASTEXITCODE = 1
                    return "Cannot connect to the Docker daemon"
                }
                $global:LASTEXITCODE = 0
                $projectState = $global:RestoreSmokeTestDocker.State[$project]
                if ($null -eq $projectState) {
                    return
                }
                if ($args[0] -eq "ps") {
                    return @($projectState.Containers)
                }
                return @($projectState.Volumes)
            }
        }
    }

    It "reports no foreign state when neither project has containers or volumes, after querying both projects" {
        Set-DockerInventoryMock -State @{}

        $inventory = Get-RestoreSmokeForeignStackInventory

        $inventory.HasForeignState | Should -BeFalse
        @($inventory.Projects.Project) | Should -Be @("dms-local", "dms-published")
        foreach ($project in @("dms-local", "dms-published")) {
            Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter {
                $args[0] -eq "ps" -and $args -contains "-a" -and $args -contains "label=com.docker.compose.project=$project"
            }
            Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter {
                $args[0] -eq "volume" -and $args[1] -eq "ls" -and $args -contains "label=com.docker.compose.project=$project"
            }
        }
        { Assert-RestoreSmokeNoForeignStack -Inventory $inventory } | Should -Not -Throw
    }

    It "refuses <case> and names them" -ForEach @(
        @{ Case = "stopped containers"; State = @{ "dms-local" = @{ Containers = @("ed-fi-api|exited|dms", "dms-postgresql|exited|db"); Volumes = @() } }; Expected = @("ed-fi-api (exited)", "dms-postgresql (exited)") }
        @{ Case = "retained volumes without containers"; State = @{ "dms-local" = @{ Containers = @(); Volumes = @("dms-local_dms-postgresql", "dms-local_dms-mssql-2025") } }; Expected = @("dms-local_dms-postgresql", "dms-local_dms-mssql-2025") }
        @{ Case = "running containers"; State = @{ "dms-local" = @{ Containers = @("ed-fi-api|running|dms"); Volumes = @() } }; Expected = @("ed-fi-api (running)") }
        @{ Case = "a published-project stack"; State = @{ "dms-published" = @{ Containers = @("ed-fi-api-config-service|exited|config"); Volumes = @("dms-published_dms-postgresql") } }; Expected = @("dms-published:", "ed-fi-api-config-service (exited)", "dms-published_dms-postgresql") }
    ) {
        Set-DockerInventoryMock -State $State

        $inventory = Get-RestoreSmokeForeignStackInventory

        $inventory.HasForeignState | Should -BeTrue
        $failure = { Assert-RestoreSmokeNoForeignStack -Inventory $inventory }
        $failure | Should -Throw "Refusing to run: Docker already holds a stack this smoke did not create*Nothing was stopped or removed*"
        foreach ($text in $Expected) {
            $failure | Should -Throw "*$text*"
        }
    }

    It "lets a confirmed removal proceed" {
        Set-DockerInventoryMock -State @{ "dms-local" = @{ Containers = @("ed-fi-api|exited|dms"); Volumes = @("dms-local_dms-postgresql") } }

        $inventory = Get-RestoreSmokeForeignStackInventory

        { Assert-RestoreSmokeNoForeignStack -Inventory $inventory -ConfirmForeignStackRemoval } | Should -Not -Throw
        $inventory.ForeignProjects | Should -Be @("dms-local")
    }

    It "fails closed when docker cannot list the guarded projects" {
        Set-DockerInventoryMock -State @{} -Fail

        { Get-RestoreSmokeForeignStackInventory } | Should -Throw "Refusing to run: could not list containers*Foreign Docker state is unknown*"
    }
}

BeforeDiscovery {
    # Every leftover-volume case runs for both engines: the allow rule and the selected-engine
    # block rule swap between them, and the other block rules must hold under either.
    $script:engineCases = @(
        @{ Engine = "postgresql"; SelectedKey = "dms-postgresql"; SelectedFile = "postgresql.yml"; OtherEngine = "mssql"; OtherKey = "dms-mssql-2025"; OtherFile = "mssql.yml" }
        @{ Engine = "mssql"; SelectedKey = "dms-mssql-2025"; SelectedFile = "mssql.yml"; OtherEngine = "postgresql"; OtherKey = "dms-postgresql"; OtherFile = "postgresql.yml" }
    )

    # Each case changes one thing about a volume that would otherwise be allowed (the other
    # engine's storage, correctly labelled), so the block comes from that change alone.
    $blockedTemplates = @(
        @{ Case = "the inspection fails"; Name = "dms-local_{other}"; Entry = @{ Fail = "Error response from daemon: permission denied" }; Expected = "inspection failed (docker exit 1: Error response from daemon: permission denied)" }
        @{ Case = "the inspection output is not JSON"; Name = "dms-local_{other}"; Entry = @{ Raw = "not json" }; Expected = "the inspection output is not one JSON object" }
        @{ Case = "the inspection output is a JSON array"; Name = "dms-local_{other}"; Entry = @{ Raw = '[{"Name":"dms-local_{other}"}]' }; Expected = "the inspection output is not one JSON object" }
        @{ Case = "the inspection returns another volume"; Name = "dms-local_{other}"; Entry = @{ InspectedName = "dms-local_elsewhere"; Labels = "default" }; Expected = "the inspection returned volume 'dms-local_elsewhere' instead" }
        @{ Case = "the volume has no labels"; Name = "dms-local_{other}"; Entry = @{ Labels = $null }; Expected = "it has no com.docker.compose.project label" }
        @{ Case = "the project label is missing"; Name = "dms-local_{other}"; Entry = @{ Labels = @{ "com.docker.compose.volume" = "{other}" } }; Expected = "it has no com.docker.compose.project label" }
        @{ Case = "the project label conflicts with the listed project"; Name = "dms-local_{other}"; Entry = @{ Labels = @{ "com.docker.compose.project" = "dms-published"; "com.docker.compose.volume" = "{other}" } }; Expected = "its com.docker.compose.project label is 'dms-published', but it was listed under 'dms-local'" }
        @{ Case = "the volume label is missing"; Name = "dms-local_{other}"; Entry = @{ Labels = @{ "com.docker.compose.project" = "dms-local" } }; Expected = "it has no com.docker.compose.volume label" }
        @{ Case = "the name does not match the labels (a renamed volume)"; Name = "edfi-{other}-copy"; Entry = @{ Labels = @{ "com.docker.compose.project" = "dms-local"; "com.docker.compose.volume" = "{other}" } }; Expected = "its name does not match its compose labels (expected 'dms-local_{other}')" }
        @{ Case = "the volume label differs only in case from a declared volume"; Name = "dms-local_{OTHER}"; Entry = @{ Labels = "default" }; Expected = "volume '{OTHER}' is not declared by any engine compose file (postgresql.yml, mssql.yml), so it cannot be identified" }
        @{ Case = "the name merely contains the other engine's volume name"; Name = "dms-local_{other}-backup"; Entry = @{ Labels = "default" }; Expected = "volume '{other}-backup' is not declared by any engine compose file (postgresql.yml, mssql.yml), so it cannot be identified" }
        @{ Case = "the volume is not engine storage (Keycloak)"; Name = "dms-local_dms-keycloak"; Entry = @{ Labels = "default" }; Expected = "volume 'dms-keycloak' is not declared by any engine compose file (postgresql.yml, mssql.yml), so it cannot be identified" }
        @{ Case = "the volume is the selected engine's storage"; Name = "dms-local_{selected}"; Entry = @{ Labels = "default" }; Expected = "it is the selected engine's ({engine}) storage, declared in {selectedFile}; the run needs fresh storage" }
    )
    function Expand-CaseText {
        param($Value, $EngineCase)

        if ($Value -is [string]) {
            return $Value.Replace("{other}", $EngineCase.OtherKey).Replace("{OTHER}", $EngineCase.OtherKey.ToUpperInvariant()).Replace("{selected}", $EngineCase.SelectedKey).Replace("{engine}", $EngineCase.Engine).Replace("{selectedFile}", $EngineCase.SelectedFile)
        }
        if ($Value -is [hashtable]) {
            $copy = @{}
            foreach ($key in $Value.Keys) {
                $copy[$key] = Expand-CaseText -Value $Value[$key] -EngineCase $EngineCase
            }
            return $copy
        }
        return $Value
    }
    $script:blockedCases = foreach ($engineCase in $script:engineCases) {
        foreach ($template in $blockedTemplates) {
            @{
                Engine   = $engineCase.Engine
                Case     = $template.Case
                Name     = Expand-CaseText -Value $template.Name -EngineCase $engineCase
                Entry    = Expand-CaseText -Value $template.Entry -EngineCase $engineCase
                Expected = Expand-CaseText -Value $template.Expected -EngineCase $engineCase
            }
        }
    }
}

Describe "Leftover volume identification" {
    BeforeAll {
        $script:repositoryComposeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
        # Docker reports RFC 3339; the record holds it as invariant round-trip UTC.
        $script:createdAt = "2026-10-01T00:00:00.0000000Z"

        # Serves docker volume inspect from a table: name -> @{ Labels = <hashtable> | "default" | $null;
        # Fail = <message>; Raw = <output>; InspectedName = <name>; CreatedAt = <value> }. A name
        # missing from the table does not exist. "default" means the compose labels its name implies.
        function script:Set-VolumeInspectMock {
            param([hashtable]$Volumes)

            $global:RestoreSmokeTestVolumes = $Volumes
            Mock docker -ModuleName RestoreSmokeProbes {
                $name = [string]$args[-1]
                if ($null -ne $global:RestoreSmokeTestVolumeCalls) {
                    $global:RestoreSmokeTestVolumeCalls.Add($name)
                }
                $entry = $global:RestoreSmokeTestVolumes[$name]
                if ($null -eq $entry) {
                    $global:LASTEXITCODE = 1
                    return "Error response from daemon: get ${name}: no such volume"
                }
                if ($entry.ContainsKey("Fail")) {
                    $global:LASTEXITCODE = 1
                    return $entry.Fail
                }
                $global:LASTEXITCODE = 0
                if ($entry.ContainsKey("Raw")) {
                    return $entry.Raw
                }
                $labels = $entry["Labels"]
                if ($labels -eq "default") {
                    $separator = $name.IndexOf("_")
                    $labels = @{
                        "com.docker.compose.project" = $name.Substring(0, $separator)
                        "com.docker.compose.volume"  = $name.Substring($separator + 1)
                        "com.docker.compose.version" = "5.1.3"
                    }
                }
                $inspectedName = if ($entry.ContainsKey("InspectedName")) { $entry.InspectedName } else { $name }
                $createdAt = if ($entry.ContainsKey("CreatedAt")) { $entry.CreatedAt } else { "2026-10-01T00:00:00Z" }
                return ([ordered]@{ CreatedAt = $createdAt; Driver = "local"; Labels = $labels; Mountpoint = "/var/lib/docker/volumes/$name/_data"; Name = $inspectedName; Options = $null; Scope = "local" } | ConvertTo-Json -Compress -Depth 5)
            }
        }

        function script:New-RemainingInventory {
            param([hashtable]$VolumesByProject)

            $projects = foreach ($project in @("dms-local", "dms-published")) {
                $volumes = @()
                if ($VolumesByProject.ContainsKey($project)) {
                    $volumes = @($VolumesByProject[$project])
                }
                [pscustomobject]@{ Project = $project; Containers = @(); Volumes = $volumes }
            }
            return [pscustomobject]@{ Projects = @($projects) }
        }

        $script:engineVolumes = Get-RestoreSmokeEngineVolumeDefinition -DockerComposeRoot $script:repositoryComposeRoot
    }

    Context "Get-RestoreSmokeEngineVolumeDefinition" {
        It "reads each engine's storage volume from the repository's compose files" {
            $definitions = Get-RestoreSmokeEngineVolumeDefinition -DockerComposeRoot $script:repositoryComposeRoot

            @($definitions.Engine) | Should -Be @("postgresql", "mssql")
            $postgresql = @($definitions | Where-Object Engine -eq "postgresql")[0]
            $mssql = @($definitions | Where-Object Engine -eq "mssql")[0]
            $postgresql.ComposeFile | Should -Be "postgresql.yml"
            @($postgresql.Volumes) | Should -Be @("dms-postgresql")
            $mssql.ComposeFile | Should -Be "mssql.yml"
            @($mssql.Volumes) | Should -Be @("dms-mssql-2025")
        }

        It "reads only top-level volume keys, ignoring service volume lists, comments, nested settings, and other sections" {
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $root | Out-Null
            Set-Content -LiteralPath (Join-Path $root "postgresql.yml") -Value @(
                "services:"
                "  db:"
                "    volumes:"
                "      - service-list-entry:/var/lib/postgresql/data"
                "# volumes:"
                "#   commented-out:"
                "volumes:"
                "  # a comment inside the section"
                "  pg-data:"
                "    labels:"
                "      nested-key: value"
                "  pg-logs: {}"
                "networks:"
                "  pg-network:"
            )
            Set-Content -LiteralPath (Join-Path $root "mssql.yml") -Value @("volumes:", "  sql-data:")

            $definitions = Get-RestoreSmokeEngineVolumeDefinition -DockerComposeRoot $root

            @(@($definitions | Where-Object Engine -eq "postgresql")[0].Volumes) | Should -Be @("pg-data", "pg-logs")
            @(@($definitions | Where-Object Engine -eq "mssql")[0].Volumes) | Should -Be @("sql-data")
        }

        It "fails closed when <case>" -ForEach @(
            @{ Case = "an engine compose file is missing"; Postgresql = @("volumes:", "  pg-data:"); Mssql = $null; Expected = "Cannot identify leftover volumes: the mssql compose file 'mssql.yml' was not found*" }
            @{ Case = "an engine compose file declares no top-level volume"; Postgresql = @("services:", "  db:", "    volumes:", "      - pg-data:/data"); Mssql = @("volumes:", "  sql-data:"); Expected = "Cannot identify leftover volumes: the postgresql compose file 'postgresql.yml' declares no top-level volume." }
            @{ Case = "both engines declare the same volume"; Postgresql = @("volumes:", "  shared-data:"); Mssql = @("volumes:", "  shared-data:"); Expected = "Cannot identify leftover volumes: volume 'shared-data' is declared by both postgresql.yml and mssql.yml." }
        ) {
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $root | Out-Null
            Set-Content -LiteralPath (Join-Path $root "postgresql.yml") -Value $Postgresql
            if ($null -ne $Mssql) {
                Set-Content -LiteralPath (Join-Path $root "mssql.yml") -Value $Mssql
            }

            { Get-RestoreSmokeEngineVolumeDefinition -DockerComposeRoot $root } | Should -Throw $Expected
        }
    }

    Context "Get-RestoreSmokeLeftoverVolumeClassification" {
        It "allows the other engine's storage, by its labels, and blocks the selected engine's (<engine>)" -ForEach $script:engineCases {
            Set-VolumeInspectMock -Volumes @{
                "dms-local_$OtherKey"    = @{ Labels = "default" }
                "dms-local_$SelectedKey" = @{ Labels = "default" }
            }
            $inventory = New-RemainingInventory -VolumesByProject @{ "dms-local" = @("dms-local_$OtherKey", "dms-local_$SelectedKey") }

            $classification = Get-RestoreSmokeLeftoverVolumeClassification -Inventory $inventory -DatabaseEngine $Engine -Definition $script:engineVolumes

            $classification.DatabaseEngine | Should -Be $Engine
            @($classification.Allowed.Name) | Should -Be @("dms-local_$OtherKey")
            @($classification.Blocked.Name) | Should -Be @("dms-local_$SelectedKey")
            $allowed = $classification.Allowed[0]
            $allowed.Decision | Should -Be "allowed"
            $allowed.Project | Should -Be "dms-local"
            $allowed.ProjectLabel | Should -Be "dms-local"
            $allowed.VolumeLabel | Should -Be $OtherKey
            $allowed.Engine | Should -Be $OtherEngine
            $allowed.ComposeFile | Should -Be $OtherFile
            $allowed.CreatedAt | Should -Be $script:createdAt
            $allowed.Reason | Should -Be "declared in $OtherFile as $OtherEngine storage; this run uses $Engine, whose $SelectedFile does not declare it, so this run's down -v teardowns leave it in place"
            $blocked = $classification.Blocked[0]
            $blocked.Decision | Should -Be "blocked"
            $blocked.Engine | Should -Be $Engine
            $blocked.Reason | Should -Be "it is the selected engine's ($Engine) storage, declared in $SelectedFile; the run needs fresh storage"
        }

        It "blocks a volume when <case> (<engine>)" -ForEach $script:blockedCases {
            Set-VolumeInspectMock -Volumes @{ $Name = $Entry }
            $inventory = New-RemainingInventory -VolumesByProject @{ "dms-local" = @($Name) }

            $classification = Get-RestoreSmokeLeftoverVolumeClassification -Inventory $inventory -DatabaseEngine $Engine -Definition $script:engineVolumes

            $classification.Allowed.Count | Should -Be 0
            $classification.Blocked.Count | Should -Be 1
            $classification.Blocked[0].Name | Should -Be $Name
            $classification.Blocked[0].Decision | Should -Be "blocked"
            $classification.Blocked[0].Reason | Should -Be $Expected
        }

        It "classifies mixed leftovers in both projects in one read-only pass (<engine>)" -ForEach $script:engineCases {
            Set-VolumeInspectMock -Volumes @{
                "dms-local_$OtherKey"         = @{ Labels = "default" }
                "dms-local_$SelectedKey"      = @{ Labels = "default" }
                "dms-local_dms-keycloak"      = @{ Labels = "default" }
                "dms-published_$OtherKey"     = @{ Labels = "default" }
                "dms-published_$SelectedKey"  = @{ Fail = "Error response from daemon: context deadline exceeded" }
            }
            $inventory = New-RemainingInventory -VolumesByProject @{
                "dms-local"     = @("dms-local_$OtherKey", "dms-local_$SelectedKey", "dms-local_dms-keycloak")
                "dms-published" = @("dms-published_$OtherKey", "dms-published_$SelectedKey")
            }

            $classification = Get-RestoreSmokeLeftoverVolumeClassification -Inventory $inventory -DatabaseEngine $Engine -Definition $script:engineVolumes

            @($classification.Volumes.Name) | Should -Be @("dms-local_$OtherKey", "dms-local_$SelectedKey", "dms-local_dms-keycloak", "dms-published_$OtherKey", "dms-published_$SelectedKey")
            @($classification.Allowed.Name) | Should -Be @("dms-local_$OtherKey", "dms-published_$OtherKey")
            @($classification.Blocked.Name) | Should -Be @("dms-local_$SelectedKey", "dms-local_dms-keycloak", "dms-published_$SelectedKey")
            $classification.Blocked[2].Reason | Should -Be "inspection failed (docker exit 1: Error response from daemon: context deadline exceeded)"

            $message = Format-RestoreSmokeLeftoverVolumeClassification -Classification $classification -Decision blocked
            $message | Should -Be ("dms-local_$SelectedKey (it is the selected engine's ($Engine) storage, declared in $SelectedFile; the run needs fresh storage); " +
                "dms-local_dms-keycloak (volume 'dms-keycloak' is not declared by any engine compose file (postgresql.yml, mssql.yml), so it cannot be identified); " +
                "dms-published_$SelectedKey (inspection failed (docker exit 1: Error response from daemon: context deadline exceeded))")

            Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 5 -Exactly
            Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 0 -Exactly -ParameterFilter {
                -not ($args[0] -eq "volume" -and $args[1] -eq "inspect")
            }
        }

        It "inspects nothing when no volume remains" {
            Set-VolumeInspectMock -Volumes @{}

            $classification = Get-RestoreSmokeLeftoverVolumeClassification -Inventory (New-RemainingInventory -VolumesByProject @{}) -DatabaseEngine postgresql -Definition $script:engineVolumes

            $classification.Volumes.Count | Should -Be 0
            $classification.Allowed.Count | Should -Be 0
            $classification.Blocked.Count | Should -Be 0
            Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 0 -Exactly
        }
    }

    Context "Get-RestoreSmokeAllowedVolumeState" {
        BeforeEach {
            Set-VolumeInspectMock -Volumes @{
                "dms-local_dms-mssql-2025" = @{ Labels = "default" }
                "dms-local_dms-postgresql" = @{ Labels = "default" }
            }
            $script:classification = Get-RestoreSmokeLeftoverVolumeClassification `
                -Inventory (New-RemainingInventory -VolumesByProject @{ "dms-local" = @("dms-local_dms-mssql-2025", "dms-local_dms-postgresql") }) `
                -DatabaseEngine postgresql -Definition $script:engineVolumes
        }

        It "reports <case>" -ForEach @(
            @{ Case = "an untouched volume as preserved"; Entry = @{ Labels = "default" }; Present = $true; Preserved = $true; Reason = $null }
            @{ Case = "a removed volume as not present"; Entry = $null; Present = $false; Preserved = $false; Reason = "inspection failed (docker exit 1: Error response from daemon: get dms-local_dms-mssql-2025: no such volume)" }
            @{ Case = "a recreated volume (new CreatedAt) as not preserved"; Entry = @{ Labels = "default"; CreatedAt = "2026-10-08T12:00:00Z" }; Present = $true; Preserved = $false; Reason = "CreatedAt changed from '2026-10-01T00:00:00.0000000Z' to '2026-10-08T12:00:00.0000000Z'; the volume was recreated" }
            @{ Case = "an inspection failure as not shown preserved"; Entry = @{ Fail = "Cannot connect to the Docker daemon" }; Present = $false; Preserved = $false; Reason = "inspection failed (docker exit 1: Cannot connect to the Docker daemon)" }
        ) {
            $global:RestoreSmokeTestVolumes = @{}
            if ($null -ne $Entry) {
                $global:RestoreSmokeTestVolumes["dms-local_dms-mssql-2025"] = $Entry
            }

            $states = @(Get-RestoreSmokeAllowedVolumeState -Classification $script:classification)

            $states.Count | Should -Be 1
            $states[0].Name | Should -Be "dms-local_dms-mssql-2025"
            $states[0].Present | Should -Be $Present
            $states[0].Preserved | Should -Be $Preserved
            $states[0].Reason | Should -Be $Reason
        }

        It "cannot show preservation when the preflight observed no CreatedAt" {
            $script:classification.Allowed[0].CreatedAt = $null

            $states = @(Get-RestoreSmokeAllowedVolumeState -Classification $script:classification)

            $states[0].Present | Should -BeTrue
            $states[0].Preserved | Should -BeFalse
            $states[0].Reason | Should -Be "the preflight observed no CreatedAt, so preservation cannot be shown"
        }

        It "re-inspects only the allowed volumes" {
            $global:RestoreSmokeTestVolumeCalls = [System.Collections.Generic.List[string]]::new()
            try {
                $null = Get-RestoreSmokeAllowedVolumeState -Classification $script:classification

                @($global:RestoreSmokeTestVolumeCalls) | Should -Be @("dms-local_dms-mssql-2025")
            }
            finally {
                $global:RestoreSmokeTestVolumeCalls = $null
            }
        }
    }
}

Describe "Invoke-RestoreSmokeSchemaToolBuild" {
    BeforeAll {
        $global:RestoreSmokeTestTargetDir = Join-Path $TestDrive "schema-tools-bin"
        New-Item -ItemType Directory -Path $global:RestoreSmokeTestTargetDir -Force | Out-Null
        $script:builtExecutable = [System.IO.Path]::GetFullPath((Join-Path $global:RestoreSmokeTestTargetDir ($IsWindows ? "api-schema-tools.exe" : "api-schema-tools")))
        Set-Content -LiteralPath $script:builtExecutable -Value "fake apphost"
        # A real managed assembly, so the informational version is read from genuine version info.
        Copy-Item -LiteralPath ([System.Text.Json.JsonSerializer].Assembly.Location) -Destination (Join-Path $global:RestoreSmokeTestTargetDir "api-schema-tools.dll")
        $script:expectedVersion = (Get-Item -LiteralPath (Join-Path $global:RestoreSmokeTestTargetDir "api-schema-tools.dll")).VersionInfo.ProductVersion
    }

    BeforeEach {
        Mock dotnet -ModuleName RestoreSmokeProbes {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq "msbuild") {
                return ($global:RestoreSmokeTestTargetDir + [System.IO.Path]::DirectorySeparatorChar)
            }
        }
    }

    It "verifies a Debug build that the resolver returns by default and on request" {
        $record = Invoke-RestoreSmokeSchemaToolBuild -ProjectPath "SchemaTools.csproj" -Resolver { param($RequestedPath) if ($RequestedPath) { $RequestedPath } else { $script:builtExecutable } }

        $record.Verified | Should -BeTrue
        $record.BuildExitCode | Should -Be 0
        $record.BuildCommand | Should -Be 'dotnet build "SchemaTools.csproj" -c Debug'
        $record.ExecutablePath | Should -Be $script:builtExecutable
        $record.Sha256AtBuild | Should -Be ((Get-FileHash -LiteralPath $script:builtExecutable -Algorithm SHA256).Hash.ToLowerInvariant())
        $record.InformationalVersion | Should -Be $script:expectedVersion
        Should -Invoke dotnet -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $args[0] -eq "build" -and $args -contains "Debug" }
    }

    It "is not verified when the default resolution selects another executable" {
        $record = Invoke-RestoreSmokeSchemaToolBuild -ProjectPath "SchemaTools.csproj" -Resolver { param($RequestedPath) if ($RequestedPath) { $RequestedPath } else { "/stale/bin/Release/net10.0/api-schema-tools" } }

        $record.Verified | Should -BeFalse
        $record.Reason | Should -BeLike "*selects '/stale/bin/Release/net10.0/api-schema-tools', not the in-run build*"
    }

    It "is not verified when the build fails, and never resolves" {
        Mock dotnet -ModuleName RestoreSmokeProbes { $global:LASTEXITCODE = 1 }
        $script:resolverCalls = 0

        $record = Invoke-RestoreSmokeSchemaToolBuild -ProjectPath "SchemaTools.csproj" -Resolver { $script:resolverCalls++; "unused" }

        $record.Verified | Should -BeFalse
        $record.BuildExitCode | Should -Be 1
        $record.Reason | Should -Be "dotnet build exited 1"
        $record.ExecutablePath | Should -BeNullOrEmpty
        $script:resolverCalls | Should -Be 0
    }
}

Describe "In-run image builds (plan, tag ownership, build, cleanup)" {
    BeforeAll {
        $script:runId = "0123456789ab"
        $script:dmsTag = "local/ed-fi-api:dms-restore-smoke-0123456789ab"
        $script:configTag = "local/ed-fi-api-configuration-service:dms-restore-smoke-0123456789ab"
        $script:publishedTag = "edfialliance/ed-fi-api:dms-restore-smoke-0123456789ab"
        $script:sharedReferences = @("local/ed-fi-api", "local/ed-fi-api:latest", "local/ed-fi-api-configuration-service", "ed-fi-api-local", "ed-fi-api-config-local", "edfialliance/ed-fi-api")
        $script:staleId = "sha256:" + ("5" * 64)

        # A fake docker with an image store: image inspect / buildx build / image rm. Per-image build
        # behavior is keyed by the iidfile name (dms, config): ExitCode, Throw, NoIidFile,
        # IidContent, NoTags, TagIds (tag -> ID the tag resolves to instead of the iid).
        function script:Set-ImageDockerMock {
            param(
                [hashtable]$Images = @{},
                [hashtable]$Builds = @{},
                [hashtable]$InspectFailures = @{},
                [int]$RemoveExitCode = 0
            )

            $global:RestoreSmokeImageTestState = @{
                Images          = $Images
                Builds          = $Builds
                InspectFailures = $InspectFailures
                RemoveExitCode  = $RemoveExitCode
                Calls           = [System.Collections.Generic.List[string]]::new()
            }
            Mock docker -ModuleName RestoreSmokeProbes {
                $state = $global:RestoreSmokeImageTestState
                $state.Calls.Add(($args -join " "))
                $global:LASTEXITCODE = 0
                if ($args[0] -eq "image" -and $args[1] -eq "inspect") {
                    $reference = [string]$args[2]
                    if ($state.InspectFailures.ContainsKey($reference)) {
                        $global:LASTEXITCODE = 1
                        return $state.InspectFailures[$reference]
                    }
                    if ($state.Images.ContainsKey($reference)) {
                        return $state.Images[$reference]
                    }
                    $global:LASTEXITCODE = 1
                    return "Error response from daemon: No such image: $reference"
                }
                if ($args[0] -eq "buildx") {
                    $iidFile = [string]$args[[array]::IndexOf($args, "--iidfile") + 1]
                    $key = [System.IO.Path]::GetFileNameWithoutExtension($iidFile)
                    $tags = @(for ($i = 0; $i -lt $args.Count - 1; $i++) { if ($args[$i] -eq "-t") { [string]$args[$i + 1] } })
                    $behavior = if ($state.Builds.ContainsKey($key)) { $state.Builds[$key] } else { @{} }
                    if ($behavior.Throw) {
                        throw $behavior.Throw
                    }
                    if ($behavior.ExitCode) {
                        $global:LASTEXITCODE = $behavior.ExitCode
                        return "ERROR: failed to solve"
                    }
                    $imageId = if ($behavior.ContainsKey("ImageId")) { $behavior.ImageId } else { "sha256:" + ([string]$key[0]) * 64 }
                    if (-not $behavior.NoIidFile) {
                        $content = if ($behavior.ContainsKey("IidContent")) { $behavior.IidContent } else { $imageId }
                        Set-Content -LiteralPath $iidFile -Value $content -NoNewline
                    }
                    if (-not $behavior.NoTags) {
                        foreach ($tag in $tags) {
                            $state.Images[$tag] = if ($behavior.TagIds -and $behavior.TagIds.ContainsKey($tag)) { $behavior.TagIds[$tag] } else { $imageId }
                        }
                    }
                    return "built"
                }
                if ($args[0] -eq "image" -and $args[1] -eq "rm") {
                    if ($state.RemoveExitCode -ne 0) {
                        $global:LASTEXITCODE = $state.RemoveExitCode
                        return "Error response from daemon: conflict: unable to remove repository reference"
                    }
                    $state.Images.Remove([string]$args[2])
                    return "Untagged: $($args[2])"
                }
                throw "unexpected docker call: $($args -join ' ')"
            }
        }

        function script:New-TestImagePlan {
            param([string]$Wrapper = "local")

            $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path (Join-Path $repoRoot "src/dms"), (Join-Path $repoRoot "src/config") -Force | Out-Null
            $workDirectory = Join-Path $repoRoot "work"
            return Get-RestoreSmokeImageBuildPlan -RepoRoot $repoRoot -WorkDirectory $workDirectory -RunId $script:runId -Wrapper $Wrapper
        }

        function script:Get-ImageDockerCall {
            param([string]$Pattern)
            return @($global:RestoreSmokeImageTestState.Calls | Where-Object { $_ -like $Pattern })
        }

        function script:Assert-NoSharedReferenceTouched {
            foreach ($call in $global:RestoreSmokeImageTestState.Calls) {
                $references = @($call -split " " | Where-Object { $_ -like "*ed-fi-api*" })
                foreach ($reference in $references) {
                    $reference | Should -Match ':dms-restore-smoke-0123456789ab$'
                }
            }
        }
    }

    Context "Get-RestoreSmokeImageBuildPlan" {
        It "builds each image with the build scripts' Dockerfile, contexts, and VERSION, plus --load, a fresh iidfile, and the run tag" {
            $plan = New-TestImagePlan

            $plan.Count | Should -Be 2
            $plan[0].Key | Should -Be "Dms"
            $plan[0].SourceDirectory | Should -Be "src/dms"
            $plan[0].Tags | Should -Be @($script:dmsTag)
            $plan[0].Arguments | Should -Be @("buildx", "build", "--load", "--iidfile", $plan[0].IidFile, "-t", $script:dmsTag, "-f", "Dockerfile", ".", "--build-context", "parentdir=../", "--build-arg", "VERSION=8.0.0")
            $plan[0].IidFile | Should -BeLike "*work*images*dms.iid"
            $plan[1].Key | Should -Be "Config"
            $plan[1].SourceDirectory | Should -Be "src/config"
            $plan[1].Arguments | Should -Be @("buildx", "build", "--load", "--iidfile", $plan[1].IidFile, "-t", $script:configTag, "-f", "Dockerfile", ".", "--build-context", "parentdir=../", "--build-arg", "VERSION=8.0.0")
            $plan[1].IidFile | Should -BeLike "*work*images*config.iid"
            $plan[0].WorkingDirectory | Should -BeLike "*src*dms"
            $plan[1].WorkingDirectory | Should -BeLike "*src*config"
        }

        It "adds the published repository's run tag to the same DMS build for the published wrapper" {
            $plan = New-TestImagePlan -Wrapper published

            $plan[0].Tags | Should -Be @($script:dmsTag, $script:publishedTag)
            ($plan[0].Arguments -join " ") | Should -BeLike "*-t $script:dmsTag -t $script:publishedTag -f Dockerfile*"
            $plan[1].Tags | Should -Be @($script:configTag)
        }

        It "rejects a run id that is not the smoke's 12-hex form" {
            { Get-RestoreSmokeImageBuildPlan -RepoRoot $TestDrive -WorkDirectory $TestDrive -RunId "latest" -Wrapper local } | Should -Throw
        }
    }

    Context "Get-RestoreSmokeImageTagState" {
        It "reports <case>" -ForEach @(
            @{ Case = "a resolving tag as Present with its ID"; Images = @{ "local/x:t" = ("sha256:" + "a" * 64) }; Failures = @{}; State = "Present" }
            @{ Case = "the daemon's No such image message as Absent"; Images = @{}; Failures = @{}; State = "Absent" }
            @{ Case = "the CLI's No such image message as Absent"; Images = @{}; Failures = @{ "local/x:t" = "Error: No such image: local/x:t" }; State = "Absent" }
            @{ Case = "a daemon connection failure as Unknown"; Images = @{}; Failures = @{ "local/x:t" = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?" }; State = "Unknown" }
            @{ Case = "a permission failure as Unknown"; Images = @{}; Failures = @{ "local/x:t" = "permission denied while trying to connect to the Docker daemon socket" }; State = "Unknown" }
            @{ Case = "No such image for a different reference as Unknown"; Images = @{}; Failures = @{ "local/x:t" = "Error response from daemon: No such image: local/x:other" }; State = "Unknown" }
            @{ Case = "No such image mixed with another error as Unknown"; Images = @{}; Failures = @{ "local/x:t" = @("Error response from daemon: No such image: local/x:t", "error during connect: EOF") }; State = "Unknown" }
            @{ Case = "an unexpected inspect output as Unknown"; Images = @{ "local/x:t" = "not-an-id" }; Failures = @{}; State = "Unknown" }
        ) {
            Set-ImageDockerMock -Images $Images -InspectFailures $Failures

            $tagState = Get-RestoreSmokeImageTagState -Reference "local/x:t"

            $tagState.State | Should -Be $State
            if ($State -eq "Present") {
                $tagState.ImageId | Should -Be $Images["local/x:t"]
            }
            else {
                $tagState.ImageId | Should -BeNullOrEmpty
            }
        }
    }

    Context "Register-RestoreSmokeImageTagOwnership" {
        It "claims every planned tag together once all are proven absent, and creates the iidfile directory" {
            Set-ImageDockerMock -Images @{ "local/ed-fi-api" = $script:staleId }
            $plan = New-TestImagePlan -Wrapper published
            $ledger = New-RestoreSmokeImageLedger

            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            @($ledger.OwnedTags) | Should -Be @($script:dmsTag, $script:publishedTag, $script:configTag)
            Test-Path -LiteralPath (Split-Path -Parent $plan[0].IidFile) | Should -BeTrue
            Test-Path -LiteralPath $plan[0].IidFile | Should -BeFalse
            Get-ImageDockerCall "buildx *" | Should -BeNullOrEmpty
            Assert-NoSharedReferenceTouched
        }

        It "claims nothing when a planned tag already exists, even if only the second image's tag collides" {
            Set-ImageDockerMock -Images @{ $script:configTag = $script:staleId }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger

            { Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger } | Should -Throw "*run tag '$script:configTag' already exists*no image tag was claimed*"

            @($ledger.OwnedTags) | Should -BeNullOrEmpty
            Get-ImageDockerCall "buildx *" | Should -BeNullOrEmpty
        }

        It "claims nothing when a tag's absence cannot be established (<case>)" -ForEach @(
            @{ Case = "daemon unreachable"; Message = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?" }
            @{ Case = "permission denied"; Message = "permission denied while trying to connect to the Docker daemon socket" }
            @{ Case = "transport failure"; Message = "error during connect: Get http://docker/v1.47/images/json: EOF" }
        ) {
            Set-ImageDockerMock -InspectFailures @{ $script:configTag = $Message }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger

            { Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger } | Should -Throw "*Cannot establish that the run tag '$script:configTag' is absent*no image tag was claimed*"

            @($ledger.OwnedTags) | Should -BeNullOrEmpty
            Get-ImageDockerCall "buildx *" | Should -BeNullOrEmpty
        }

        It "claims nothing when an iidfile path already exists" {
            Set-ImageDockerMock
            $plan = New-TestImagePlan
            New-Item -ItemType Directory -Path (Split-Path -Parent $plan[1].IidFile) -Force | Out-Null
            Set-Content -LiteralPath $plan[1].IidFile -Value $script:staleId
            $ledger = New-RestoreSmokeImageLedger

            { Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger } | Should -Throw "*iidfile path*already exists*no image tag was claimed*"

            @($ledger.OwnedTags) | Should -BeNullOrEmpty
        }
    }

    Context "Invoke-RestoreSmokeImageBuild" {
        BeforeEach {
            $script:dmsId = "sha256:" + ("d" * 64)
            $script:configId = "sha256:" + ("c" * 64)
        }

        It "verifies each image by the ID its build reported, matching every run tag, and records the command and iidfile content" {
            Set-ImageDockerMock -Images @{ "local/ed-fi-api" = $script:staleId }
            $plan = New-TestImagePlan -Wrapper published
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger

            $ledger.Dms.Verified | Should -BeTrue
            $ledger.Dms.ExitCode | Should -Be 0
            $ledger.Dms.ImageId | Should -Be $script:dmsId
            $ledger.Dms.IidFileContent | Should -Be $script:dmsId
            $ledger.Dms.TagImageIds[$script:dmsTag] | Should -Be $script:dmsId
            $ledger.Dms.TagImageIds[$script:publishedTag] | Should -Be $script:dmsId
            $ledger.Dms.Command | Should -Be (@("docker") + $plan[0].Arguments)
            $ledger.Dms.Reason | Should -BeNullOrEmpty
            $ledger.Config.Verified | Should -BeTrue
            $ledger.Config.ImageId | Should -Be $script:configId
            Assert-NoSharedReferenceTouched
        }

        It "accepts a cached build that reproduces the ID a pre-existing shared tag already has" {
            Set-ImageDockerMock -Images @{ "local/ed-fi-api" = $script:staleId } -Builds @{ dms = @{ ImageId = $script:staleId } }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger

            $ledger.Dms.Verified | Should -BeTrue
            $ledger.Dms.ImageId | Should -Be $script:staleId
        }

        It "accepts a cached build that produces a new ID" {
            $newId = "sha256:" + ("e" * 64)
            Set-ImageDockerMock -Images @{ "local/ed-fi-api" = $script:staleId } -Builds @{ dms = @{ ImageId = $newId } }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger

            $ledger.Dms.Verified | Should -BeTrue
            $ledger.Dms.ImageId | Should -Be $newId
        }

        It "fails a build that exits non-zero while stale images exist, without inspecting any shared tag" {
            Set-ImageDockerMock -Images @{ "local/ed-fi-api" = $script:staleId; "ed-fi-api-local" = $script:staleId; "edfialliance/ed-fi-api" = $script:staleId } -Builds @{ dms = @{ ExitCode = 1 } }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            { Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger } | Should -Throw "*Image provenance could not be established for Dms: docker buildx build (Dms) exited 1*"

            $ledger.Dms.ExitCode | Should -Be 1
            $ledger.Dms.Verified | Should -BeFalse
            $ledger.Dms.ImageId | Should -BeNullOrEmpty
            $ledger.Config | Should -BeNullOrEmpty
            Get-ImageDockerCall "buildx *" | Should -HaveCount 1
            Assert-NoSharedReferenceTouched
        }

        It "is not verified when <case>" -ForEach @(
            @{ Case = "the build exits 0 but writes no iidfile"; Build = @{ NoIidFile = $true }; Expected = "the build produced no image ID (no iidfile)" }
            @{ Case = "the iidfile is empty"; Build = @{ IidContent = "" }; Expected = "the build produced no image ID (iidfile content '')" }
            @{ Case = "the iidfile is malformed"; Build = @{ IidContent = "sha256:xyz" }; Expected = "the build produced no image ID (iidfile content 'sha256:xyz')" }
            @{ Case = "the run tag is missing after the build"; Build = @{ NoTags = $true }; Expected = "the run tag 'local/ed-fi-api:dms-restore-smoke-0123456789ab' is missing after the build" }
            @{ Case = "the run tag resolves to another ID than the build reported"; Build = @{ TagIds = @{ "local/ed-fi-api:dms-restore-smoke-0123456789ab" = ("sha256:" + "5" * 64) } }; Expected = "the run tag 'local/ed-fi-api:dms-restore-smoke-0123456789ab' resolves to sha256:5555*, but the build reported sha256:dddd*" }
        ) {
            Set-ImageDockerMock -Builds @{ dms = $Build }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            { Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger } | Should -Throw "*Image provenance could not be established for Dms*"

            $ledger.Dms.ExitCode | Should -Be 0
            $ledger.Dms.Verified | Should -BeFalse
            $ledger.Dms.ImageId | Should -BeNullOrEmpty
            $ledger.Dms.Reason | Should -BeLike $Expected
            $ledger.Config | Should -BeNullOrEmpty
        }

        It "is not verified when the published second tag resolves to another ID" {
            Set-ImageDockerMock -Builds @{ dms = @{ TagIds = @{ "edfialliance/ed-fi-api:dms-restore-smoke-0123456789ab" = $script:staleId } } }
            $plan = New-TestImagePlan -Wrapper published
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            { Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger } | Should -Throw "*Image provenance could not be established for Dms*"

            $ledger.Dms.Reason | Should -BeLike "the run tag '$script:publishedTag' resolves to $script:staleId, but the build reported $script:dmsId"
        }

        It "refuses to build under a tag that was not claimed" {
            Set-ImageDockerMock
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger

            { Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger } | Should -Throw "*was not claimed for this run*"

            Get-ImageDockerCall "buildx *" | Should -BeNullOrEmpty
        }

        It "refuses an iidfile that appeared after the tags were claimed" {
            Set-ImageDockerMock
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger
            Set-Content -LiteralPath $plan[0].IidFile -Value $script:staleId

            { Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger } | Should -Throw "*the iidfile already existed before the build*"

            Get-ImageDockerCall "buildx *" | Should -BeNullOrEmpty
            $ledger.Dms.Verified | Should -BeFalse
        }

        It "keeps the verified DMS record and every claimed tag when the CMS build <case>, and cleanup removes only the DMS run tag" -ForEach @(
            @{ Case = "exits non-zero"; ConfigBuild = @{ ExitCode = 2 }; Expected = "*Image provenance could not be established for Config: docker buildx build (Config) exited 2*"; ConfigExitCode = 2; ConfigReason = "docker buildx build (Config) exited 2" }
            @{ Case = "throws"; ConfigBuild = @{ Throw = "buildx transport closed" }; Expected = "*buildx transport closed*"; ConfigExitCode = $null; ConfigReason = "the build did not complete" }
        ) {
            Set-ImageDockerMock -Images @{ "local/ed-fi-api-configuration-service" = $script:staleId } -Builds @{ config = $ConfigBuild }
            $plan = New-TestImagePlan
            $ledger = New-RestoreSmokeImageLedger
            Register-RestoreSmokeImageTagOwnership -Plan $plan -Ledger $ledger

            { Invoke-RestoreSmokeImageBuild -Plan $plan -Ledger $ledger } | Should -Throw $Expected

            $ledger.Dms.Verified | Should -BeTrue
            $ledger.Dms.ImageId | Should -Be $script:dmsId
            $ledger.Config.Verified | Should -BeFalse
            $ledger.Config.ExitCode | Should -Be $ConfigExitCode
            $ledger.Config.Reason | Should -Be $ConfigReason
            @($ledger.OwnedTags) | Should -Be @($script:dmsTag, $script:configTag)

            Remove-RestoreSmokeOwnedImageTag -Ledger $ledger

            Get-ImageDockerCall "image rm *" | Should -Be @("image rm $script:dmsTag")
            @($ledger.Cleanup | ForEach-Object { "$($_.Tag)=$($_.Action)" }) | Should -Be @("$script:dmsTag=removed", "$script:configTag=absent")
            $global:RestoreSmokeImageTestState.Images["local/ed-fi-api-configuration-service"] | Should -Be $script:staleId
            Assert-NoSharedReferenceTouched
        }
    }

    Context "Remove-RestoreSmokeOwnedImageTag" {
        BeforeEach {
            $script:ledger = New-RestoreSmokeImageLedger
            foreach ($tag in @($script:dmsTag, $script:publishedTag, $script:configTag)) {
                $script:ledger.OwnedTags.Add($tag)
            }
            $script:builtId = "sha256:" + ("d" * 64)
            $script:images = @{
                $script:dmsTag          = $script:builtId
                $script:publishedTag    = $script:builtId
                $script:configTag       = ("sha256:" + "c" * 64)
                "local/ed-fi-api"       = $script:builtId
                "ed-fi-api-local"       = $script:staleId
            }
        }

        It "removes each owned tag by name, without -f, and never a shared tag or an image ID" {
            Set-ImageDockerMock -Images $script:images

            Remove-RestoreSmokeOwnedImageTag -Ledger $script:ledger

            Get-ImageDockerCall "image rm *" | Should -Be @("image rm $script:dmsTag", "image rm $script:publishedTag", "image rm $script:configTag")
            @($script:ledger.Cleanup.Action) | Should -Be @("removed", "removed", "removed")
            $global:RestoreSmokeImageTestState.Images["local/ed-fi-api"] | Should -Be $script:builtId
            $global:RestoreSmokeImageTestState.Images["ed-fi-api-local"] | Should -Be $script:staleId
            @($global:RestoreSmokeImageTestState.Calls | Where-Object { $_ -match '(^|\s)(-f|--force)(\s|$)' }) | Should -BeNullOrEmpty
            Get-ImageDockerCall "*prune*" | Should -BeNullOrEmpty
            Assert-NoSharedReferenceTouched
        }

        It "keeps every tag under -SkipTeardown and records it" {
            Set-ImageDockerMock -Images $script:images

            Remove-RestoreSmokeOwnedImageTag -Ledger $script:ledger -Keep

            $global:RestoreSmokeImageTestState.Calls | Should -BeNullOrEmpty
            @($script:ledger.Cleanup.Action) | Should -Be @("kept", "kept", "kept")
        }

        It "warns and records a removal failure without throwing" {
            Set-ImageDockerMock -Images $script:images -RemoveExitCode 1

            Remove-RestoreSmokeOwnedImageTag -Ledger $script:ledger -WarningVariable warnings -WarningAction SilentlyContinue

            @($script:ledger.Cleanup.Action) | Should -Be @("failed", "failed", "failed")
            $script:ledger.Cleanup[0].Detail | Should -BeLike "docker image rm exited 1: *conflict*"
            $warnings.Count | Should -Be 3
        }

        It "removes nothing for a tag whose state cannot be established" {
            Set-ImageDockerMock -Images $script:images -InspectFailures @{ $script:configTag = "permission denied while trying to connect to the Docker daemon socket" }

            Remove-RestoreSmokeOwnedImageTag -Ledger $script:ledger -WarningAction SilentlyContinue

            Get-ImageDockerCall "image rm *" | Should -Be @("image rm $script:dmsTag", "image rm $script:publishedTag")
            $script:ledger.Cleanup[2].Action | Should -Be "failed"
        }
    }
}

Describe "Write-RestoreSmokeImageEnvironmentFile" {
    BeforeEach {
        $script:baseFile = Join-Path $TestDrive ".env.base"
        Set-Content -LiteralPath $script:baseFile -Value @("POSTGRES_DB_NAME=edfi_datamanagementservice", "DMS_DOCKER_IMAGE=stale-image", "DMS_IMAGE_TAG=pre")
        $script:targetFile = Join-Path $TestDrive ".env.smoke-images"
    }

    It "points the local compose files at the in-run images, replacing any base value" {
        $null = Write-RestoreSmokeImageEnvironmentFile -BaseEnvironmentFile $script:baseFile -TargetPath $script:targetFile -Wrapper local -DmsImage "local/ed-fi-api" -ConfigImage "local/ed-fi-api-configuration-service"

        $lines = Get-Content -LiteralPath $script:targetFile
        $lines | Should -Contain "POSTGRES_DB_NAME=edfi_datamanagementservice"
        @($lines | Where-Object { $_ -like "DMS_DOCKER_IMAGE=*" }) | Should -Be @("DMS_DOCKER_IMAGE=local/ed-fi-api")
        $lines | Should -Contain "DMS_CONFIG_DOCKER_IMAGE=local/ed-fi-api-configuration-service"
    }

    It "points the published compose files at the run-unique tag and the in-run config image" {
        $null = Write-RestoreSmokeImageEnvironmentFile -BaseEnvironmentFile $script:baseFile -TargetPath $script:targetFile -Wrapper published -PublishedDmsTag "dms-restore-smoke-abc" -ConfigImage "local/ed-fi-api-configuration-service"

        $lines = Get-Content -LiteralPath $script:targetFile
        @($lines | Where-Object { $_ -like "DMS_IMAGE_TAG=*" }) | Should -Be @("DMS_IMAGE_TAG=dms-restore-smoke-abc")
        $lines | Should -Contain "DMS_CONFIG_DOCKER_IMAGE=local/ed-fi-api-configuration-service"
    }
}

Describe "Get-RestoreSmokeStackObservation" {
    It "records the image each service container actually runs, with db repo digests" {
        Mock docker -ModuleName RestoreSmokeProbes {
            $global:LASTEXITCODE = 0
            switch ($args[0]) {
                "ps" { return @("c1|ed-fi-api|dms", "c2|ed-fi-api-config-service|config", "c3|dms-postgresql|db", "c4|kafka|kafka") }
                "inspect" {
                    switch ($args[1]) {
                        "c1" { return "sha256:dms|local/ed-fi-api" }
                        "c2" { return "sha256:config|local/ed-fi-api-configuration-service" }
                        "c3" { return "sha256:pg|postgres:16" }
                    }
                }
                "image" { return "postgres@sha256:abc" }
            }
        }

        $observation = Get-RestoreSmokeStackObservation -ComposeProject "dms-local" -Label "leg-package-directory"

        $observation.Label | Should -Be "leg-package-directory"
        $observation.Services.Keys | Should -Be @("dms", "config", "db")
        $observation.Services["dms"].ImageId | Should -Be "sha256:dms"
        $observation.Services["config"].ImageRef | Should -Be "local/ed-fi-api-configuration-service"
        $observation.Services["db"].RepoDigests | Should -Be @("postgres@sha256:abc")
        $observation.Services["db"].RepoDigestsReason | Should -BeNullOrEmpty
        Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $args[0] -eq "ps" -and $args -contains "label=com.docker.compose.project=dms-local" }
    }

    It "records why the engine image has no repository digest when <case>" -ForEach @(
        @{ Case = "the image inspect fails"; ExitCode = 1; Output = "Error response from daemon: No such image: sha256:pg"; Expected = "docker image inspect sha256:pg exited 1: Error response from daemon: No such image: sha256:pg" }
        @{ Case = "the image has no digest (locally built)"; ExitCode = 0; Output = ""; Expected = "the image has no repository digest (locally built or untagged)" }
    ) {
        $global:RestoreSmokeTestDocker = @{ ExitCode = $ExitCode; Output = $Output }
        Mock docker -ModuleName RestoreSmokeProbes {
            $global:LASTEXITCODE = 0
            switch ($args[0]) {
                "ps" { return @("c3|dms-postgresql|db") }
                "inspect" { return "sha256:pg|postgres:16" }
                "image" {
                    $global:LASTEXITCODE = $global:RestoreSmokeTestDocker.ExitCode
                    return $global:RestoreSmokeTestDocker.Output
                }
            }
        }

        $observation = Get-RestoreSmokeStackObservation -ComposeProject "dms-local" -Label "leg-package-directory"

        @($observation.Services["db"].RepoDigests) | Should -BeNullOrEmpty
        $observation.Services["db"].RepoDigestsReason | Should -Be $Expected
    }
}

Describe "Read-RestoreSmokeStagedSelection" {
    BeforeAll {
        function script:Write-TestManifest {
            param([string]$Root, [string]$Content)

            New-Item -ItemType Directory -Path $Root -Force | Out-Null
            $path = Join-Path $Root "bootstrap-manifest.json"
            [System.IO.File]::WriteAllText($path, $Content, [System.Text.UTF8Encoding]::new($false))
            return $path
        }
    }

    It "reads the staged identities with the SHA-256 and write time of the same manifest" {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $path = Write-TestManifest -Root $root -Content '{"version":1,"schema":{"selectionMode":"Standard","selectedPackages":["EdFi.DataStandard52.ApiSchema@1.0.335","EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"]}}'
        [System.IO.File]::SetLastWriteTimeUtc($path, [System.DateTime]::new(2026, 10, 8, 11, 1, 0, [System.DateTimeKind]::Utc))

        $result = Read-RestoreSmokeStagedSelection -BootstrapRoot $root

        $result.Reason | Should -BeNullOrEmpty
        $result.Source | Should -BeExactly "bootstrap-manifest.json schema.selectedPackages"
        @($result.StagedPackages) | Should -Be @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335")
        $result.ManifestSha256 | Should -BeExactly (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $result.ManifestLastWriteTimeUtc | Should -BeExactly "2026-10-08T11:01:00.0000000Z"
    }

    It "records an empty selection as an empty list, not as a reason" {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $null = Write-TestManifest -Root $root -Content '{"schema":{"selectedPackages":[]}}'

        $result = Read-RestoreSmokeStagedSelection -BootstrapRoot $root

        $result.Reason | Should -BeNullOrEmpty
        $result.StagedPackages.Count | Should -Be 0
    }

    It "reads a manifest that starts with a byte-order mark" {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $root "bootstrap-manifest.json"), '{"schema":{"selectedPackages":["EdFi.DataStandard52.ApiSchema@1.0.335"]}}', [System.Text.UTF8Encoding]::new($true))

        $result = Read-RestoreSmokeStagedSelection -BootstrapRoot $root

        $result.Reason | Should -BeNullOrEmpty
        @($result.StagedPackages) | Should -Be @("EdFi.DataStandard52.ApiSchema@1.0.335")
    }

    It "makes every staged entry log-safe" {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $null = Write-TestManifest -Root $root -Content '{"schema":{"selectedPackages":["EdFi\u0007.DataStandard52.ApiSchema@1.0.335",null]}}'

        $result = Read-RestoreSmokeStagedSelection -BootstrapRoot $root

        @($result.StagedPackages) | Should -Be @("EdFi?.DataStandard52.ApiSchema@1.0.335", "")
    }

    It "reports <case> as a reason, keeping the manifest's SHA-256" -ForEach @(
        @{ Case = "malformed JSON"; Content = '{"schema":'; Expected = "bootstrap-manifest.json is not valid JSON: *" }
        @{ Case = "a JSON array"; Content = '[]'; Expected = "bootstrap-manifest.json has no schema section" }
        @{ Case = "no schema section"; Content = '{"claims":{}}'; Expected = "bootstrap-manifest.json has no schema section" }
        @{ Case = "a schema section that is not an object"; Content = '{"schema":"Standard"}'; Expected = "bootstrap-manifest.json has no schema section" }
        @{ Case = "no selectedPackages"; Content = '{"schema":{"selectionMode":"Standard","selectedExtensions":[]}}'; Expected = "bootstrap-manifest.json records no schema.selectedPackages" }
        @{ Case = "a null selectedPackages"; Content = '{"schema":{"selectedPackages":null}}'; Expected = "bootstrap-manifest.json records no schema.selectedPackages" }
        @{ Case = "a string selectedPackages"; Content = '{"schema":{"selectedPackages":"EdFi.DataStandard52.ApiSchema@1.0.335"}}'; Expected = "schema.selectedPackages in bootstrap-manifest.json is not an array" }
        @{ Case = "an object selectedPackages"; Content = '{"schema":{"selectedPackages":{"core":"EdFi.DataStandard52.ApiSchema@1.0.335"}}}'; Expected = "schema.selectedPackages in bootstrap-manifest.json is not an array" }
    ) {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $path = Write-TestManifest -Root $root -Content $Content

        $result = Read-RestoreSmokeStagedSelection -BootstrapRoot $root

        $result.Reason | Should -BeLike $Expected
        $result.StagedPackages | Should -BeNullOrEmpty
        $result.ManifestSha256 | Should -BeExactly (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    It "reports a workspace without a manifest (<case>)" -ForEach @(
        @{ Case = "no workspace"; Prepare = { param($root) $null = $root } }
        @{ Case = "an empty workspace"; Prepare = { param($root) New-Item -ItemType Directory -Path $root -Force | Out-Null } }
        @{ Case = "only a derived env file, as a plain start leaves beside the manifest"; Prepare = { param($root) New-Item -ItemType Directory -Path $root -Force | Out-Null; Set-Content -LiteralPath (Join-Path $root ".env.derived") -Value "SCHEMA_PACKAGES='[]'" } }
        @{ Case = "a directory in the manifest's place"; Prepare = { param($root) New-Item -ItemType Directory -Path (Join-Path $root "bootstrap-manifest.json") -Force | Out-Null } }
    ) {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        & $Prepare $root

        $result = Read-RestoreSmokeStagedSelection -BootstrapRoot $root

        $result.Reason | Should -BeExactly "the active workspace has no bootstrap-manifest.json"
        $result.ManifestSha256 | Should -BeNullOrEmpty
        $result.ManifestLastWriteTimeUtc | Should -BeNullOrEmpty
        $result.StagedPackages | Should -BeNullOrEmpty
    }
}

Describe "New-RestoreSmokeStackStart" {
    BeforeAll {
        function script:New-StackStartEnv {
            param([string[]]$Lines)

            $path = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N") + ".env")
            Set-Content -LiteralPath $path -Value $Lines
            return $path
        }
    }

    It "records the start with no workspace before it" {
        $environment = New-StackStartEnv -Lines @("A=1", "SCHEMA_PACKAGES='[{""name"":""EdFi.DataStandard52.ApiSchema"",""version"":""1.0.335""},{""name"":""EdFi.DataStandard52.TPDM.ApiSchema"",""version"":""1.0.335"",""feedUrl"":""https://example.invalid/feed""}]'")
        $before = [System.DateTime]::UtcNow

        $start = New-RestoreSmokeStackStart -Sequence 3 -Label "leg-package-directory" -Selection "default" -EnvironmentFile $environment -BootstrapRoot (Join-Path $TestDrive "absent-bootstrap")

        $after = [System.DateTime]::UtcNow
        $start.StackStart | Should -BeExactly "stack-start#3"
        $start.Label | Should -BeExactly "leg-package-directory"
        $start.Restore | Should -BeFalse
        $start.Selection | Should -BeExactly "default"
        $start.EnvironmentFile | Should -BeExactly ([System.IO.Path]::GetFileName($environment))
        @($start.RequestedPackages) | Should -Be @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335")
        $start.RequestedReason | Should -BeNullOrEmpty
        $start.WorkspaceBefore.Present | Should -BeFalse
        $start.WorkspaceBefore.Sha256 | Should -BeNullOrEmpty
        $start.WorkspaceBefore.Reason | Should -BeExactly "the active workspace has no bootstrap-manifest.json"
        $started = [System.DateTimeOffset]::Parse($start.StartedUtc, [System.Globalization.CultureInfo]::InvariantCulture).UtcDateTime
        $started | Should -BeGreaterOrEqual $before
        $started | Should -BeLessOrEqual $after
    }

    It "captures the manifest present before a restore start" {
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        $manifest = Join-Path $root "bootstrap-manifest.json"
        Set-Content -LiteralPath $manifest -Value '{"schema":{"selectedPackages":["EdFi.DataStandard52.ApiSchema@1.0.335"]}}'
        [System.IO.File]::SetLastWriteTimeUtc($manifest, [System.DateTime]::new(2026, 10, 8, 9, 0, 0, [System.DateTimeKind]::Utc))
        $environment = New-StackStartEnv -Lines @("SCHEMA_PACKAGES='[{""name"":""EdFi.DataStandard52.ApiSchema"",""version"":""1.0.335""}]'")

        $start = New-RestoreSmokeStackStart -Sequence 1 -Label "leg-extension-selection" -Selection "core-only" -EnvironmentFile $environment -BootstrapRoot $root -Restore

        $start.Restore | Should -BeTrue
        $start.Selection | Should -BeExactly "core-only"
        $start.WorkspaceBefore.Present | Should -BeTrue
        $start.WorkspaceBefore.Sha256 | Should -BeExactly (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
        $start.WorkspaceBefore.LastWriteTimeUtc | Should -BeExactly "2026-10-08T09:00:00.0000000Z"
        $start.WorkspaceBefore.Reason | Should -BeNullOrEmpty
    }

    It "records an env whose SCHEMA_PACKAGES cannot be parsed (<case>) as a reason, not a throw" -ForEach @(
        @{ Case = "no SCHEMA_PACKAGES"; Lines = @("A=1") }
        @{ Case = "malformed JSON"; Lines = @("SCHEMA_PACKAGES='[{'") }
        @{ Case = "an empty array"; Lines = @("SCHEMA_PACKAGES='[]'") }
    ) {
        $environment = New-StackStartEnv -Lines $Lines

        $start = New-RestoreSmokeStackStart -Sequence 1 -Label "x" -Selection "default" -EnvironmentFile $environment -BootstrapRoot (Join-Path $TestDrive "absent-bootstrap")

        $start.RequestedPackages | Should -BeNullOrEmpty
        $start.RequestedReason | Should -BeLike "SCHEMA_PACKAGES in the env file could not be parsed: *"
        $start.StackStart | Should -BeExactly "stack-start#1"
    }
}

Describe "Get-RestoreSmokeStagedSelectionReason" {
    BeforeAll {
        $script:selCore = "EdFi.DataStandard52.ApiSchema@1.0.335"
        $script:selTpdm = "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"
        $script:selSha = "e1" * 32

        # A context of one observation (step leg-separate-config, bound to stack-start#2, a
        # default-selection restore that began at 11:00 into an empty workspace), the recorded
        # stack starts, and the recorded selection envs. The manifest was written at 11:01.
        function script:New-ReasonContext {
            $starts = [System.Collections.Generic.List[object]]::new()
            $starts.Add([pscustomobject]@{
                    StackStart        = "stack-start#2"
                    Label             = "leg-separate-config"
                    Restore           = $true
                    Selection         = "default"
                    EnvironmentFile   = ".env.smoke-images"
                    RequestedPackages = @($script:selCore, $script:selTpdm)
                    RequestedReason   = $null
                    WorkspaceBefore   = [pscustomobject]@{ Present = $false; Sha256 = $null; LastWriteTimeUtc = $null; Reason = "the active workspace has no bootstrap-manifest.json" }
                    StartedUtc        = "2026-10-08T11:00:00.0000000Z"
                })
            return [pscustomobject]@{
                Observation  = [pscustomobject]@{
                    Label           = "leg-separate-config"
                    StackStart      = "stack-start#2"
                    StagedSelection = [pscustomobject]@{ Source = "bootstrap-manifest.json schema.selectedPackages"; ManifestSha256 = $script:selSha; ManifestLastWriteTimeUtc = "2026-10-08T11:01:00.0000000Z"; StagedPackages = @($script:selCore, $script:selTpdm); Reason = $null }
                }
                Starts       = $starts
                Environments = [System.Collections.Generic.List[object]]::new()
            }
        }

        function script:Get-ContextReason {
            param($Context)

            return @(Get-RestoreSmokeStagedSelectionReason -Observation $Context.Observation -Label "leg-separate-config" -StackStart @($Context.Starts) -SelectionEnvironment @($Context.Environments))
        }

        function script:Set-KeptWorkspace {
            # The manifest the start found is the one observed: same SHA-256 and write time (10:30).
            param($Context)

            $Context.Starts[0].WorkspaceBefore = [pscustomobject]@{ Present = $true; Sha256 = $script:selSha; LastWriteTimeUtc = "2026-10-08T10:30:00.0000000Z"; Reason = $null }
            $Context.Observation.StagedSelection.ManifestLastWriteTimeUtc = "2026-10-08T10:30:00.0000000Z"
        }

        function script:Add-CoreOnlyEnvironment {
            param($Context)

            $Context.Environments.Add([pscustomobject]@{ Selection = "core-only"; FileName = ".env.smoke-core-only"; SelectedPackages = @($script:selCore); RemovedPackages = @($script:selTpdm) })
        }
    }

    It "is empty when <case>" -ForEach @(
        @{ Case = "the default-selection observation is complete"; Mutate = { param($c) $null = $c } }
        @{ Case = "a restore kept the workspace it found, unchanged (byte-identical candidate)"; Mutate = { param($c) Set-KeptWorkspace $c } }
        @{ Case = "the requested packages differ from the staged ones on a default-selection stack (a Data Standard overlay)"; Mutate = { param($c) $c.Starts[0].RequestedPackages = @("EdFi.DataStandard61.ApiSchema@1.0.0"); $c.Observation.StagedSelection.StagedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335") } }
        @{ Case = "the requested packages could not be parsed"; Mutate = { param($c) $c.Starts[0].RequestedPackages = $null; $c.Starts[0].RequestedReason = "SCHEMA_PACKAGES in the env file could not be parsed: x" } }
        @{ Case = "a core-only stack staged exactly its env's packages"; Mutate = { param($c) Add-CoreOnlyEnvironment $c; $c.Starts[0].Selection = "core-only"; $c.Observation.StagedSelection.StagedPackages = @($script:selCore) } }
        @{ Case = "the manifest was written at the very instant the start began"; Mutate = { param($c) $c.Observation.StagedSelection.ManifestLastWriteTimeUtc = "2026-10-08T11:00:00.0000000Z" } }
        @{ Case = "the times came back from JSON as DateTime and DateTimeOffset values"; Mutate = { param($c) $c.Observation.StagedSelection.ManifestLastWriteTimeUtc = [System.DateTime]::new(2026, 10, 8, 11, 1, 0, [System.DateTimeKind]::Utc); $c.Starts[0].StartedUtc = [System.DateTimeOffset]::new(2026, 10, 8, 12, 0, 0, [System.TimeSpan]::FromHours(1)) } }
    ) {
        $context = New-ReasonContext
        & $Mutate $context

        $reasons = Get-ContextReason $context

        $reasons.Count | Should -Be 0
    }

    It "reports exactly the expected reasons when <case>" -ForEach @(
        # Binding.
        @{ Case = "the observation names no stack start"; Mutate = { param($c) $c.Observation.StackStart = $null }; Expected = @("leg-separate-config: the observation names no stack start, so its staged selection is not bound to the stack it observed") }
        @{ Case = "the observation has no StackStart field"; Mutate = { param($c) $c.Observation.PSObject.Properties.Remove("StackStart") }; Expected = @("leg-separate-config: the observation names no stack start, so its staged selection is not bound to the stack it observed") }
        @{ Case = "the named stack start was never recorded"; Mutate = { param($c) $c.Observation.StackStart = "stack-start#9" }; Expected = @("leg-separate-config: 0 recorded stack starts are named 'stack-start#9'; expected exactly one") }
        @{ Case = "no stack start was recorded at all"; Mutate = { param($c) $c.Starts.Clear() }; Expected = @("leg-separate-config: 0 recorded stack starts are named 'stack-start#2'; expected exactly one") }
        @{ Case = "the stack start was recorded twice"; Mutate = { param($c) $c.Starts.Add($c.Starts[0]) }; Expected = @("leg-separate-config: 2 recorded stack starts are named 'stack-start#2'; expected exactly one") }
        @{ Case = "the stack start belongs to another step"; Mutate = { param($c) $c.Starts[0].Label = "build-source-datastore-default-minimal" }; Expected = @("leg-separate-config: stack start 'stack-start#2' belongs to step 'build-source-datastore-default-minimal'") }
        # Evidence.
        @{ Case = "no staged selection was recorded"; Mutate = { param($c) $c.Observation.PSObject.Properties.Remove("StagedSelection") }; Expected = @("leg-separate-config: the staged schema selection was not observed") }
        @{ Case = "the staged selection is null"; Mutate = { param($c) $c.Observation.StagedSelection = $null }; Expected = @("leg-separate-config: the staged schema selection was not observed") }
        @{ Case = "the workspace had no manifest"; Mutate = { param($c) $c.Observation.StagedSelection = [pscustomobject]@{ Source = "bootstrap-manifest.json schema.selectedPackages"; ManifestSha256 = $null; ManifestLastWriteTimeUtc = $null; StagedPackages = $null; Reason = "the active workspace has no bootstrap-manifest.json" } }; Expected = @("leg-separate-config: the staged schema selection was not observed: the active workspace has no bootstrap-manifest.json") }
        @{ Case = "the manifest was malformed"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = $null; $c.Observation.StagedSelection.Reason = "bootstrap-manifest.json is not valid JSON: bad" }; Expected = @("leg-separate-config: the staged schema selection was not observed: bootstrap-manifest.json is not valid JSON: bad") }
        @{ Case = "the manifest staged no package"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @() }; Expected = @("leg-separate-config: the workspace manifest records no staged package") }
        @{ Case = "the record has no StagedPackages field"; Mutate = { param($c) $c.Observation.StagedSelection.PSObject.Properties.Remove("StagedPackages") }; Expected = @("leg-separate-config: the workspace manifest records no staged package") }
        @{ Case = "an identity has no version"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selCore, "EdFi.DataStandard52.TPDM.ApiSchema") }; Expected = @("leg-separate-config: staged package 'EdFi.DataStandard52.TPDM.ApiSchema' is not a <name>@<version> identity") }
        @{ Case = "an identity is blank"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selCore, "") }; Expected = @("leg-separate-config: staged package '' is not a <name>@<version> identity") }
        @{ Case = "an identity contains whitespace"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selCore, "EdFi.DataStandard52.TPDM.ApiSchema @1.0.335") }; Expected = @("leg-separate-config: staged package 'EdFi.DataStandard52.TPDM.ApiSchema @1.0.335' is not a <name>@<version> identity") }
        @{ Case = "an identity has two separators"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selCore, "a@b@c") }; Expected = @("leg-separate-config: staged package 'a@b@c' is not a <name>@<version> identity") }
        @{ Case = "an identity is repeated in another case"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selCore, $script:selTpdm, $script:selTpdm.ToLowerInvariant()) }; Expected = @("leg-separate-config: the staged packages repeat an identity") }
        @{ Case = "no core package was staged"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selTpdm) }; Expected = @("leg-separate-config: the staged packages list 0 core packages (EdFi.DataStandard<NN>.ApiSchema); expected exactly one") }
        @{ Case = "two core packages were staged"; Mutate = { param($c) $c.Observation.StagedSelection.StagedPackages = @($script:selCore, "EdFi.DataStandard61.ApiSchema@1.0.0") }; Expected = @("leg-separate-config: the staged packages list 2 core packages (EdFi.DataStandard<NN>.ApiSchema); expected exactly one") }
        # Freshness.
        @{ Case = "the manifest was written before the start began"; Mutate = { param($c) $c.Observation.StagedSelection.ManifestLastWriteTimeUtc = "2026-10-08T10:30:00.0000000Z" }; Expected = @("leg-separate-config: the workspace manifest was written at 2026-10-08T10:30:00.0000000Z, before stack start 'stack-start#2' began at 2026-10-08T11:00:00.0000000Z, so its selection is not this stack's") }
        @{ Case = "the manifest's write time is missing"; Mutate = { param($c) $c.Observation.StagedSelection.ManifestLastWriteTimeUtc = $null }; Expected = @("leg-separate-config: the workspace manifest's write time or the start time of stack start 'stack-start#2' is not recorded") }
        @{ Case = "the start time is unparseable"; Mutate = { param($c) $c.Starts[0].StartedUtc = "soon" }; Expected = @("leg-separate-config: the workspace manifest's write time or the start time of stack start 'stack-start#2' is not recorded") }
        @{ Case = "a non-restore start left the manifest it found unchanged"; Mutate = { param($c) Set-KeptWorkspace $c; $c.Starts[0].Restore = $false }; Expected = @("leg-separate-config: the workspace manifest is the one present before stack start 'stack-start#2', which is not a restore, so the stack staged no selection of its own") }
        @{ Case = "a restore's manifest has the content found before it but an older write time"; Mutate = { param($c) Set-KeptWorkspace $c; $c.Starts[0].WorkspaceBefore.LastWriteTimeUtc = "2026-10-08T10:20:00.0000000Z" }; Expected = @("leg-separate-config: the workspace manifest was written at 2026-10-08T10:30:00.0000000Z, before stack start 'stack-start#2' began at 2026-10-08T11:00:00.0000000Z, so its selection is not this stack's") }
        @{ Case = "a restore's manifest has the write time found before it but other content"; Mutate = { param($c) Set-KeptWorkspace $c; $c.Starts[0].WorkspaceBefore.Sha256 = "f0" * 32 }; Expected = @("leg-separate-config: the workspace manifest was written at 2026-10-08T10:30:00.0000000Z, before stack start 'stack-start#2' began at 2026-10-08T11:00:00.0000000Z, so its selection is not this stack's") }
        @{ Case = "a restore found no manifest but one older than the start is observed"; Mutate = { param($c) Set-KeptWorkspace $c; $c.Starts[0].WorkspaceBefore.Present = $false }; Expected = @("leg-separate-config: the workspace manifest was written at 2026-10-08T10:30:00.0000000Z, before stack start 'stack-start#2' began at 2026-10-08T11:00:00.0000000Z, so its selection is not this stack's") }
        # Agreement.
        @{ Case = "the start records no selection"; Mutate = { param($c) $c.Starts[0].Selection = "" }; Expected = @("leg-separate-config: stack start 'stack-start#2' records no schema selection") }
        @{ Case = "a core-only start has no recorded env"; Mutate = { param($c) $c.Starts[0].Selection = "core-only"; $c.Observation.StagedSelection.StagedPackages = @($script:selCore) }; Expected = @("leg-separate-config: stack start 'stack-start#2' used the 'core-only' selection, which has 0 recorded envs; expected exactly one") }
        @{ Case = "a core-only start has two recorded envs"; Mutate = { param($c) Add-CoreOnlyEnvironment $c; Add-CoreOnlyEnvironment $c; $c.Starts[0].Selection = "core-only"; $c.Observation.StagedSelection.StagedPackages = @($script:selCore) }; Expected = @("leg-separate-config: stack start 'stack-start#2' used the 'core-only' selection, which has 2 recorded envs; expected exactly one") }
        @{ Case = "a core-only stack observed the default selection, freshly written"; Mutate = { param($c) Add-CoreOnlyEnvironment $c; $c.Starts[0].Selection = "core-only" }; Expected = @("leg-separate-config: the workspace staged [EdFi.DataStandard52.ApiSchema@1.0.335, EdFi.DataStandard52.TPDM.ApiSchema@1.0.335], but stack start 'stack-start#2' used the core-only selection [EdFi.DataStandard52.ApiSchema@1.0.335]") }
        @{ Case = "a core-only restore kept a default-selection workspace"; Mutate = { param($c) Add-CoreOnlyEnvironment $c; Set-KeptWorkspace $c; $c.Starts[0].Selection = "core-only" }; Expected = @("leg-separate-config: the workspace staged [EdFi.DataStandard52.ApiSchema@1.0.335, EdFi.DataStandard52.TPDM.ApiSchema@1.0.335], but stack start 'stack-start#2' used the core-only selection [EdFi.DataStandard52.ApiSchema@1.0.335]") }
    ) {
        $context = New-ReasonContext
        & $Mutate $context

        $reasons = Get-ContextReason $context

        $reasons | Should -Be $Expected
    }
}

Describe "Staged-selection wiring in the smoke (real smoke functions, stub wrapper, no Docker)" {
    BeforeAll {
        # The smoke's own wrapper, observation, and teardown functions, run against stub wrapper and
        # teardown scripts that write the workspace the way production does.
        $smokePath = Join-Path $PSScriptRoot "Invoke-BootstrapRestoreSmoke.ps1"
        $tokens = $null
        $errors = $null
        $smokeAst = [System.Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) {
            throw "The smoke did not parse: $($errors[0])"
        }
        foreach ($name in @("Invoke-RestoreWrapper", "Add-SmokeStackObservation", "Invoke-SmokeTeardown", "Get-SmokeEnvironmentSelection")) {
            $definitions = @($smokeAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
            if ($definitions.Count -ne 1) {
                throw "Expected one definition of $name in the smoke, found $($definitions.Count)."
            }
            . ([scriptblock]::Create($definitions[0].Extent.Text))
        }

        $script:WiringCore52 = "EdFi.DataStandard52.ApiSchema@1.0.335"
        $script:WiringTpdm52 = "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"
        $script:WiringCore61 = "EdFi.DataStandard61.ApiSchema@1.0.0"

        # Plain start: writes the manifest of the packages it staged plus .bootstrap/.env.derived.
        # Restore: stages a candidate (no derived env file) and commits it as
        # Publish-RestoreCandidateWorkspace does - kept when byte-identical to the active tree,
        # otherwise moved in. Production staging runs seconds after the wrapper starts; the stub
        # stamps its write one second ahead so a coarse filesystem clock cannot order this
        # immediate write before the recorded start.
        $script:WiringStub = @'
param(
    [string]$EnvironmentFile,
    [string]$DatabaseEngine,
    [string]$RestoreTemplate,
    [string]$PackageDirectory,
    [switch]$SeparateConfigDatabase,
    [switch]$LoadSeedData,
    [string]$SeedTemplate,
    [string]$DataStandardVersion
)
$ErrorActionPreference = "Stop"
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot "stub-plan.json") -Raw | ConvertFrom-Json
$bootstrapRoot = Join-Path $PSScriptRoot ".bootstrap"

function Write-StubManifest {
    param([string]$Root, [string[]]$Packages)

    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $path = Join-Path $Root "bootstrap-manifest.json"
    $manifest = [ordered]@{ version = 1; schema = [ordered]@{ selectionMode = "Standard"; selectedExtensions = @(); selectedPackages = @($Packages); effectiveSchemaHash = ("ab" * 32) } }
    [System.IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::SetLastWriteTimeUtc($path, [System.DateTime]::UtcNow.AddSeconds(1))
}

function Get-StubTree {
    param([string]$Root)

    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object {
            [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace("\", "/") + "=" + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        } | Sort-Object) -join "|"
}

$outcome = $plan.Action
switch ($plan.Action) {
    "stage" {
        Write-StubManifest -Root $bootstrapRoot -Packages @($plan.Packages)
        Set-Content -LiteralPath (Join-Path $bootstrapRoot ".env.derived") -Value "SCHEMA_PACKAGES='$($plan.DerivedSchemaPackages)'"
    }
    "restore" {
        $candidate = Join-Path $PSScriptRoot ".bootstrap-restore/candidate-$([Guid]::NewGuid().ToString('N'))"
        Write-StubManifest -Root $candidate -Packages @($plan.Packages)
        if ((Test-Path -LiteralPath $bootstrapRoot) -and (Get-StubTree $bootstrapRoot) -ceq (Get-StubTree $candidate)) {
            Remove-Item -LiteralPath $candidate -Recurse -Force
            $outcome = "restore-kept"
        }
        else {
            if (Test-Path -LiteralPath $bootstrapRoot) {
                Remove-Item -LiteralPath $bootstrapRoot -Recurse -Force
            }
            Move-Item -LiteralPath $candidate -Destination $bootstrapRoot
            $outcome = "restore-replaced"
        }
        if ($plan.PostCommitFile) {
            Set-Content -LiteralPath (Join-Path $bootstrapRoot $plan.PostCommitFile) -Value "written by a later phase"
        }
    }
    "nothing" { }
    "remove" { Remove-Item -LiteralPath $bootstrapRoot -Recurse -Force }
    "malformed" { Set-Content -LiteralPath (Join-Path $bootstrapRoot "bootstrap-manifest.json") -Value '{"schema":' }
    default { throw "unknown stub action $($plan.Action)" }
}
Add-Content -LiteralPath (Join-Path $PSScriptRoot "stub-calls.log") -Value "$outcome $RestoreTemplate"
'@

        $script:WiringTeardownStub = @'
param([switch]$d, [switch]$v, [switch]$RemoveBootstrap, [string]$EnvironmentFile, [string]$DatabaseEngine)
if ($RemoveBootstrap) {
    Remove-Item -LiteralPath (Join-Path $PSScriptRoot ".bootstrap") -Recurse -Force -ErrorAction SilentlyContinue
}
Add-Content -LiteralPath (Join-Path $PSScriptRoot "stub-calls.log") -Value "teardown $([bool]$RemoveBootstrap)"
'@

        function script:Set-WiringPlan {
            param([string]$Action, [string[]]$Packages = @(), [string]$DerivedSchemaPackages = "", [string]$PostCommitFile = "")

            [ordered]@{ Action = $Action; Packages = @($Packages); DerivedSchemaPackages = $DerivedSchemaPackages; PostCommitFile = $PostCommitFile } |
                ConvertTo-Json -Depth 3 |
                Set-Content -LiteralPath (Join-Path $script:DockerComposeRoot "stub-plan.json")
        }

        function script:Get-WiringCall {
            return @(Get-Content -LiteralPath (Join-Path $script:DockerComposeRoot "stub-calls.log"))
        }

        function script:Invoke-WiringStart {
            # One stack start in the current step, then the observation Wait-SmokeDmsHealth takes.
            param([hashtable]$Arguments, [switch]$NoObservation)

            Invoke-RestoreWrapper -Arguments $Arguments
            if (-not $NoObservation) {
                Add-SmokeStackObservation
            }
        }

        function script:Get-WiringReason {
            # The staged-selection reasons the classifier gives one observation of this run.
            param([int]$Index)

            $observation = $script:Provenance.StackObservations[$Index]
            return @(Get-RestoreSmokeStagedSelectionReason -Observation $observation -Label $observation.Label -StackStart @($script:Provenance.StackStarts) -SelectionEnvironment @($script:Provenance.SelectionEnvironments))
        }

        function script:Set-ManifestWrittenEarlier {
            # Time passes between two starts (a stop, the next step): the workspace the next start
            # finds was written an hour before it.
            $manifest = Join-Path $script:BootstrapRoot "bootstrap-manifest.json"
            [System.IO.File]::SetLastWriteTimeUtc($manifest, [System.DateTime]::UtcNow.AddHours(-1))
        }
    }

    BeforeEach {
        $script:DockerComposeRoot = Join-Path $TestDrive ("compose-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $script:DockerComposeRoot -Force | Out-Null
        foreach ($name in @("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
            Set-Content -LiteralPath (Join-Path $script:DockerComposeRoot $name) -Value $script:WiringStub
        }
        foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
            Set-Content -LiteralPath (Join-Path $script:DockerComposeRoot $name) -Value $script:WiringTeardownStub
        }
        $script:BootstrapRoot = Join-Path $script:DockerComposeRoot ".bootstrap"

        # The base env requests Core+TPDM 5.2; the core-only env requests the core package alone.
        $script:ResolvedEnvironmentFile = Join-Path $script:DockerComposeRoot ".env.smoke-images"
        Set-Content -LiteralPath $script:ResolvedEnvironmentFile -Value @("DMS_IMAGE_TAG=dms-restore-smoke-0123456789ab", "SCHEMA_PACKAGES='[{""name"":""EdFi.DataStandard52.ApiSchema"",""version"":""1.0.335""},{""name"":""EdFi.DataStandard52.TPDM.ApiSchema"",""version"":""1.0.335""}]'")
        $script:CoreOnlyEnvironmentFile = Join-Path $script:DockerComposeRoot ".env.smoke-core-only"
        Set-Content -LiteralPath $script:CoreOnlyEnvironmentFile -Value @("DMS_IMAGE_TAG=dms-restore-smoke-0123456789ab", "SCHEMA_PACKAGES='[{""name"":""EdFi.DataStandard52.ApiSchema"",""version"":""1.0.335""}]'")
        $script:SelectionEnvironmentFiles = @{}

        $script:Wrapper = "published"
        $script:DataStandardVersion = "5.2"
        $script:DataStandardVersionSupplied = $false
        $script:DatabaseEngine = "postgresql"
        $script:WrapperProfile = Get-RestoreSmokeWrapperProfile -Wrapper $script:Wrapper
        $script:ApiSession = New-RestoreSmokeApiSession
        $script:TeardownAuthorized = $true
        $script:CurrentStepName = $null
        $script:CurrentStackStart = $null
        $script:Provenance = [ordered]@{
            StackStarts           = [System.Collections.Generic.List[object]]::new()
            StackObservations     = [System.Collections.Generic.List[object]]::new()
            SelectionEnvironments = [System.Collections.Generic.List[object]]::new()
        }

        Mock Get-RestoreSmokeStackObservation { [pscustomobject]@{ Label = $Label; Services = [ordered]@{}; Reason = $null } }
    }

    It "records the staged overlay packages of a local start, not the base env's request or its derived env file" {
        $script:Wrapper = "local"
        $script:WrapperProfile = Get-RestoreSmokeWrapperProfile -Wrapper "local"
        $script:DataStandardVersion = "6.1"
        $script:CurrentStepName = "build-source-datastore-default-minimal"
        Set-WiringPlan -Action "stage" -Packages @($script:WiringCore61) -DerivedSchemaPackages '[{"name":"Unrelated.Derived.Package","version":"9.9.9"}]'

        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" }

        Test-Path -LiteralPath (Join-Path $script:BootstrapRoot ".env.derived") | Should -BeTrue
        $start = $script:Provenance.StackStarts[0]
        $observation = $script:Provenance.StackObservations[0]
        @($start.RequestedPackages) | Should -Be @($script:WiringCore52, $script:WiringTpdm52)
        $start.Restore | Should -BeFalse
        $start.Selection | Should -BeExactly "default"
        $observation.StackStart | Should -BeExactly "stack-start#1"
        @($observation.StagedSelection.StagedPackages) | Should -Be @($script:WiringCore61)
        (Get-WiringReason 0).Count | Should -Be 0
    }

    It "binds a published core-only restore that replaced a default workspace, with no derived env file left" {
        $script:CurrentStepName = "leg-separate-config"
        Set-WiringPlan -Action "stage" -Packages @($script:WiringCore52, $script:WiringTpdm52) -DerivedSchemaPackages "[]"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" }
        Invoke-SmokeTeardown -KeepVolumes

        $script:SelectionEnvironmentFiles["core-only"] = $script:CoreOnlyEnvironmentFile
        $script:Provenance.SelectionEnvironments.Add([pscustomobject]@{ Selection = "core-only"; FileName = ".env.smoke-core-only"; SelectedPackages = @($script:WiringCore52); RemovedPackages = @($script:WiringTpdm52) })
        $script:CurrentStepName = "leg-extension-selection"
        Set-WiringPlan -Action "restore" -Packages @($script:WiringCore52)
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:CoreOnlyEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-core-only-minimal" }

        Get-WiringCall | Should -Be @("stage ", "teardown False", "restore-replaced Minimal")
        Test-Path -LiteralPath (Join-Path $script:BootstrapRoot ".env.derived") | Should -BeFalse
        $start = $script:Provenance.StackStarts[1]
        $start.Restore | Should -BeTrue
        $start.Selection | Should -BeExactly "core-only"
        $start.WorkspaceBefore.Present | Should -BeTrue
        $script:Provenance.StackObservations[1].StackStart | Should -BeExactly "stack-start#2"
        @($script:Provenance.StackObservations[1].StagedSelection.StagedPackages) | Should -Be @($script:WiringCore52)
        (Get-WiringReason 0).Count | Should -Be 0
        (Get-WiringReason 1).Count | Should -Be 0
    }

    It "accepts a repeated restore that kept the workspace byte-identical to its candidate" {
        $script:CurrentStepName = "leg-package-directory"
        Set-WiringPlan -Action "restore" -Packages @($script:WiringCore52, $script:WiringTpdm52)
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-default-minimal" }
        Invoke-SmokeTeardown -KeepVolumes
        Set-ManifestWrittenEarlier

        $script:CurrentStepName = "leg-package-directory-repeat"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-default-minimal" }

        Get-WiringCall | Should -Be @("restore-replaced Minimal", "teardown False", "restore-kept Minimal")
        $second = $script:Provenance.StackObservations[1]
        $second.StackStart | Should -BeExactly "stack-start#2"
        $second.StagedSelection.ManifestSha256 | Should -BeExactly $script:Provenance.StackStarts[1].WorkspaceBefore.Sha256
        $second.StagedSelection.ManifestLastWriteTimeUtc | Should -BeExactly $script:Provenance.StackStarts[1].WorkspaceBefore.LastWriteTimeUtc
        (Get-WiringReason 0).Count | Should -Be 0
        (Get-WiringReason 1).Count | Should -Be 0
    }

    It "binds each of two restores that replaced the workspace to its own start" {
        $script:CurrentStepName = "leg-package-directory"
        Set-WiringPlan -Action "restore" -Packages @($script:WiringCore52, $script:WiringTpdm52) -PostCommitFile "seed-delivered.txt"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-default-minimal" }
        Invoke-SmokeTeardown -KeepVolumes
        Set-ManifestWrittenEarlier

        $script:CurrentStepName = "leg-package-directory-repeat"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-default-minimal" }

        Get-WiringCall | Should -Be @("restore-replaced Minimal", "teardown False", "restore-replaced Minimal")
        @($script:Provenance.StackObservations | ForEach-Object { $_.StackStart }) | Should -Be @("stack-start#1", "stack-start#2")
        $script:Provenance.StackObservations[1].StagedSelection.ManifestLastWriteTimeUtc | Should -Not -Be $script:Provenance.StackStarts[1].WorkspaceBefore.LastWriteTimeUtc
        (Get-WiringReason 0).Count | Should -Be 0
        (Get-WiringReason 1).Count | Should -Be 0
    }

    It "binds the two starts of one step (the separate-config shape) each to its own observation" {
        $script:CurrentStepName = "leg-separate-config"
        Set-WiringPlan -Action "stage" -Packages @($script:WiringCore52, $script:WiringTpdm52) -DerivedSchemaPackages "[]"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; SeparateConfigDatabase = $true }
        Invoke-SmokeTeardown -KeepVolumes
        Set-ManifestWrittenEarlier
        Set-WiringPlan -Action "restore" -Packages @($script:WiringCore52, $script:WiringTpdm52)
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-default-minimal"; SeparateConfigDatabase = $true }

        @($script:Provenance.StackStarts | ForEach-Object { $_.Label }) | Should -Be @("leg-separate-config", "leg-separate-config")
        @($script:Provenance.StackObservations | ForEach-Object { $_.StackStart }) | Should -Be @("stack-start#1", "stack-start#2")
        (Get-WiringReason 0).Count | Should -Be 0
        (Get-WiringReason 1).Count | Should -Be 0
    }

    It "rejects a non-restore start that left the workspace it found unchanged" {
        $script:CurrentStepName = "leg-running-stack"
        Set-WiringPlan -Action "stage" -Packages @($script:WiringCore52, $script:WiringTpdm52) -DerivedSchemaPackages "[]"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" }
        Invoke-SmokeTeardown -KeepVolumes
        Set-ManifestWrittenEarlier
        Set-WiringPlan -Action "nothing"

        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" }

        Get-WiringReason 1 | Should -Be @("leg-running-stack: the workspace manifest is the one present before stack start 'stack-start#2', which is not a restore, so the stack staged no selection of its own")
    }

    It "keeps default-selection evidence out of the core-only restore when <case>" -ForEach @(
        @{ Case = "the restore left the default workspace in place"; Action = "nothing" }
        @{ Case = "the restore rewrote the default selection"; Action = "restore-default" }
    ) {
        $script:CurrentStepName = "build-source-datastore-default-minimal"
        Set-WiringPlan -Action "stage" -Packages @($script:WiringCore52, $script:WiringTpdm52) -DerivedSchemaPackages "[]"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" }
        Invoke-SmokeTeardown -KeepVolumes
        Set-ManifestWrittenEarlier

        $script:SelectionEnvironmentFiles["core-only"] = $script:CoreOnlyEnvironmentFile
        $script:Provenance.SelectionEnvironments.Add([pscustomobject]@{ Selection = "core-only"; FileName = ".env.smoke-core-only"; SelectedPackages = @($script:WiringCore52); RemovedPackages = @($script:WiringTpdm52) })
        $script:CurrentStepName = "leg-extension-selection"
        if ($Action -eq "nothing") {
            Set-WiringPlan -Action "nothing"
        }
        else {
            Set-WiringPlan -Action "restore" -Packages @($script:WiringCore52, $script:WiringTpdm52)
        }
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:CoreOnlyEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-core-only-minimal" }

        $script:Provenance.StackStarts[1].Selection | Should -BeExactly "core-only"
        Get-WiringReason 1 | Should -Be @("leg-extension-selection: the workspace staged [EdFi.DataStandard52.ApiSchema@1.0.335, EdFi.DataStandard52.TPDM.ApiSchema@1.0.335], but stack start 'stack-start#2' used the core-only selection [EdFi.DataStandard52.ApiSchema@1.0.335]")
    }

    It "leaves an observation taken after a teardown unbound" {
        $script:CurrentStepName = "leg-package-directory"
        Set-WiringPlan -Action "restore" -Packages @($script:WiringCore52, $script:WiringTpdm52)
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql"; RestoreTemplate = "Minimal"; PackageDirectory = "package-default-minimal" }
        Invoke-SmokeTeardown -KeepVolumes

        Add-SmokeStackObservation

        $script:Provenance.StackObservations[1].StackStart | Should -BeNullOrEmpty
        Get-WiringReason 1 | Should -Be @("leg-package-directory: the observation names no stack start, so its staged selection is not bound to the stack it observed")
    }

    It "reports a start whose workspace is <case> afterwards" -ForEach @(
        @{ Case = "missing"; Action = "remove"; Expected = "leg-populated: the staged schema selection was not observed: the active workspace has no bootstrap-manifest.json" }
        @{ Case = "malformed"; Action = "malformed"; Expected = "leg-populated: the staged schema selection was not observed: bootstrap-manifest.json is not valid JSON: *" }
    ) {
        $script:CurrentStepName = "leg-populated"
        Set-WiringPlan -Action "stage" -Packages @($script:WiringCore52, $script:WiringTpdm52) -DerivedSchemaPackages "[]"
        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" } -NoObservation
        Set-WiringPlan -Action $Action

        Invoke-WiringStart -Arguments @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = "postgresql" }

        $reasons = @(Get-WiringReason 0)
        $reasons.Count | Should -Be 1
        $reasons[0] | Should -BeLike $Expected
    }
}

Describe "Get-RestoreSmokePackageProvenance" {
    BeforeAll {
        function script:New-FakeTemplatePackage {
            param([string]$Directory, [string]$RecordedSha, [string]$ManifestJson = ('{"projects":["edfi","tpdm"],"effectiveSchemaHash":"' + ("e5" * 32) + '"}'))

            New-Item -ItemType Directory -Path $Directory -Force | Out-Null
            $contents = Join-Path $Directory "contents"
            New-Item -ItemType Directory -Path $contents -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $contents "restore-manifest.json") -Value $ManifestJson
            $packagePath = Join-Path $Directory "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.999.nupkg"
            Compress-Archive -Path (Join-Path $contents "*") -DestinationPath ($packagePath + ".zip")
            Move-Item -LiteralPath ($packagePath + ".zip") -Destination $packagePath
            Remove-Item -LiteralPath $contents -Recurse -Force

            $sha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $payload = @{ packageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"; packageVersion = "1.0.999"; packageSha256 = ($RecordedSha ? $RecordedSha : $sha); producer = "restore-smoke-1234" } | ConvertTo-Json -Compress
            @{
                version    = 1
                payloadB64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($payload))
                signature  = @{ algorithm = "ECDSA_P256_SHA256"; keyId = ("ab" * 32); valueB64 = "AA==" }
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$packagePath.attestation.json"
            return $sha
        }
    }

    It "records the package, attestation, and restore-manifest projects as observed" {
        $directory = Join-Path $TestDrive "package-ok"
        $sha = New-FakeTemplatePackage -Directory $directory

        $record = Get-RestoreSmokePackageProvenance -PackageDirectory $directory -RestoreManifestFileName "restore-manifest.json" -PackageFixture default-minimal

        $record.Verified | Should -BeTrue
        $record.Sha256 | Should -Be $sha
        $record.PackageId | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
        $record.PackageVersion | Should -Be "1.0.999"
        $record.AttestationProducer | Should -Be "restore-smoke-1234"
        $record.AttestationKeyId | Should -Be ("ab" * 32)
        $record.RestoreManifestProjects | Should -Be @("edfi", "tpdm")
        $record.RestoreManifestEffectiveSchemaHash | Should -BeExactly ("e5" * 32)
        $record.PackageFixture | Should -BeExactly "default-minimal"
        $record.TemplateKind | Should -BeExactly "Minimal"
    }

    It "records the fixture's kind, and no effective schema hash when the restore manifest has none" {
        $directory = Join-Path $TestDrive "package-core-only"
        $null = New-FakeTemplatePackage -Directory $directory -ManifestJson '{"projects":["edfi"]}'

        $record = Get-RestoreSmokePackageProvenance -PackageDirectory $directory -RestoreManifestFileName "restore-manifest.json" -PackageFixture core-only-minimal

        $record.PackageFixture | Should -BeExactly "core-only-minimal"
        $record.TemplateKind | Should -BeExactly "Minimal"
        $record.RestoreManifestProjects | Should -Be @("edfi")
        $record.RestoreManifestEffectiveSchemaHash | Should -BeNullOrEmpty
    }

    It "is not verified when the attestation's recorded SHA does not match the file" {
        $directory = Join-Path $TestDrive "package-mismatch"
        $null = New-FakeTemplatePackage -Directory $directory -RecordedSha ("0" * 64)

        $record = Get-RestoreSmokePackageProvenance -PackageDirectory $directory -RestoreManifestFileName "restore-manifest.json" -PackageFixture default-minimal

        $record.Verified | Should -BeFalse
        $record.Reason | Should -BeLike "*records packageSha256 $("0" * 64), but the file hashes to*"
    }
}

Describe "Get-RestoreSmokeSourceRevision" {
    BeforeAll {
        function script:New-RevisionTestRepository {
            $directory = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $directory "tracked.txt") -Value "original"
            $head = New-CleanTestRepository -Directory $directory
            return [pscustomobject]@{ Directory = $directory; Head = $head }
        }
    }

    It "reports a clean checkout as observed and clean, with an empty porcelain array" {
        $repository = New-RevisionTestRepository

        $revision = Get-RestoreSmokeSourceRevision -RepoRoot $repository.Directory

        $revision.Observed | Should -BeTrue
        $revision.Clean | Should -BeTrue
        $revision.Revision | Should -Be $repository.Head
        $revision.Reason | Should -BeNullOrEmpty
        ($revision.Porcelain -is [array]) | Should -BeTrue
        $revision.Porcelain.Count | Should -Be 0
    }

    It "reports one porcelain entry as a one-element array" {
        $repository = New-RevisionTestRepository
        Set-Content -LiteralPath (Join-Path $repository.Directory "tracked.txt") -Value "changed"

        $revision = Get-RestoreSmokeSourceRevision -RepoRoot $repository.Directory

        $revision.Observed | Should -BeTrue
        $revision.Clean | Should -BeFalse
        ($revision.Porcelain -is [array]) | Should -BeTrue
        $revision.Porcelain.Count | Should -Be 1
        $revision.Porcelain[0] | Should -Be "M tracked.txt"
    }

    It "reports every porcelain entry when several files changed" {
        $repository = New-RevisionTestRepository
        Set-Content -LiteralPath (Join-Path $repository.Directory "tracked.txt") -Value "changed"
        Set-Content -LiteralPath (Join-Path $repository.Directory "untracked.txt") -Value "new"

        $revision = Get-RestoreSmokeSourceRevision -RepoRoot $repository.Directory

        $revision.Clean | Should -BeFalse
        $revision.Porcelain.Count | Should -Be 2
        $revision.Porcelain | Should -Contain "M tracked.txt"
        $revision.Porcelain | Should -Contain "?? untracked.txt"
    }

    It "reports a git failure as unobserved, not clean, with the exit codes and an empty porcelain array" {
        $outside = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $notARepository = Join-Path $outside "not-a-repository"
        New-Item -ItemType Directory -Path $notARepository -Force | Out-Null
        $savedCeiling = $env:GIT_CEILING_DIRECTORIES
        try {
            # Keeps git from discovering any repository above the directory.
            $env:GIT_CEILING_DIRECTORIES = $outside

            $revision = Get-RestoreSmokeSourceRevision -RepoRoot $notARepository
        }
        finally {
            $env:GIT_CEILING_DIRECTORIES = $savedCeiling
        }

        $revision.Observed | Should -BeFalse
        $revision.Clean | Should -BeFalse
        $revision.Revision | Should -BeNullOrEmpty
        $revision.Reason | Should -BeLike "git rev-parse exit *, git status exit *"
        $revision.Reason | Should -Not -BeLike "git rev-parse exit 0, git status exit 0"
        ($revision.Porcelain -is [array]) | Should -BeTrue
        $revision.Porcelain.Count | Should -Be 0
    }
}

Describe "Served-data API probe" {
    BeforeAll {
        $script:pgConnection = "host=dms-postgresql;port=5432;username=postgres;password=Pg-S3cret!;database=edfi_datamanagementservice"
        $script:mssqlConnection = "Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=Ms-S3cret!;TrustServerCertificate=true"
        $script:target = "edfi_datamanagementservice"
        $script:descriptorUri = "http://localhost:18080/data/ed-fi/academicSubjectDescriptors?limit=5"
        $script:schoolUri = "http://localhost:18080/data/ed-fi/schools?limit=5"

        # The shape Get-DataStore returns (Invoke-RestMethod objects, camelCase CMS properties).
        function script:New-TestDataStore {
            param(
                [object]$Id,
                [string]$Name = "Local Development Data Store",
                [AllowNull()]
                [object]$ConnectionString = $script:pgConnection,
                [object]$Contexts = @()
            )

            return [pscustomobject]@{ id = $Id; dataStoreType = "Development"; name = $Name; connectionString = $ConnectionString; dataStoreContexts = $Contexts }
        }

        function script:New-TestEndpoint {
            return [pscustomobject]@{
                CmsUrl      = "http://localhost:18081"
                DmsUrl      = "http://localhost:18080"
                AdminClient = [pscustomobject]@{ ClientId = "dms-data-store-admin"; ClientSecret = "Admin-S3cret!" }
            }
        }
    }

    Context "Resolve-RestoreSmokeApiEndpoint" {
        It "reads the stack's ports and bootstrap admin client from the env file" {
            $file = Join-Path $TestDrive "endpoint.env"
            Set-Content -LiteralPath $file -Value @(
                "DMS_HTTP_PORTS=18080"
                "DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081"
                "DMS_BOOTSTRAP_ADMIN_CLIENT_ID=custom-admin"
                "DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET=Custom-S3cret!"
                "DMS_CONFIG_MULTI_TENANCY=false"
            )

            $endpoint = Resolve-RestoreSmokeApiEndpoint -EnvironmentFile $file

            $endpoint.CmsUrl | Should -Be "http://localhost:18081"
            $endpoint.DmsUrl | Should -Be "http://localhost:18080"
            $endpoint.AdminClient.ClientId | Should -Be "custom-admin"
            $endpoint.AdminClient.ClientSecret | Should -Be "Custom-S3cret!"
        }

        It "falls back to the defaults the configure phase uses when the keys are absent" {
            $file = Join-Path $TestDrive "defaults.env"
            Set-Content -LiteralPath $file -Value "POSTGRES_DB_NAME=edfi_datamanagementservice"

            $endpoint = Resolve-RestoreSmokeApiEndpoint -EnvironmentFile $file

            $endpoint.CmsUrl | Should -Be "http://localhost:8081"
            $endpoint.DmsUrl | Should -Be "http://localhost:8080"
            $endpoint.AdminClient.ClientId | Should -Be "dms-data-store-admin"
        }

        It "refuses a multi-tenant stack (DMS_CONFIG_MULTI_TENANCY=<value>)" -ForEach @(
            @{ Value = "true" }
            @{ Value = "True" }
            @{ Value = '"true"' }
        ) {
            $file = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N") + ".env")
            Set-Content -LiteralPath $file -Value "DMS_CONFIG_MULTI_TENANCY=$Value"

            { Resolve-RestoreSmokeApiEndpoint -EnvironmentFile $file } | Should -Throw "The served-data probe supports only a single-tenant stack*"
        }
    }

    Context "Get-RestoreSmokeJsonArrayLength" {
        It "returns the length of a JSON array with <case>" -ForEach @(
            @{ Case = "no elements"; Content = '[]'; Expected = 0 }
            @{ Case = "one element"; Content = '[{"codeValue":"Math"}]'; Expected = 1 }
            @{ Case = "several elements"; Content = '[{"codeValue":"Math"},{"codeValue":"Art"},{"codeValue":"Science"}]'; Expected = 3 }
        ) {
            $shape = Get-RestoreSmokeJsonArrayLength -Content $Content

            $shape.IsArray | Should -BeTrue
            $shape.Length | Should -Be $Expected
            $shape.Reason | Should -BeNullOrEmpty
        }

        It "rejects <case>" -ForEach @(
            @{ Case = "a JSON object"; Content = '{"codeValue":"Math"}'; Reason = "the body's JSON root is object, not an array" }
            @{ Case = "an object wrapping an array"; Content = '{"items":[{"codeValue":"Math"}]}'; Reason = "the body's JSON root is object, not an array" }
            @{ Case = "a JSON string"; Content = '"Math"'; Reason = "the body's JSON root is string, not an array" }
            @{ Case = "a JSON number"; Content = '5'; Reason = "the body's JSON root is number, not an array" }
            @{ Case = "JSON null"; Content = 'null'; Reason = "the body's JSON root is null, not an array" }
            @{ Case = "an array of numbers"; Content = '[1,2]'; Reason = "array element 0 is number, not a resource object" }
            @{ Case = "an array holding null after an object"; Content = '[{"codeValue":"Math"},null]'; Reason = "array element 1 is null, not a resource object" }
            @{ Case = "a nested array"; Content = '[[{"codeValue":"Math"}]]'; Reason = "array element 0 is array, not a resource object" }
            @{ Case = "two JSON values"; Content = '[] []'; Reason = "the body is not a single JSON value" }
            @{ Case = "text that is not JSON"; Content = 'Service Unavailable'; Reason = "the body is not a single JSON value" }
            @{ Case = "an empty body"; Content = ''; Reason = "the body is empty" }
            @{ Case = "a whitespace body"; Content = "  `n"; Reason = "the body is empty" }
        ) {
            $shape = Get-RestoreSmokeJsonArrayLength -Content $Content

            $shape.IsArray | Should -BeFalse
            $shape.Length | Should -BeNullOrEmpty
            $shape.Reason | Should -Be $Reason
        }

        It "tells a one-element array from the object it holds, which ConvertFrom-Json does not" {
            # The trap: after conversion the singleton array and the bare object are the same value.
            $fromArray = '[{"codeValue":"Math"}]' | ConvertFrom-Json
            $fromObject = '{"codeValue":"Math"}' | ConvertFrom-Json
            @($fromArray).Count | Should -Be @($fromObject).Count
            $fromArray.codeValue | Should -Be $fromObject.codeValue

            (Get-RestoreSmokeJsonArrayLength -Content '[{"codeValue":"Math"}]').IsArray | Should -BeTrue
            (Get-RestoreSmokeJsonArrayLength -Content '{"codeValue":"Math"}').IsArray | Should -BeFalse
        }
    }

    Context "Select-RestoreSmokeDataStore" {
        It "selects the one route-unqualified data store for the restored database among others" {
            $stores = @(
                New-TestDataStore -Id 1 -Name "Year 2025" -Contexts @([pscustomobject]@{ id = 1; dataStoreId = 1; contextKey = "schoolYear"; contextValue = "2025" })
                New-TestDataStore -Id 2 -Name "Other" -ConnectionString ($script:pgConnection -replace 'database=edfi_datamanagementservice', 'database=edfi_other')
                New-TestDataStore -Id 3
            )

            $result = Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine postgresql

            $result.DataStoreId | Should -Be 3
            $result.Name | Should -Be "Local Development Data Store"
            @($result.Candidates.Id) | Should -Be @(1, 2, 3)
            @($result.Candidates.Selected) | Should -Be @($false, $false, $true)
            @($result.Candidates.Reason) | Should -Be @(
                "it is route-qualified (schoolYear=2025)"
                "it targets database 'edfi_other', not 'edfi_datamanagementservice'"
                "route-unqualified postgresql data store for 'edfi_datamanagementservice'"
            )
            $result.Candidates[2].DatabaseName | Should -Be "edfi_datamanagementservice"
        }

        It "selects only the <engine> connection-string form" -ForEach @(
            @{ Engine = "mssql"; SelectedId = 2; OtherReason = "its connection string is not the mssql form (no Server or Data Source key)" }
            @{ Engine = "postgresql"; SelectedId = 1; OtherReason = "its connection string is not the postgresql form (no Host key)" }
        ) {
            $stores = @(
                New-TestDataStore -Id 1 -ConnectionString $script:pgConnection
                New-TestDataStore -Id 2 -ConnectionString $script:mssqlConnection
            )

            $result = Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine $Engine

            $result.DataStoreId | Should -Be $SelectedId
            @($result.Candidates | Where-Object { -not $_.Selected }).Reason | Should -Be $OtherReason
        }

        It "accepts the SqlClient synonyms Data Source and Initial Catalog" {
            $stores = @(New-TestDataStore -Id 5 -ConnectionString "Data Source=dms-mssql,1433;Initial Catalog=edfi_datamanagementservice;User Id=sa;Password=Ms-S3cret!")

            (Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine mssql).DataStoreId | Should -Be 5
        }

        It "fails when CMS lists no data store" {
            { Select-RestoreSmokeDataStore -DataStores @() -TargetDatabaseName $script:target -DatabaseEngine postgresql } |
                Should -Throw -ExpectedMessage "No route-unqualified data store targets the restored database 'edfi_datamanagementservice' (0 listed)."
        }

        It "fails when the only data store for the target is route-qualified" {
            $stores = @(New-TestDataStore -Id 4 -Contexts @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2025" }))

            { Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine postgresql } |
                Should -Throw -ExpectedMessage "No route-unqualified data store targets the restored database 'edfi_datamanagementservice' (1 listed: id=4 name='Local Development Data Store': it is route-qualified (schoolYear=2025))."
        }

        It "fails on an ambiguous match, naming every matching id" {
            $stores = @(
                New-TestDataStore -Id 4
                New-TestDataStore -Id 7 -Name "Duplicate"
            )

            { Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine postgresql } |
                Should -Throw -ExpectedMessage "2 route-unqualified data stores target the restored database 'edfi_datamanagementservice' (ids 4, 7); the probe needs exactly one.*"
        }

        It "does not select a data store <case>" -ForEach @(
            @{ Case = "without an id"; Store = @{ Id = $null }; Reason = "it has no positive integer id" }
            @{ Case = "with id 0"; Store = @{ Id = 0 }; Reason = "it has no positive integer id" }
            @{ Case = "with a non-numeric id"; Store = @{ Id = "abc" }; Reason = "it has no positive integer id" }
            @{ Case = "whose route contexts are a string"; Store = @{ Id = 3; Contexts = "schoolYear=2025" }; Reason = "its route contexts are not a list" }
            @{ Case = "without a connection string"; Store = @{ Id = 3; ConnectionString = $null }; Reason = "CMS returned no connection string for it" }
            @{ Case = "with an unparsable connection string"; Store = @{ Id = 3; ConnectionString = "host=dms-postgresql;database" }; Reason = "its connection string could not be parsed" }
            @{ Case = "whose database differs only by case"; Store = @{ Id = 3; ConnectionString = "host=dms-postgresql;database=EdFi_DataManagementService" }; Reason = "it targets database 'EdFi_DataManagementService', not 'edfi_datamanagementservice'" }
            @{ Case = "without a database"; Store = @{ Id = 3; ConnectionString = "host=dms-postgresql;port=5432" }; Reason = "its connection string names 0 databases" }
        ) {
            $parameters = @{}
            foreach ($key in $Store.Keys) { $parameters[$key] = $Store[$key] }
            $stores = @(New-TestDataStore @parameters)

            $failure = $null
            try {
                Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine postgresql
            }
            catch {
                $failure = $_.Exception.Message
            }

            $failure | Should -BeLike "No route-unqualified data store targets the restored database 'edfi_datamanagementservice' (1 listed: *: $Reason)."
        }

        It "does not select a SQL Server data store that names two databases" {
            $stores = @(New-TestDataStore -Id 3 -ConnectionString "Server=dms-mssql,1433;Database=edfi_datamanagementservice;Initial Catalog=edfi_other;User Id=sa;Password=Ms-S3cret!")

            { Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine mssql } |
                Should -Throw -ExpectedMessage "*: its connection string names 2 databases)."
        }

        It "never reports a connection string or its password" {
            $stores = @(
                New-TestDataStore -Id 3
                New-TestDataStore -Id 4 -ConnectionString $script:mssqlConnection
                New-TestDataStore -Id 5
            )

            $failure = $null
            try {
                Select-RestoreSmokeDataStore -DataStores $stores -TargetDatabaseName $script:target -DatabaseEngine postgresql
            }
            catch {
                $failure = $_.Exception.Message
            }
            $selected = Select-RestoreSmokeDataStore -DataStores @($stores[0], $stores[1]) -TargetDatabaseName $script:target -DatabaseEngine postgresql | ConvertTo-Json -Depth 6

            foreach ($text in @($failure, $selected)) {
                $text | Should -Not -BeNullOrEmpty
                $text | Should -Not -Match '(?i)password|Pg-S3cret|Ms-S3cret|dms-postgresql|dms-mssql'
            }
        }
    }

    Context "Test-RestoreSmokeApiRead" {
        BeforeEach {
            $global:RestoreSmokeApiTest = @{
                DataStores = @(New-TestDataStore -Id 3)
                Tokens     = [System.Collections.Generic.Queue[string]]::new([string[]]@("dms-token-1", "dms-token-2"))
                Fail       = $null
                Responses  = @{
                    academicSubjectDescriptors = @{ StatusCode = 200; Content = '[{"codeValue":"Math"},{"codeValue":"Art"},{"codeValue":"Science"}]' }
                    schools                    = @{ StatusCode = 200; Content = '[{"schoolId":255901001}]' }
                }
                Calls      = [System.Collections.Generic.List[string]]::new()
            }

            # Each helper fails, when asked to, with a message that quotes every secret in play.
            Mock Get-CmsToken -ModuleName RestoreSmokeProbes {
                $global:RestoreSmokeApiTest.Calls.Add("Get-CmsToken")
                if ($global:RestoreSmokeApiTest.Fail -eq "Get-CmsToken") { throw "401 for dms-data-store-admin with Admin-S3cret!" }
                return "cms-admin-token"
            }
            Mock Get-DataStore -ModuleName RestoreSmokeProbes {
                $global:RestoreSmokeApiTest.Calls.Add("Get-DataStore")
                if ($global:RestoreSmokeApiTest.Fail -eq "Get-DataStore") { throw "403 for bearer cms-admin-token" }
                return $global:RestoreSmokeApiTest.DataStores
            }
            Mock Get-SmokeTestCredential -ModuleName RestoreSmokeProbes {
                $global:RestoreSmokeApiTest.Calls.Add("Get-SmokeTestCredential")
                if ($global:RestoreSmokeApiTest.Fail -eq "Get-SmokeTestCredential") { throw "Failed to create smoke test credentials: 500 (admin Admin-S3cret!)" }
                return @{ Key = "app-key"; Secret = "App-S3cret!"; VendorId = 1; ApplicationName = "Smoke Test Application" }
            }
            Mock Wait-CmsClientAvailable -ModuleName RestoreSmokeProbes {
                $global:RestoreSmokeApiTest.Calls.Add("Wait-CmsClientAvailable")
                if ($global:RestoreSmokeApiTest.Fail -eq "Wait-CmsClientAvailable") { throw "CMS client 'app-key' (App-S3cret!) did not become available" }
            }
            Mock Get-DmsToken -ModuleName RestoreSmokeProbes {
                $global:RestoreSmokeApiTest.Calls.Add("Get-DmsToken")
                if ($global:RestoreSmokeApiTest.Fail -eq "Get-DmsToken") { throw "401 for app-key:App-S3cret!" }
                if ($global:RestoreSmokeApiTest.Fail -eq "Get-DmsToken-blank") { return "" }
                return $global:RestoreSmokeApiTest.Tokens.Dequeue()
            }
            Mock Invoke-WebRequest -ModuleName RestoreSmokeProbes {
                $global:RestoreSmokeApiTest.Calls.Add("GET $Uri")
                if ($global:RestoreSmokeApiTest.Fail -eq "GET") { throw "connection refused ($($Headers.Authorization))" }
                $resource = ([uri]$Uri).AbsolutePath.Split("/")[-1]
                $response = $global:RestoreSmokeApiTest.Responses[$resource]
                return [pscustomobject]@{ StatusCode = $response.StatusCode; Content = $response.Content }
            }

            $script:session = New-RestoreSmokeApiSession
            $script:endpoint = New-TestEndpoint
            $script:readArguments = @{ Session = $script:session; Endpoint = $script:endpoint; TargetDatabaseName = $script:target; DatabaseEngine = "postgresql" }
        }

        It "reads a non-empty descriptor array with one token bound to the selected data store" {
            $result = Test-RestoreSmokeApiRead @script:readArguments

            $result.Mode | Should -Be "seeded"
            $result.CmsUrl | Should -Be "http://localhost:18081"
            $result.DmsUrl | Should -Be "http://localhost:18080"
            $result.DataStoreId | Should -Be 3
            $result.DataStoreName | Should -Be "Local Development Data Store"
            $result.TokenReused | Should -BeFalse
            $result.TokenRequestCount | Should -Be 1
            $result.Reads.Count | Should -Be 1
            $result.Reads[0].Resource | Should -Be "academicSubjectDescriptors"
            $result.Reads[0].Uri | Should -Be $script:descriptorUri
            $result.Reads[0].StatusCode | Should -Be 200
            $result.Reads[0].Count | Should -Be 3
            @($global:RestoreSmokeApiTest.Calls) | Should -Be @("Get-CmsToken", "Get-DataStore", "Get-SmokeTestCredential", "Wait-CmsClientAvailable", "Get-DmsToken", "GET $script:descriptorUri")
            Should -Invoke Get-CmsToken -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $CmsUrl -eq "http://localhost:18081" -and $ClientId -eq "dms-data-store-admin" -and $ClientSecret -eq "Admin-S3cret!" }
            Should -Invoke Get-DataStore -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $CmsUrl -eq "http://localhost:18081" -and $AccessToken -eq "cms-admin-token" }
            Should -Invoke Get-SmokeTestCredential -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $ConfigServiceUrl -eq "http://localhost:18081" -and @($DataStoreIds).Count -eq 1 -and $DataStoreIds[0] -eq 3 }
            Should -Invoke Wait-CmsClientAvailable -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $CmsUrl -eq "http://localhost:18081" -and $ClientId -eq "app-key" -and $ClientSecret -eq "App-S3cret!" }
            Should -Invoke Get-DmsToken -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $DmsUrl -eq "http://localhost:18080" -and $Key -eq "app-key" -and $Secret -eq "App-S3cret!" }
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $Method -eq "Get" -and $Headers.Authorization -eq "Bearer dms-token-1" }
        }

        It "accepts a one-element descriptor array" {
            $global:RestoreSmokeApiTest.Responses.academicSubjectDescriptors.Content = '[{"codeValue":"Math"}]'

            (Test-RestoreSmokeApiRead @script:readArguments).Reads[0].Count | Should -Be 1
        }

        It "also reads a non-empty schools array for Populated, with the same token" {
            $result = Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData

            $result.Mode | Should -Be "populated"
            @($result.Reads.Resource) | Should -Be @("academicSubjectDescriptors", "schools")
            @($result.Reads.Uri) | Should -Be @($script:descriptorUri, $script:schoolUri)
            @($result.Reads | ForEach-Object { $_.Count }) | Should -Be @(3, 1)
            Should -Invoke Get-DmsToken -ModuleName RestoreSmokeProbes -Times 1 -Exactly
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 2 -Exactly -ParameterFilter { $Headers.Authorization -eq "Bearer dms-token-1" }
        }

        It "fails on HTTP <status> from <resource>" -ForEach @(
            @{ Resource = "academicSubjectDescriptors"; Status = 401; Populated = $false }
            @{ Resource = "academicSubjectDescriptors"; Status = 404; Populated = $false }
            @{ Resource = "academicSubjectDescriptors"; Status = 204; Populated = $false }
            @{ Resource = "schools"; Status = 403; Populated = $true }
            @{ Resource = "schools"; Status = 500; Populated = $true }
        ) {
            $global:RestoreSmokeApiTest.Responses[$Resource] = @{ StatusCode = $Status; Content = '{"detail":"denied"}' }

            { Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData:$Populated } |
                Should -Throw -ExpectedMessage "GET http://localhost:18080/data/ed-fi/$Resource`?limit=5 returned HTTP $Status; the served-data probe requires 200. Body: {`"detail`":`"denied`"}"
        }

        It "fails when <resource> answers 200 with <case>" -ForEach @(
            @{ Resource = "academicSubjectDescriptors"; Case = "a bare object"; Content = '{"codeValue":"Math"}'; Reason = "the body's JSON root is object, not an array" }
            @{ Resource = "academicSubjectDescriptors"; Case = "an object wrapping the array"; Content = '{"items":[{"codeValue":"Math"}]}'; Reason = "the body's JSON root is object, not an array" }
            @{ Resource = "academicSubjectDescriptors"; Case = "JSON null"; Content = 'null'; Reason = "the body's JSON root is null, not an array" }
            @{ Resource = "academicSubjectDescriptors"; Case = "an array of strings"; Content = '["Math"]'; Reason = "array element 0 is string, not a resource object" }
            @{ Resource = "academicSubjectDescriptors"; Case = "text"; Content = 'OK'; Reason = "the body is not a single JSON value" }
            @{ Resource = "academicSubjectDescriptors"; Case = "no body"; Content = ''; Reason = "the body is empty" }
            @{ Resource = "schools"; Case = "a bare object"; Content = '{"schoolId":255901001}'; Reason = "the body's JSON root is object, not an array" }
        ) {
            $global:RestoreSmokeApiTest.Responses[$Resource] = @{ StatusCode = 200; Content = $Content }

            { Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData:($Resource -eq "schools") } |
                Should -Throw -ExpectedMessage "GET http://localhost:18080/data/ed-fi/$Resource`?limit=5 returned HTTP 200, but $Reason; the served-data probe requires a JSON array of $Resource."
        }

        It "fails on an empty descriptor array from a seeded source" {
            $global:RestoreSmokeApiTest.Responses.academicSubjectDescriptors.Content = '[]'

            { Test-RestoreSmokeApiRead @script:readArguments } |
                Should -Throw -ExpectedMessage "GET $script:descriptorUri returned an empty array; the restored academicSubjectDescriptors were not served."
        }

        It "fails on an empty schools array for Populated" {
            $global:RestoreSmokeApiTest.Responses.schools.Content = '[]'

            { Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData } |
                Should -Throw -ExpectedMessage "GET $script:schoolUri returned an empty array; the restored schools were not served."
        }

        It "accepts an empty descriptor array with -SchemaOnly and reports the read as schema-only" {
            $global:RestoreSmokeApiTest.Responses.academicSubjectDescriptors.Content = '[]'

            $result = Test-RestoreSmokeApiRead @script:readArguments -SchemaOnly

            $result.Mode | Should -Be "schema-only"
            $result.Reads.Count | Should -Be 1
            $result.Reads[0].Count | Should -Be 0
            Should -Invoke Get-DmsToken -ModuleName RestoreSmokeProbes -Times 1 -Exactly
        }

        It "still requires HTTP 200 and a JSON array with -SchemaOnly (<case>)" -ForEach @(
            @{ Case = "HTTP 401"; Status = 401; Content = '[]'; Expected = "*returned HTTP 401; the served-data probe requires 200.*" }
            @{ Case = "a bare object"; Status = 200; Content = '{"codeValue":"Math"}'; Expected = "*returned HTTP 200, but the body's JSON root is object, not an array;*" }
            @{ Case = "text"; Status = 200; Content = 'OK'; Expected = "*returned HTTP 200, but the body is not a single JSON value;*" }
        ) {
            $global:RestoreSmokeApiTest.Responses.academicSubjectDescriptors = @{ StatusCode = $Status; Content = $Content }

            { Test-RestoreSmokeApiRead @script:readArguments -SchemaOnly } | Should -Throw -ExpectedMessage $Expected
        }

        It "refuses -SchemaOnly with -RequirePopulatedData before any call" {
            { Test-RestoreSmokeApiRead @script:readArguments -SchemaOnly -RequirePopulatedData } |
                Should -Throw -ExpectedMessage "A Populated served-data read needs a seeded source*"
            $global:RestoreSmokeApiTest.Calls.Count | Should -Be 0
        }

        It "fails before creating any credential when <case>" -ForEach @(
            @{ Case = "two route-unqualified data stores target the database"; Stores = @(@{ Id = 3 }, @{ Id = 9 }); Expected = "2 route-unqualified data stores target the restored database 'edfi_datamanagementservice' (ids 3, 9)*" }
            @{ Case = "no data store is listed"; Stores = @(); Expected = "No route-unqualified data store targets the restored database 'edfi_datamanagementservice' (0 listed)." }
            @{ Case = "the only data store targets another database"; Stores = @(@{ Id = 3; ConnectionString = "host=dms-postgresql;database=edfi_other" }); Expected = "No route-unqualified data store targets*it targets database 'edfi_other'*" }
        ) {
            $global:RestoreSmokeApiTest.DataStores = @($Stores | ForEach-Object { $parameters = $_; New-TestDataStore @parameters })

            { Test-RestoreSmokeApiRead @script:readArguments } | Should -Throw -ExpectedMessage $Expected
            @($global:RestoreSmokeApiTest.Calls) | Should -Be @("Get-CmsToken", "Get-DataStore")
            $script:session.Token | Should -BeNullOrEmpty
        }

        It "fails without any read when <step> fails" -ForEach @(
            @{ Step = "Get-CmsToken"; Expected = "The served-data probe could not list the CMS data stores: 401 for dms-data-store-admin with ***"; TokenRequests = 0 }
            @{ Step = "Get-DataStore"; Expected = "The served-data probe could not list the CMS data stores: 403 for bearer ***"; TokenRequests = 0 }
            @{ Step = "Get-SmokeTestCredential"; Expected = "The served-data probe could not obtain a DMS token for data store 3: Failed to create smoke test credentials: 500 (admin ***)"; TokenRequests = 0 }
            @{ Step = "Wait-CmsClientAvailable"; Expected = "The served-data probe could not obtain a DMS token for data store 3: CMS client 'app-key' (***) did not become available"; TokenRequests = 0 }
            @{ Step = "Get-DmsToken"; Expected = "The served-data probe could not obtain a DMS token for data store 3: 401 for app-key:***"; TokenRequests = 1 }
            @{ Step = "Get-DmsToken-blank"; Expected = "The served-data probe received no DMS token for data store 3."; TokenRequests = 1 }
        ) {
            $global:RestoreSmokeApiTest.Fail = $Step
            $message = $null

            try { Test-RestoreSmokeApiRead @script:readArguments } catch { $message = $_.Exception.Message }

            # Exact comparison: -ExpectedMessage is a wildcard match, where *** would match a leaked secret.
            $message | Should -BeExactly $Expected
            foreach ($secret in @("Admin-S3cret!", "App-S3cret!", "cms-admin-token")) {
                $message | Should -Not -Match ([regex]::Escape($secret))
            }
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 0 -Exactly
            $script:session.Token | Should -BeNullOrEmpty
            $script:session.TokenRequestCount | Should -Be $TokenRequests
        }

        It "keeps no token after a failed acquisition, so the next probe obtains one" {
            $global:RestoreSmokeApiTest.Fail = "Get-DmsToken"
            { Test-RestoreSmokeApiRead @script:readArguments } | Should -Throw
            $global:RestoreSmokeApiTest.Fail = $null

            $result = Test-RestoreSmokeApiRead @script:readArguments

            $result.TokenReused | Should -BeFalse
            $result.TokenRequestCount | Should -Be 2
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $Headers.Authorization -eq "Bearer dms-token-1" }
        }

        It "obtains one DMS token per stack and reuses it for every probe request" {
            $first = Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData
            $second = Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData

            $first.TokenReused | Should -BeFalse
            $second.TokenReused | Should -BeTrue
            $second.TokenRequestCount | Should -Be 1
            $second.DataStoreId | Should -Be 3
            $second.StackGeneration | Should -Be $first.StackGeneration
            Should -Invoke Get-DmsToken -ModuleName RestoreSmokeProbes -Times 1 -Exactly
            Should -Invoke Get-CmsToken -ModuleName RestoreSmokeProbes -Times 1 -Exactly
            Should -Invoke Get-DataStore -ModuleName RestoreSmokeProbes -Times 1 -Exactly
            Should -Invoke Get-SmokeTestCredential -ModuleName RestoreSmokeProbes -Times 1 -Exactly
            Should -Invoke Wait-CmsClientAvailable -ModuleName RestoreSmokeProbes -Times 1 -Exactly
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 4 -Exactly
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 4 -Exactly -ParameterFilter { $Headers.Authorization -eq "Bearer dms-token-1" }
        }

        It "obtains a new token and credential after the session is reset for a recreated stack" {
            $first = Test-RestoreSmokeApiRead @script:readArguments
            Reset-RestoreSmokeApiSession -Session $script:session
            $script:session.Token | Should -BeNullOrEmpty
            $second = Test-RestoreSmokeApiRead @script:readArguments

            $second.TokenReused | Should -BeFalse
            $second.TokenRequestCount | Should -Be 2
            $second.StackGeneration | Should -Be ($first.StackGeneration + 1)
            Should -Invoke Get-DmsToken -ModuleName RestoreSmokeProbes -Times 2 -Exactly
            Should -Invoke Get-SmokeTestCredential -ModuleName RestoreSmokeProbes -Times 2 -Exactly
            Should -Invoke Get-DataStore -ModuleName RestoreSmokeProbes -Times 2 -Exactly
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $Headers.Authorization -eq "Bearer dms-token-1" }
            Should -Invoke Invoke-WebRequest -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $Headers.Authorization -eq "Bearer dms-token-2" }
        }

        It "keeps the token out of a failed read's message" {
            $global:RestoreSmokeApiTest.Responses.academicSubjectDescriptors = @{ StatusCode = 401; Content = '{"error":"invalid_token","token":"dms-token-1"}' }
            $unauthorized = $null
            try { Test-RestoreSmokeApiRead @script:readArguments } catch { $unauthorized = $_.Exception.Message }

            Reset-RestoreSmokeApiSession -Session $script:session
            $global:RestoreSmokeApiTest.Fail = "GET"
            $unreachable = $null
            try { Test-RestoreSmokeApiRead @script:readArguments } catch { $unreachable = $_.Exception.Message }

            $unauthorized | Should -Be "GET $script:descriptorUri returned HTTP 401; the served-data probe requires 200. Body: {`"error`":`"invalid_token`",`"token`":`"***`"}"
            $unreachable | Should -Be "GET $script:descriptorUri failed: connection refused (Bearer ***)"
        }

        It "produces a <mode> record the classifier <outcome>" -ForEach @(
            @{ Mode = "seeded"; Leg = "package-directory"; Switches = @{}; Outcome = "accepts"; Expected = @() }
            @{ Mode = "populated"; Leg = "populated"; Switches = @{ RequirePopulatedData = $true }; Outcome = "accepts"; Expected = @() }
            @{ Mode = "schema-only"; Leg = "package-directory"; Switches = @{ SchemaOnly = $true }; Outcome = "rejects"; Expected = @("restore package-directory#1: the API read was schema-only (unseeded source), which cannot prove seeded served data") }
        ) {
            # The producer-consumer contract: the fields Test-RestoreSmokeApiRead writes are the ones
            # the classifier reads, tagged exactly as Assert-RestoredDatastore tags them.
            $record = Test-RestoreSmokeApiRead @script:readArguments @Switches
            $record | Add-Member -NotePropertyName RestoreExecution -NotePropertyValue (Get-RestoreSmokeRestoreExecutionId -Leg $Leg)

            $classification = Get-RestoreSmokeResultClassification -Provenance ([ordered]@{ Legs = @($Leg); ApiReads = @($record) })

            # Only this record's own restore: the leg's other restores and the SourceIdentity
            # evidence are not supplied here.
            $record.Mode | Should -Be $Mode
            @($classification.Reasons | Where-Object { ($_ -like "restore $($record.RestoreExecution):*" -and $_ -notlike "*SourceIdentity*") -or $_ -like "a served-data*" }) | Should -Be $Expected
        }

        It "keeps tokens and secrets out of the evidence record" {
            $json = Test-RestoreSmokeApiRead @script:readArguments -RequirePopulatedData | ConvertTo-Json -Depth 10

            foreach ($secret in @("dms-token-1", "cms-admin-token", "Admin-S3cret!", "App-S3cret!", "app-key", "Pg-S3cret!", "password")) {
                $json | Should -Not -Match ([regex]::Escape($secret))
            }
        }
    }
}

Describe "Restore executions requiring served-data and SourceIdentity evidence" {
    It "names the restore <leg> performs" -ForEach @(
        @{ Leg = "package-directory" }
        @{ Leg = "separate-config" }
        @{ Leg = "directory-feed" }
        @{ Leg = "populated" }
    ) {
        Get-RestoreSmokeRestoreExecutionId -Leg $Leg | Should -BeExactly "$Leg#1"
    }

    It "names package-directory's second restore, the repeated restore of the same package" {
        Get-RestoreSmokeRestoreExecutionId -Leg "package-directory" -Ordinal 2 | Should -BeExactly "package-directory#2"
    }

    It "has no restore id for <leg>" -ForEach @(
        @{ Leg = "tampered-package"; Ordinal = 1; Expected = "Leg 'tampered-package' performs no successful restore, so it has no restore execution id." }
        @{ Leg = "contaminated-package"; Ordinal = 1; Expected = "Leg 'contaminated-package' performs no successful restore, so it has no restore execution id." }
        @{ Leg = "running-stack"; Ordinal = 1; Expected = "Leg 'running-stack' performs no successful restore, so it has no restore execution id." }
        @{ Leg = "mystery-leg"; Ordinal = 1; Expected = "Leg 'mystery-leg' performs no successful restore, so it has no restore execution id." }
        @{ Leg = "package-directory"; Ordinal = 3; Expected = "Leg 'package-directory' performs 2 successful restore(s); restore 3 is not defined." }
        @{ Leg = "package-directory"; Ordinal = 0; Expected = "Leg 'package-directory' performs 2 successful restore(s); restore 0 is not defined." }
        @{ Leg = "separate-config"; Ordinal = 2; Expected = "Leg 'separate-config' performs 1 successful restore(s); restore 2 is not defined." }
    ) {
        { Get-RestoreSmokeRestoreExecutionId -Leg $Leg -Ordinal $Ordinal } | Should -Throw -ExpectedMessage $Expected
    }

    It "derives one required read per successful restore of the selected legs, none for negative legs" {
        $requirement = Get-RestoreSmokeRequiredApiRead -Legs @("package-directory", "tampered-package", "separate-config", "directory-feed", "contaminated-package", "running-stack", "populated", "package-directory")

        @($requirement.Required | ForEach-Object { $_.RestoreExecution }) | Should -Be @("package-directory#1", "package-directory#2", "separate-config#1", "directory-feed#1", "populated#1")
        @($requirement.Required | ForEach-Object { $_.TemplateKind }) | Should -Be @("Minimal", "Minimal", "Minimal", "Minimal", "Populated")
        @($requirement.UnknownLegs) | Should -BeNullOrEmpty
    }

    It "derives no required read for only negative legs, and reports unknown legs" {
        $negative = Get-RestoreSmokeRequiredApiRead -Legs @("tampered-package", "contaminated-package", "running-stack")
        $unknown = Get-RestoreSmokeRequiredApiRead -Legs @("running-stack", "mystery-leg")

        @($negative.Required) | Should -BeNullOrEmpty
        @($negative.UnknownLegs) | Should -BeNullOrEmpty
        @($unknown.Required) | Should -BeNullOrEmpty
        @($unknown.UnknownLegs) | Should -Be @("mystery-leg")
    }
}

Describe "SourceIdentity evidence (capture, inspection, restored identity)" {
    BeforeAll {
        $script:savedMssqlPassword = $env:MSSQL_SA_PASSWORD
        $env:MSSQL_SA_PASSWORD = "Sm0ke-Secret!"
        $script:sourceIdentity = "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"
        $script:otherIdentity = "7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22"
        $script:decoyIdentity = "11111111-2222-4333-8444-555555555555"

        # A fake engine container. Each docker call is classified by what it does, logged with its
        # step, and answered from $global:RestoreSmokeIdentityTest: Fail = @{ <step> = @{ Exit; Output } },
        # DatabaseExists (the inspection database), IdentityRows, and SelectQueue ("|"-joined rows per
        # SELECT, "" for none). An unrecognized call fails.
        Mock docker -ModuleName RestoreSmokeProbes {
            $state = $global:RestoreSmokeIdentityTest
            $line = $args -join " "
            $step = "unknown"
            if ($args[0] -eq "cp") { $step = "cp" }
            elseif ($line.Contains(" rm -f ")) { $step = "rm" }
            elseif ($line.Contains("RESTORE FILELISTONLY")) { $step = "filelist" }
            elseif ($line.Contains("RESTORE DATABASE")) { $step = "restore" }
            elseif ($line.Contains("ON_ERROR_STOP=1 -f ")) { $step = "replay" }
            elseif ($line.Contains("FROM pg_database WHERE") -or $line.Contains("SELECT CASE WHEN DB_ID")) { $step = "presence" }
            elseif ($line.Contains("CREATE DATABASE")) { $step = "create" }
            elseif ($line.Contains("pg_terminate_backend")) { $step = "terminate" }
            elseif ($line.Contains("DROP DATABASE")) { $step = "drop" }
            elseif ($line.Contains("DataStoreIdentity")) { $step = "select" }
            $state.Calls.Add($line)
            $state.Steps.Add($step)
            if ($state.Fail.ContainsKey($step)) {
                $global:LASTEXITCODE = [int]$state.Fail[$step].Exit
                return $state.Fail[$step].Output
            }
            $global:LASTEXITCODE = 0
            switch ($step) {
                "presence" { if ($state.DatabaseExists) { return "1" } return "0" }
                "create" { $state.DatabaseExists = $true; return "CREATE DATABASE" }
                "restore" { $state.DatabaseExists = $true; return "RESTORE DATABASE successfully processed 1234 pages" }
                "drop" { $state.DatabaseExists = $false; return "DROP DATABASE" }
                "replay" { return @("SET", "CREATE SCHEMA", "INSERT 0 1") }
                "filelist" { return @("EdFi_Ods|/var/opt/mssql/data/EdFi_Ods.mdf|D|PRIMARY|8388608|35184372080640", "EdFi_Ods_log|/var/opt/mssql/data/EdFi_Ods_log.ldf|L|NULL|8388608|2199023255552") }
                "select" {
                    if ($state.SelectQueue.Count -gt 0) {
                        $next = [string]$state.SelectQueue.Dequeue()
                        if ($next -eq "") { return }
                        return $next.Split("|")
                    }
                    return $state.IdentityRows
                }
                "unknown" { $global:LASTEXITCODE = 99; return "unexpected docker call: $line" }
                default { return }
            }
        }

        function script:Reset-IdentityDocker {
            param(
                [object[]]$IdentityRows = @($script:sourceIdentity),

                [hashtable]$Fail = @{},

                [switch]$DatabaseExists,

                [string[]]$SelectQueue = @()
            )

            $queue = [System.Collections.Generic.Queue[string]]::new()
            foreach ($item in $SelectQueue) {
                $queue.Enqueue($item)
            }
            $global:RestoreSmokeIdentityTest = @{
                Calls          = [System.Collections.Generic.List[string]]::new()
                Steps          = [System.Collections.Generic.List[string]]::new()
                IdentityRows   = $IdentityRows
                Fail           = $Fail
                DatabaseExists = [bool]$DatabaseExists
                SelectQueue    = $queue
            }
        }

        # A template package as the producer lays it out: the artifact and restore-manifest.json in
        # the .nupkg, and the sibling attestation document over the .nupkg's SHA-256.
        function script:New-FakeIdentityPackage {
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [ValidateSet("postgresql", "mssql")]
                [string]$Engine = "postgresql",

                [ValidateSet("Minimal", "Populated")]
                [string]$TemplateKind = "Minimal",

                [string]$ArtifactContent = "-- template dump",

                [string]$RecordedArtifactSha,

                [string]$RecordedPackageSha,

                [switch]$WithoutManifest
            )

            New-Item -ItemType Directory -Path $Directory -Force | Out-Null
            $engineToken = if ($Engine -eq "mssql") { "MsSql" } else { "PostgreSql" }
            $extension = if ($Engine -eq "mssql") { "bak" } else { "sql" }
            $contents = Join-Path $Directory "contents-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $contents | Out-Null
            $artifactName = "EdFi.Api.$TemplateKind.Template.$engineToken.$extension"
            $artifactPath = Join-Path $contents $artifactName
            [System.IO.File]::WriteAllText($artifactPath, $ArtifactContent)
            $artifactSha = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $WithoutManifest) {
                [ordered]@{
                    templateKind     = $TemplateKind
                    projects         = @("edfi")
                    artifactFileName = $artifactName
                    artifactSha256   = ($RecordedArtifactSha ? $RecordedArtifactSha : $artifactSha)
                } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $contents "restore-manifest.json")
            }
            $packagePath = Join-Path $Directory "EdFi.Api.$TemplateKind.Template.$engineToken.5.2.0.1.0.999.nupkg"
            Compress-Archive -Path (Join-Path $contents "*") -DestinationPath ($packagePath + ".zip")
            Move-Item -LiteralPath ($packagePath + ".zip") -Destination $packagePath
            Remove-Item -LiteralPath $contents -Recurse -Force

            $packageSha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $payload = @{ packageId = "EdFi.Api.$TemplateKind.Template.$engineToken.5.2.0"; packageVersion = "1.0.999"; packageSha256 = ($RecordedPackageSha ? $RecordedPackageSha : $packageSha); producer = "restore-smoke-1234" } | ConvertTo-Json -Compress
            @{
                version    = 1
                payloadB64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($payload))
                signature  = @{ algorithm = "ECDSA_P256_SHA256"; keyId = ("ab" * 32); valueB64 = "AA==" }
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$packagePath.attestation.json"
            return [pscustomobject]@{ Path = $packagePath; Name = [System.IO.Path]::GetFileName($packagePath); Sha256 = $packageSha; ArtifactName = $artifactName; ArtifactSha256 = $artifactSha }
        }

        function script:Invoke-TestInspection {
            param([string]$Engine, [object]$Package, [string]$Label)

            $work = Join-Path $TestDrive "work-$Label-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            $record = Invoke-RestoreSmokePackageInspection -PackagePath $Package.Path -TemplateKind Minimal -DatabaseEngine $Engine -WorkDirectory $work -RestoreManifestFileName "restore-manifest.json"
            return [pscustomobject]@{ Record = $record; Work = $work }
        }

        function script:Get-ModuleSql {
            # The consumer's SQL builders, called where the probe module imported them.
            param([string]$Function, [string]$Engine)

            return (& (Get-Module RestoreSmokeProbes) { param($name, $engine) & $name -DatabaseEngine $engine } $Function $Engine)
        }
    }

    AfterAll {
        $env:MSSQL_SA_PASSWORD = $script:savedMssqlPassword
        Remove-Variable -Name RestoreSmokeIdentityTest -Scope Global -ErrorAction SilentlyContinue
    }

    Context "Get-RestoreSmokeSourceIdentityValue" {
        It "accepts exactly one canonical UUID, ignoring blank rows, and lower-cases it" {
            $value = Get-RestoreSmokeSourceIdentityValue -Row @("", "  6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11  ", $null, "   ")

            $value.Identity | Should -BeExactly $script:sourceIdentity
            $value.Defect | Should -BeNullOrEmpty
        }

        It "rejects <case>" -ForEach @(
            @{ Case = "no row"; Rows = @(); Expected = "has 0 rows; expected exactly one" }
            @{ Case = "null"; Rows = $null; Expected = "has 0 rows; expected exactly one" }
            @{ Case = "only blank rows"; Rows = @("", "   "); Expected = "has 0 rows; expected exactly one" }
            @{ Case = "two different rows"; Rows = @("6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11", "7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22"); Expected = "has 2 rows; expected exactly one" }
            @{ Case = "the same row twice"; Rows = @("6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11", "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"); Expected = "has 2 rows; expected exactly one" }
            @{ Case = "a non-UUID"; Rows = @("not-a-uuid"); Expected = "is not a UUID ('not-a-uuid')" }
            @{ Case = "a braced UUID"; Rows = @("{6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11}"); Expected = "is not a UUID ('{6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11}')" }
            @{ Case = "a UUID without hyphens"; Rows = @("6f1c1c330d4e4d0f9b9e4f5b8a3d2c11"); Expected = "is not a UUID ('6f1c1c330d4e4d0f9b9e4f5b8a3d2c11')" }
            @{ Case = "the zero UUID"; Rows = @("00000000-0000-0000-0000-000000000000"); Expected = "is the zero UUID" }
        ) {
            $value = Get-RestoreSmokeSourceIdentityValue -Row $Rows

            $value.Identity | Should -BeNullOrEmpty
            $value.Defect | Should -BeExactly $Expected
        }
    }

    Context "Get-RestoreSmokeSourceIdentityRead" {
        It "runs the consumer's SELECT, and nothing else, through the <engine> transport" -ForEach @(
            @{ Engine = "postgresql"; Prefix = "exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -tA -c " }
            @{ Engine = "mssql"; Prefix = "exec -e SQLCMDPASSWORD=Sm0ke-Secret! dms-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d edfi_datamanagementservice -C -b -h -1 -W -Q " }
        ) {
            Reset-IdentityDocker -IdentityRows @("6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11")

            $read = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine $Engine -DatabaseName "edfi_datamanagementservice"

            $read.Identity | Should -BeExactly $script:sourceIdentity
            $read.Reason | Should -BeNullOrEmpty
            $read.ExitCode | Should -Be 0
            @($read.Rows) | Should -Be @("6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11")
            @($global:RestoreSmokeIdentityTest.Calls) | Should -Be @($Prefix + (Get-ModuleSql -Function Get-SourceIdentitySelectSql -Engine $Engine))
        }

        It "records a failed query as a reason, with the sa password redacted (<engine>)" -ForEach @(
            @{ Engine = "postgresql" }
            @{ Engine = "mssql" }
        ) {
            Reset-IdentityDocker -Fail @{ select = @{ Exit = 1; Output = "Login failed for user 'sa' (password Sm0ke-Secret!)" } }

            $read = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine $Engine -DatabaseName "edfi_datamanagementservice"

            $read.ExitCode | Should -Be 1
            $read.Identity | Should -BeNullOrEmpty
            @($read.Rows).Count | Should -Be 0
            $read.Reason | Should -BeExactly "the SourceIdentity query against 'edfi_datamanagementservice' exited 1: Login failed for user 'sa' (password ***)"
        }

        It "keeps both rows of a two-row answer and gives the cardinality as the reason" {
            Reset-IdentityDocker -IdentityRows @($script:sourceIdentity, $script:otherIdentity)

            $read = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice"

            @($read.Rows) | Should -Be @($script:sourceIdentity, $script:otherIdentity)
            $read.Identity | Should -BeNullOrEmpty
            $read.Reason | Should -BeExactly "dms.DataStoreIdentity.SourceIdentity in 'edfi_datamanagementservice' has 2 rows; expected exactly one"
        }
    }

    Context "Invoke-RestoreSmokeIdentityBoundPackageBuild" {
        BeforeEach {
            $script:directory = Join-Path $TestDrive "bound-$([Guid]::NewGuid().ToString('N'))"
            $script:bindings = [System.Collections.Generic.List[object]]::new()
            $script:packages = [System.Collections.Generic.List[object]]::new()
            $script:buildArguments = @{
                PackageFixture          = "default-minimal"
                DatabaseEngine          = "postgresql"
                SourceDatabaseName      = "edfi_datamanagementservice"
                PackageDirectory        = $script:directory
                RestoreManifestFileName = "restore-manifest.json"
                BindingList             = $script:bindings
                PackageList             = $script:packages
                BuildPackage            = {
                    $global:RestoreSmokeIdentityTest.Steps.Add("build")
                    $null = New-FakeIdentityPackage -Directory $script:directory
                }
            }
        }

        It "reads the source identity, builds, reads it again, and binds both to the exact .nupkg's SHA-256" {
            Reset-IdentityDocker -SelectQueue @("6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11", $script:sourceIdentity)

            $result = Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments

            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be @("select", "build", "select")
            foreach ($call in $global:RestoreSmokeIdentityTest.Calls) {
                $call | Should -BeLike "exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -tA -c *"
            }
            $packageFile = @(Get-ChildItem -LiteralPath $script:directory -Filter "*.nupkg")
            $packageFile.Count | Should -Be 1
            $fileSha = (Get-FileHash -LiteralPath $packageFile[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()

            $script:bindings.Count | Should -Be 1
            $binding = $script:bindings[0]
            $binding.Bound | Should -BeTrue
            $binding.Reason | Should -BeNullOrEmpty
            $binding.TemplateKind | Should -Be "Minimal"
            $binding.SourceDatabase | Should -Be "edfi_datamanagementservice"
            $binding.PackageSha256 | Should -BeExactly $fileSha
            $binding.PackageFile | Should -Be $packageFile[0].Name
            $binding.BeforeBackup.Identity | Should -BeExactly $script:sourceIdentity
            $binding.AfterBackup.Identity | Should -BeExactly $script:sourceIdentity
            $script:packages.Count | Should -Be 1
            $script:packages[0].Sha256 | Should -BeExactly $fileSha
            $script:packages[0].Verified | Should -BeTrue
            $result.Binding | Should -Be $binding
        }

        It "builds nothing when the source identity before the backup <case>" -ForEach @(
            @{ Case = "has no row"; Queue = @(""); Fail = @{}; Expected = "dms.DataStoreIdentity.SourceIdentity in 'edfi_datamanagementservice' has 0 rows; expected exactly one" }
            @{ Case = "has two rows"; Queue = @("6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11|7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22"); Fail = @{}; Expected = "dms.DataStoreIdentity.SourceIdentity in 'edfi_datamanagementservice' has 2 rows; expected exactly one" }
            @{ Case = "is not a UUID"; Queue = @("pending"); Fail = @{}; Expected = "dms.DataStoreIdentity.SourceIdentity in 'edfi_datamanagementservice' is not a UUID ('pending')" }
            @{ Case = "is the zero UUID"; Queue = @("00000000-0000-0000-0000-000000000000"); Fail = @{}; Expected = "dms.DataStoreIdentity.SourceIdentity in 'edfi_datamanagementservice' is the zero UUID" }
            @{ Case = "cannot be read"; Queue = @(); Fail = @{ select = @{ Exit = 2; Output = "connection refused" } }; Expected = "the SourceIdentity query against 'edfi_datamanagementservice' exited 2: connection refused" }
        ) {
            Reset-IdentityDocker -SelectQueue $Queue -Fail $Fail

            { Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments } | Should -Throw -ExpectedMessage "Cannot bind the default-minimal package to its source's SourceIdentity: before the backup, $Expected. Nothing was built."

            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be @("select")
            Test-Path -LiteralPath $script:directory | Should -BeFalse
            $script:packages.Count | Should -Be 0
            $script:bindings.Count | Should -Be 1
            $script:bindings[0].Bound | Should -BeFalse
            $script:bindings[0].PackageSha256 | Should -BeNullOrEmpty
            $script:bindings[0].Reason | Should -BeExactly "before the backup, $Expected"
        }

        It "does not bind when the source identity changed across the build" {
            Reset-IdentityDocker -SelectQueue @($script:sourceIdentity, $script:otherIdentity)

            { Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments } | Should -Throw -ExpectedMessage "*changed across the backup (before $($script:sourceIdentity), after $($script:otherIdentity))*"

            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be @("select", "build", "select")
            $binding = $script:bindings[0]
            $binding.Bound | Should -BeFalse
            $binding.PackageSha256 | Should -BeExactly $script:packages[0].Sha256
            $binding.Reason | Should -BeExactly "the source's SourceIdentity changed across the backup (before $($script:sourceIdentity), after $($script:otherIdentity))"
        }

        It "does not bind when the identity cannot be read after the build" {
            Reset-IdentityDocker -SelectQueue @($script:sourceIdentity, "")

            { Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments } | Should -Throw -ExpectedMessage "*after the backup, dms.DataStoreIdentity.SourceIdentity in 'edfi_datamanagementservice' has 0 rows; expected exactly one*"

            $script:bindings[0].Bound | Should -BeFalse
        }

        It "does not bind a package whose provenance cannot be established" {
            Reset-IdentityDocker -SelectQueue @($script:sourceIdentity, $script:sourceIdentity)
            $script:buildArguments.BuildPackage = {
                $global:RestoreSmokeIdentityTest.Steps.Add("build")
                $null = New-FakeIdentityPackage -Directory $script:directory -RecordedPackageSha ("0" * 64)
            }

            { Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments } | Should -Throw -ExpectedMessage "Package provenance could not be established: *"

            $script:packages[0].Verified | Should -BeFalse
            $script:bindings[0].Bound | Should -BeFalse
            $script:bindings[0].Reason | Should -BeLike "the package provenance could not be established: *"
        }

        It "keeps a separate binding for each template kind, so a later build never overwrites an earlier one" {
            Reset-IdentityDocker -SelectQueue @($script:sourceIdentity, $script:sourceIdentity, $script:otherIdentity, $script:otherIdentity)
            $minimalDirectory = Join-Path $TestDrive "bound-minimal"
            $populatedDirectory = Join-Path $TestDrive "bound-populated"

            $minimal = Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments -PackageDirectory $minimalDirectory -BuildPackage { $null = New-FakeIdentityPackage -Directory $minimalDirectory }
            $populated = Invoke-RestoreSmokeIdentityBoundPackageBuild @script:buildArguments -PackageFixture default-populated -PackageDirectory $populatedDirectory -BuildPackage { $null = New-FakeIdentityPackage -Directory $populatedDirectory -TemplateKind Populated -ArtifactContent "-- populated dump" }

            $script:bindings.Count | Should -Be 2
            @($script:bindings.TemplateKind) | Should -Be @("Minimal", "Populated")
            $script:bindings[0].PackageSha256 | Should -BeExactly $minimal.Package.Sha256
            $script:bindings[0].BeforeBackup.Identity | Should -BeExactly $script:sourceIdentity
            $script:bindings[1].PackageSha256 | Should -BeExactly $populated.Package.Sha256
            $script:bindings[1].BeforeBackup.Identity | Should -BeExactly $script:otherIdentity
            $script:bindings[0].PackageSha256 | Should -Not -Be $script:bindings[1].PackageSha256
            @($script:bindings.Bound) | Should -Be @($true, $true)
        }
    }

    Context "Invoke-RestoreSmokePackageInspection" {
        It "restores the exact package into a run-owned database, selects its identity, and removes everything it created (<engine>)" -ForEach @(
            @{ Engine = "postgresql"; Container = "dms-postgresql"; Steps = @("presence", "create", "cp", "replay", "select", "terminate", "drop", "presence", "rm"); ContainerFile = '^/tmp/restore-smoke-inspect-[0-9a-f]{12}\.sql$' }
            @{ Engine = "mssql"; Container = "dms-mssql"; Steps = @("presence", "cp", "filelist", "restore", "select", "drop", "presence", "rm"); ContainerFile = '^/var/opt/mssql/data/restore-smoke-inspect-[0-9a-f]{12}\.bak$' }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-ok-$Engine") -Engine $Engine
            Reset-IdentityDocker -IdentityRows @("6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11")

            $run = Invoke-TestInspection -Engine $Engine -Package $package -Label "ok-$Engine"
            $record = $run.Record

            $record.Reason | Should -BeNullOrEmpty
            $record.Identity | Should -BeExactly $script:sourceIdentity
            @($record.Rows) | Should -Be @("6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11")
            $record.PackageSha256 | Should -BeExactly $package.Sha256
            $record.PackageFile | Should -Be $package.Name
            $record.ArtifactFileName | Should -Be $package.ArtifactName
            $record.ArtifactSha256 | Should -BeExactly $package.ArtifactSha256
            $record.InspectionDatabase | Should -Match '^restore_smoke_inspect_[0-9a-f]{12}$'
            $record.Cleanup.DatabaseOwned | Should -BeTrue
            $record.Cleanup.DatabaseDropped | Should -BeTrue
            $record.Cleanup.DatabaseAbsent | Should -BeTrue
            $record.Cleanup.ContainerFile | Should -Match $ContainerFile
            $record.Cleanup.ContainerFileRemoved | Should -BeTrue
            $record.Cleanup.LocalDirectoryRemoved | Should -BeTrue
            $record.Cleanup.Complete | Should -BeTrue
            @(Get-ChildItem -LiteralPath $run.Work -Force) | Should -BeNullOrEmpty
            $global:RestoreSmokeIdentityTest.DatabaseExists | Should -BeFalse

            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be $Steps
            $database = $record.InspectionDatabase
            $calls = @($global:RestoreSmokeIdentityTest.Calls)
            foreach ($call in $calls) {
                $call | Should -BeLike "*$Container*"
            }
            $copy = @($calls | Where-Object { $_ -like "cp *" })
            $copy.Count | Should -Be 1
            $copy[0] | Should -BeExactly ("cp " + (Join-Path $run.Work ("inspect-" + $database.Substring("restore_smoke_inspect_".Length)) | Join-Path -ChildPath $package.ArtifactName) + " ${Container}:$($record.Cleanup.ContainerFile)")
            $select = @($calls | Where-Object { $_.Contains("DataStoreIdentity") })
            $select.Count | Should -Be 1
            $select[0] | Should -BeLike "* -d $database *"
            $select[0].EndsWith((Get-ModuleSql -Function Get-SourceIdentitySelectSql -Engine $Engine)) | Should -BeTrue
            if ($Engine -eq "mssql") {
                $restore = @($calls | Where-Object { $_.Contains("RESTORE DATABASE") })[0]
                $restore | Should -BeLike "*RESTORE DATABASE [[]$database] FROM DISK = N'$($record.Cleanup.ContainerFile)' WITH MOVE N'EdFi_Ods' TO N'/var/opt/mssql/data/$database.mdf', MOVE N'EdFi_Ods_log' TO N'/var/opt/mssql/data/${database}_log.ldf';"
                $restore | Should -Not -BeLike "*REPLACE*"
            }
            else {
                @($calls | Where-Object { $_.Contains("CREATE DATABASE") }) | Should -Be @("exec dms-postgresql psql -U postgres -d postgres -tA -c CREATE DATABASE `"$database`";")
                @($calls | Where-Object { $_.Contains("ON_ERROR_STOP") }) | Should -Be @("exec dms-postgresql psql -U postgres -d $database -v ON_ERROR_STOP=1 -f $($record.Cleanup.ContainerFile)")
            }
        }

        It "never issues the reseed and never takes the identity from the artifact's text (<engine>)" -ForEach @(
            @{ Engine = "postgresql" }
            @{ Engine = "mssql" }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-decoy-$Engine") -Engine $Engine -ArtifactContent "INSERT INTO dms.`"DataStoreIdentity`" VALUES (1, '$($script:decoyIdentity)');"
            Reset-IdentityDocker -IdentityRows @($script:sourceIdentity)

            $record = (Invoke-TestInspection -Engine $Engine -Package $package -Label "decoy-$Engine").Record

            $record.Identity | Should -BeExactly $script:sourceIdentity
            $reseedSql = Get-ModuleSql -Function Get-SourceIdentityReseedSql -Engine $Engine
            foreach ($call in $global:RestoreSmokeIdentityTest.Calls) {
                $call.Contains($reseedSql) | Should -BeFalse
                $call | Should -Not -Match '(?i)\bUPDATE\b|NEWID|gen_random_uuid'
                $call.Contains($script:decoyIdentity) | Should -BeFalse
            }
        }

        It "reports a package identity that <case>, and still drops its database (<engine>)" -ForEach @(
            @{ Engine = "postgresql"; Case = "has no row"; Rows = @(); Expected = "has 0 rows; expected exactly one" }
            @{ Engine = "mssql"; Case = "has no row"; Rows = @(); Expected = "has 0 rows; expected exactly one" }
            @{ Engine = "postgresql"; Case = "has two rows"; Rows = @("6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11", "7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22"); Expected = "has 2 rows; expected exactly one" }
            @{ Engine = "mssql"; Case = "has two rows"; Rows = @("6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11", "7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22"); Expected = "has 2 rows; expected exactly one" }
            @{ Engine = "postgresql"; Case = "is not a UUID"; Rows = @("NULL"); Expected = "is not a UUID ('NULL')" }
            @{ Engine = "mssql"; Case = "is not a UUID"; Rows = @("NULL"); Expected = "is not a UUID ('NULL')" }
            @{ Engine = "postgresql"; Case = "is the zero UUID"; Rows = @("00000000-0000-0000-0000-000000000000"); Expected = "is the zero UUID" }
            @{ Engine = "mssql"; Case = "is the zero UUID"; Rows = @("00000000-0000-0000-0000-000000000000"); Expected = "is the zero UUID" }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-rows-$Engine-$([Guid]::NewGuid().ToString('N'))") -Engine $Engine
            Reset-IdentityDocker -IdentityRows $Rows

            $record = (Invoke-TestInspection -Engine $Engine -Package $package -Label "rows").Record

            $record.Identity | Should -BeNullOrEmpty
            $record.Reason | Should -BeExactly "the package's dms.DataStoreIdentity.SourceIdentity in '$($record.InspectionDatabase)' $Expected"
            $record.Cleanup.DatabaseDropped | Should -BeTrue
            $record.Cleanup.DatabaseAbsent | Should -BeTrue
            $record.Cleanup.Complete | Should -BeTrue
        }

        It "cleans up what it created when the <failstep> step fails (<engine>)" -ForEach @(
            @{ Engine = "postgresql"; FailStep = "create"; Steps = @("presence", "create", "terminate", "drop", "presence"); Reason = "CREATE DATABASE {db} exited 1: boom"; FileExpected = $false }
            @{ Engine = "postgresql"; FailStep = "cp"; Steps = @("presence", "create", "cp", "terminate", "drop", "presence", "rm"); Reason = "docker cp of the artifact exited 1: boom"; FileExpected = $true }
            @{ Engine = "postgresql"; FailStep = "replay"; Steps = @("presence", "create", "cp", "replay", "terminate", "drop", "presence", "rm"); Reason = "the replay into {db} exited 1: boom"; FileExpected = $true }
            @{ Engine = "postgresql"; FailStep = "select"; Steps = @("presence", "create", "cp", "replay", "select", "terminate", "drop", "presence", "rm"); Reason = "the SourceIdentity query against '{db}' exited 1: boom"; FileExpected = $true }
            @{ Engine = "mssql"; FailStep = "cp"; Steps = @("presence", "cp", "drop", "presence", "rm"); Reason = "docker cp of the artifact exited 1: boom"; FileExpected = $true }
            @{ Engine = "mssql"; FailStep = "filelist"; Steps = @("presence", "cp", "filelist", "drop", "presence", "rm"); Reason = "RESTORE FILELISTONLY exited 1: boom"; FileExpected = $true }
            @{ Engine = "mssql"; FailStep = "restore"; Steps = @("presence", "cp", "filelist", "restore", "drop", "presence", "rm"); Reason = "RESTORE DATABASE {db} exited 1: boom"; FileExpected = $true }
            @{ Engine = "mssql"; FailStep = "select"; Steps = @("presence", "cp", "filelist", "restore", "select", "drop", "presence", "rm"); Reason = "the SourceIdentity query against '{db}' exited 1: boom"; FileExpected = $true }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-fail-$Engine-$FailStep") -Engine $Engine
            Reset-IdentityDocker -Fail @{ $FailStep = @{ Exit = 1; Output = "boom" } }

            $run = Invoke-TestInspection -Engine $Engine -Package $package -Label "fail-$FailStep"
            $record = $run.Record

            $record.Reason | Should -BeExactly $Reason.Replace("{db}", $record.InspectionDatabase)
            $record.Identity | Should -BeNullOrEmpty
            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be $Steps
            $record.Cleanup.DatabaseOwned | Should -BeTrue
            $record.Cleanup.DatabaseDropped | Should -BeTrue
            $record.Cleanup.DatabaseAbsent | Should -BeTrue
            if ($FileExpected) {
                $record.Cleanup.ContainerFileRemoved | Should -BeTrue
            }
            else {
                $record.Cleanup.ContainerFile | Should -BeNullOrEmpty
                $record.Cleanup.ContainerFileRemoved | Should -BeNullOrEmpty
            }
            $record.Cleanup.LocalDirectoryRemoved | Should -BeTrue
            $record.Cleanup.Complete | Should -BeTrue
            @(Get-ChildItem -LiteralPath $run.Work -Force) | Should -BeNullOrEmpty
        }

        It "records cleanup it could not complete: the <failstep> step fails (<engine>)" -ForEach @(
            @{ Engine = "postgresql"; FailStep = "drop"; Dropped = $false; Absent = $false; FileRemoved = $true; Reasons = @("dropping {db} exited 1: ERROR: database is being accessed by other users", "{db} was not shown absent after the drop") }
            @{ Engine = "mssql"; FailStep = "drop"; Dropped = $false; Absent = $false; FileRemoved = $true; Reasons = @("dropping {db} exited 1: ERROR: database is being accessed by other users", "{db} was not shown absent after the drop") }
            @{ Engine = "postgresql"; FailStep = "rm"; Dropped = $true; Absent = $true; FileRemoved = $false; Reasons = @("removing {file} from dms-postgresql exited 1: ERROR: database is being accessed by other users") }
            @{ Engine = "mssql"; FailStep = "rm"; Dropped = $true; Absent = $true; FileRemoved = $false; Reasons = @("removing {file} from dms-mssql exited 1: ERROR: database is being accessed by other users") }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-cleanup-$Engine-$FailStep") -Engine $Engine
            Reset-IdentityDocker -Fail @{ $FailStep = @{ Exit = 1; Output = "ERROR: database is being accessed by other users" } }

            $record = (Invoke-TestInspection -Engine $Engine -Package $package -Label "cleanup-$FailStep").Record

            $record.Identity | Should -BeExactly $script:sourceIdentity
            $record.Cleanup.DatabaseDropped | Should -Be $Dropped
            $record.Cleanup.DatabaseAbsent | Should -Be $Absent
            $record.Cleanup.ContainerFileRemoved | Should -Be $FileRemoved
            $record.Cleanup.Complete | Should -BeFalse
            @($record.Cleanup.Reasons) | Should -Be @($Reasons | ForEach-Object { $_.Replace("{db}", $record.InspectionDatabase).Replace("{file}", [string]$record.Cleanup.ContainerFile) })
        }

        It "touches no database when the generated name already exists, and leaves that database in place (<engine>)" -ForEach @(
            @{ Engine = "postgresql" }
            @{ Engine = "mssql" }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-exists-$Engine") -Engine $Engine
            Reset-IdentityDocker -DatabaseExists

            $record = (Invoke-TestInspection -Engine $Engine -Package $package -Label "exists").Record

            $record.Reason | Should -BeExactly "'$($record.InspectionDatabase)' already exists; it is not this inspection's and is left in place"
            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be @("presence")
            $global:RestoreSmokeIdentityTest.DatabaseExists | Should -BeTrue
            $record.Cleanup.DatabaseOwned | Should -BeFalse
            $record.Cleanup.DatabaseDropped | Should -BeNullOrEmpty
            $record.Cleanup.ContainerFile | Should -BeNullOrEmpty
            $record.Cleanup.Complete | Should -BeTrue
        }

        It "touches no database when the name's absence cannot be established (<engine>)" -ForEach @(
            @{ Engine = "postgresql" }
            @{ Engine = "mssql" }
        ) {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-presence-$Engine") -Engine $Engine
            Reset-IdentityDocker -Fail @{ presence = @{ Exit = 1; Output = "server is starting up" } }

            $record = (Invoke-TestInspection -Engine $Engine -Package $package -Label "presence").Record

            $record.Reason | Should -BeExactly "could not establish that '$($record.InspectionDatabase)' is absent (exit 1: server is starting up)"
            @($global:RestoreSmokeIdentityTest.Steps) | Should -Be @("presence")
            $record.Cleanup.DatabaseOwned | Should -BeFalse
        }

        It "makes no Docker call when the package <case> (<engine>)" -ForEach @(
            @{ Engine = "postgresql"; Case = "artifact does not match its manifest's artifactSha256"; Manifest = "mismatch"; Expected = "the artifact hashes to {artifact}, but the restore manifest records $("0" * 64)" }
            @{ Engine = "mssql"; Case = "artifact does not match its manifest's artifactSha256"; Manifest = "mismatch"; Expected = "the artifact hashes to {artifact}, but the restore manifest records $("0" * 64)" }
            @{ Engine = "postgresql"; Case = "has no restore manifest"; Manifest = "missing"; Expected = "expected one 'restore-manifest.json' in the package, found 0" }
        ) {
            $packageArguments = @{ Directory = (Join-Path $TestDrive "inspect-manifest-$Engine-$Manifest"); Engine = $Engine }
            if ($Manifest -eq "mismatch") { $packageArguments.RecordedArtifactSha = "0" * 64 }
            if ($Manifest -eq "missing") { $packageArguments.WithoutManifest = $true }
            $package = New-FakeIdentityPackage @packageArguments
            Reset-IdentityDocker

            $run = Invoke-TestInspection -Engine $Engine -Package $package -Label "manifest-$Manifest"

            $run.Record.Reason | Should -BeExactly $Expected.Replace("{artifact}", $package.ArtifactSha256)
            @($global:RestoreSmokeIdentityTest.Calls) | Should -BeNullOrEmpty
            $run.Record.Cleanup.DatabaseOwned | Should -BeFalse
            $run.Record.Cleanup.Complete | Should -BeTrue
            @(Get-ChildItem -LiteralPath $run.Work -Force) | Should -BeNullOrEmpty
        }

        It "keeps the sa password out of the record" {
            $package = New-FakeIdentityPackage -Directory (Join-Path $TestDrive "inspect-secret") -Engine mssql
            Reset-IdentityDocker -Fail @{ restore = @{ Exit = 1; Output = "Msg 18456: Login failed for user 'sa' with password Sm0ke-Secret!" } }

            $record = (Invoke-TestInspection -Engine mssql -Package $package -Label "secret").Record

            $record.Reason | Should -BeExactly "RESTORE DATABASE $($record.InspectionDatabase) exited 1: Msg 18456: Login failed for user 'sa' with password ***"
            ($record | ConvertTo-Json -Depth 6) | Should -Not -Match "Sm0ke-Secret"
        }
    }

    Context "Get-RestoreSmokeRestoredIdentityDefect" {
        BeforeAll {
            $script:earlierRestores = @(
                [pscustomobject]@{ RestoreExecution = "package-directory#1"; Rows = @("d1000000-0000-4000-8000-000000000001") }
                [pscustomobject]@{ RestoreExecution = "package-directory#2"; Rows = @("D1000000-0000-4000-8000-000000000002") }
            )
        }

        It "finds no defect in a new identity" {
            @(Get-RestoreSmokeRestoredIdentityDefect -Row @("d1000000-0000-4000-8000-000000000003") -PackageIdentity $script:sourceIdentity -EarlierRestore $script:earlierRestores) | Should -BeNullOrEmpty
        }

        It "reports <case>" -ForEach @(
            @{ Case = "no row"; Rows = @(); Expected = @("the restored SourceIdentity has 0 rows; expected exactly one") }
            @{ Case = "two rows"; Rows = @("d1000000-0000-4000-8000-000000000003", "d1000000-0000-4000-8000-000000000004"); Expected = @("the restored SourceIdentity has 2 rows; expected exactly one") }
            @{ Case = "a non-UUID"; Rows = @("x"); Expected = @("the restored SourceIdentity is not a UUID ('x')") }
            @{ Case = "the zero UUID"; Rows = @("00000000-0000-0000-0000-000000000000"); Expected = @("the restored SourceIdentity is the zero UUID") }
            @{ Case = "the package's identity, in another case"; Rows = @("6F1C1C33-0D4E-4D0F-9B9E-4F5B8A3D2C11"); Expected = @("the restored SourceIdentity 6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11 equals the package's inspected SourceIdentity") }
            @{ Case = "an earlier restore's identity, in another case"; Rows = @("d1000000-0000-4000-8000-000000000002"); Expected = @("the restored SourceIdentity d1000000-0000-4000-8000-000000000002 equals restore package-directory#2's") }
        ) {
            @(Get-RestoreSmokeRestoredIdentityDefect -Row $Rows -PackageIdentity $script:sourceIdentity -EarlierRestore $script:earlierRestores) | Should -Be $Expected
        }

        It "reports both equalities when the identity is the package's and an earlier restore's" {
            $earlier = @([pscustomobject]@{ RestoreExecution = "package-directory#1"; Rows = @($script:sourceIdentity) })

            @(Get-RestoreSmokeRestoredIdentityDefect -Row @($script:sourceIdentity) -PackageIdentity $script:sourceIdentity -EarlierRestore $earlier) | Should -Be @(
                "the restored SourceIdentity $($script:sourceIdentity) equals the package's inspected SourceIdentity"
                "the restored SourceIdentity $($script:sourceIdentity) equals restore package-directory#1's"
            )
        }

        It "skips only the package comparison when the package identity is unknown" {
            @(Get-RestoreSmokeRestoredIdentityDefect -Row @($script:sourceIdentity) -PackageIdentity "" -EarlierRestore @()) | Should -BeNullOrEmpty
            @(Get-RestoreSmokeRestoredIdentityDefect -Row @("d1000000-0000-4000-8000-000000000001") -PackageIdentity $null -EarlierRestore $script:earlierRestores) | Should -Be @("the restored SourceIdentity d1000000-0000-4000-8000-000000000001 equals restore package-directory#1's")
        }
    }

    Context "producer-consumer contract" {
        It "classifies the records the capture, the inspection, and two target reads produce: <case>" -ForEach @(
            @{ Case = "two new identities"; SecondRead = "d1000000-0000-4000-8000-000000000002"; Expected = @() }
            @{ Case = "the repeated restore kept the first identity"; SecondRead = "d1000000-0000-4000-8000-000000000001"; Expected = @("restore package-directory#2: the restored SourceIdentity d1000000-0000-4000-8000-000000000001 equals restore package-directory#1's") }
            @{ Case = "the repeated restore kept the package identity"; SecondRead = "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"; Expected = @("restore package-directory#2: the restored SourceIdentity 6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11 equals the package's inspected SourceIdentity") }
        ) {
            # The smoke's order: capture before and after the build, the first restore's target
            # read, the inspection (on the first restore), the second restore's target read.
            Reset-IdentityDocker -SelectQueue @($script:sourceIdentity, $script:sourceIdentity, "d1000000-0000-4000-8000-000000000001", $script:sourceIdentity, $SecondRead)
            $directory = Join-Path $TestDrive "contract-$([Guid]::NewGuid().ToString('N'))"
            $bindings = [System.Collections.Generic.List[object]]::new()
            $packages = [System.Collections.Generic.List[object]]::new()

            $bound = Invoke-RestoreSmokeIdentityBoundPackageBuild -PackageFixture default-minimal -DatabaseEngine postgresql -SourceDatabaseName "edfi_datamanagementservice" -PackageDirectory $directory -RestoreManifestFileName "restore-manifest.json" -BindingList $bindings -PackageList $packages -BuildPackage { $null = New-FakeIdentityPackage -Directory $directory }
            $packagePath = Join-Path $directory $bound.Package.PackageFile
            $restored = [System.Collections.Generic.List[object]]::new()
            $first = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice"
            $restored.Add([pscustomobject]@{ RestoreExecution = "package-directory#1"; PackageFixture = "default-minimal"; TemplateKind = "Minimal"; PackageSha256 = (Get-RestoreSmokeFileSha256 -Path $packagePath); Rows = $first.Rows; Identity = $first.Identity; Reason = $first.Reason })
            $work = Join-Path $TestDrive "contract-work-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $work | Out-Null
            $inspection = Invoke-RestoreSmokePackageInspection -PackagePath $packagePath -TemplateKind Minimal -DatabaseEngine postgresql -WorkDirectory $work -RestoreManifestFileName "restore-manifest.json"
            $second = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice"
            $restored.Add([pscustomobject]@{ RestoreExecution = "package-directory#2"; PackageFixture = "default-minimal"; TemplateKind = "Minimal"; PackageSha256 = (Get-RestoreSmokeFileSha256 -Path $packagePath); Rows = $second.Rows; Identity = $second.Identity; Reason = $second.Reason })

            $classification = Get-RestoreSmokeResultClassification -Provenance ([ordered]@{
                    Legs                   = @("package-directory")
                    Packages               = $packages
                    SourceIdentityBindings = $bindings
                    PackageInspections     = @($inspection)
                    RestoredIdentities     = $restored
                })

            $inspection.Identity | Should -BeExactly $script:sourceIdentity
            @($classification.Reasons | Where-Object { $_ -like "*SourceIdentity*" -or $_ -like "*inspect*" -or $_ -like "default-minimal package *" }) | Should -Be $Expected
        }
    }
}

Describe "Package fixtures and leg selection" {
    It "describes the <name> fixture" -ForEach @(
        @{ Name = "default-minimal"; Kind = "Minimal"; Selection = "default"; Projects = $null; Directory = "package-default-minimal" }
        @{ Name = "core-only-minimal"; Kind = "Minimal"; Selection = "core-only"; Projects = @("edfi"); Directory = "package-core-only-minimal" }
        @{ Name = "default-populated"; Kind = "Populated"; Selection = "default"; Projects = $null; Directory = "package-default-populated" }
    ) {
        $fixture = Get-RestoreSmokePackageFixture -Name $Name

        $fixture.Name | Should -BeExactly $Name
        $fixture.TemplateKind | Should -BeExactly $Kind
        $fixture.Selection | Should -BeExactly $Selection
        $fixture.DirectoryName | Should -BeExactly $Directory
        if ($null -eq $Projects) {
            $fixture.ProjectSchemas | Should -BeNullOrEmpty
        }
        else {
            @($fixture.ProjectSchemas) | Should -Be $Projects
        }
    }

    It "refuses an unknown fixture" {
        { Get-RestoreSmokePackageFixture -Name "minimal" } | Should -Throw -ExpectedMessage "Unknown package fixture 'minimal'. Known fixtures: default-minimal, core-only-minimal, default-populated."
    }

    It "derives the fixtures <case>" -ForEach @(
        @{ Case = "a package-directory run needs"; Legs = @("package-directory"); Expected = @("default-minimal") }
        @{ Case = "a negative-only run needs"; Legs = @("tampered-package", "running-stack"); Expected = @("default-minimal") }
        @{ Case = "extension-selection needs, for its restore and its refusal"; Legs = @("extension-selection"); Expected = @("default-minimal", "core-only-minimal") }
        @{ Case = "a populated-only run needs"; Legs = @("populated"); Expected = @("default-populated") }
        @{ Case = "a mixed run needs, each once and in table order"; Legs = @("populated", "extension-selection", "separate-config", "package-directory"); Expected = @("default-minimal", "core-only-minimal", "default-populated") }
    ) {
        @(Get-RestoreSmokeLegPackageFixture -Leg $Legs) | Should -Be $Expected
    }

    It "derives no fixture for no leg" {
        @(Get-RestoreSmokeLegPackageFixture -Leg @()) | Should -BeNullOrEmpty
    }

    It "maps restore <execution> to the <expected> fixture" -ForEach @(
        @{ Execution = "package-directory#1"; Expected = "default-minimal" }
        @{ Execution = "package-directory#2"; Expected = "default-minimal" }
        @{ Execution = "separate-config#1"; Expected = "default-minimal" }
        @{ Execution = "directory-feed#1"; Expected = "default-minimal" }
        @{ Execution = "extension-selection#1"; Expected = "core-only-minimal" }
        @{ Execution = "populated#1"; Expected = "default-populated" }
    ) {
        (Get-RestoreSmokeRestoreExecutionFixture -RestoreExecution $Execution).Name | Should -BeExactly $Expected
    }

    It "refuses restore execution <execution>" -ForEach @(
        @{ Execution = "tampered-package#1"; Expected = "Leg 'tampered-package' performs no successful restore, so it has no restore execution id." }
        @{ Execution = "extension-selection#2"; Expected = "Leg 'extension-selection' performs 1 successful restore(s); restore 2 is not defined." }
        @{ Execution = "package-directory"; Expected = "'package-directory' is not a restore execution id ('<leg>#<ordinal>')." }
        @{ Execution = "package-directory#0"; Expected = "'package-directory#0' is not a restore execution id ('<leg>#<ordinal>')." }
    ) {
        { Get-RestoreSmokeRestoreExecutionFixture -RestoreExecution $Execution } | Should -Throw -ExpectedMessage $Expected
    }

    It "requires extension-selection's one restore, of the core-only fixture" {
        Get-RestoreSmokeRestoreExecutionId -Leg "extension-selection" | Should -BeExactly "extension-selection#1"
        $requirement = Get-RestoreSmokeRequiredApiRead -Legs @("extension-selection", "package-directory")

        @($requirement.Required | ForEach-Object { $_.RestoreExecution }) | Should -Be @("extension-selection#1", "package-directory#1", "package-directory#2")
        @($requirement.Required | ForEach-Object { $_.PackageFixture }) | Should -Be @("core-only-minimal", "default-minimal", "default-minimal")
        @($requirement.Required | ForEach-Object { $_.TemplateKind }) | Should -Be @("Minimal", "Minimal", "Minimal")
        @($requirement.UnknownLegs) | Should -BeNullOrEmpty
    }

    It "refuses extension-selection <case>" -ForEach @(
        @{ Case = "with -Wrapper local"; Wrapper = "local"; Supplied = $false; Expected = "The extension-selection leg requires -Wrapper published; it proves the selection a published-image deployment takes from its env file." }
        @{ Case = "with -Wrapper local and an explicit Data Standard"; Wrapper = "local"; Supplied = $true; Expected = "The extension-selection leg requires -Wrapper published; it proves the selection a published-image deployment takes from its env file." }
        @{ Case = "with an explicit -DataStandardVersion"; Wrapper = "published"; Supplied = $true; Expected = "The extension-selection leg refuses an explicit -DataStandardVersion: the published wrapper would compose the Data Standard overlay's SCHEMA_PACKAGES over the env file's selection." }
    ) {
        { Assert-RestoreSmokeLegSelection -Leg @("package-directory", "extension-selection") -Wrapper $Wrapper -DataStandardVersionSupplied $Supplied } | Should -Throw -ExpectedMessage $Expected
    }

    It "accepts <case>" -ForEach @(
        @{ Case = "extension-selection under the published wrapper without a Data Standard"; Legs = @("extension-selection"); Wrapper = "published"; Supplied = $false }
        @{ Case = "other legs under the local wrapper with a Data Standard"; Legs = @("package-directory", "populated"); Wrapper = "local"; Supplied = $true }
        @{ Case = "other legs under the published wrapper with a Data Standard"; Legs = @("separate-config"); Wrapper = "published"; Supplied = $true }
        @{ Case = "no leg"; Legs = @(); Wrapper = "local"; Supplied = $true }
    ) {
        { Assert-RestoreSmokeLegSelection -Leg $Legs -Wrapper $Wrapper -DataStandardVersionSupplied $Supplied } | Should -Not -Throw
    }
}

Describe "Write-RestoreSmokeCoreOnlyEnvironmentFile" {
    BeforeAll {
        # The test reads the written file with the parser prepare-dms-schema.ps1 uses.
        Import-Module (Join-Path $PSScriptRoot "../../schema-package-utility.psm1") -Force

        $script:coreEntry = '{ "version": "1.0.335", "feedUrl": "https://feed.example.invalid/index.json", "name": "EdFi.DataStandard52.ApiSchema" }'
        $script:tpdmEntry = '{ "version": "1.0.335", "feedUrl": "https://feed.example.invalid/index.json", "name": "EdFi.DataStandard52.TPDM.ApiSchema" }'
        $script:schemaBlock = "SCHEMA_PACKAGES='[`n  $script:coreEntry,`n  $script:tpdmEntry`n]'"

        function script:New-BaseEnvironmentFile {
            param([string]$SchemaPackages = $script:schemaBlock)

            $path = Join-Path $TestDrive "base-$([Guid]::NewGuid().ToString('N')).env"
            $content = @(
                "POSTGRES_DB_NAME=edfi_datamanagementservice"
                "DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.5.2.0"
                "# SCHEMA_PACKAGES='[]' in a comment is not a declaration"
                $SchemaPackages
                "DMS_CONFIG_ASPNETCORE_HTTP_PORTS=8081"
                "DMS_IMAGE_TAG=dms-restore-smoke-0123456789ab"
                "DMS_CONFIG_DOCKER_IMAGE=local/ed-fi-api-configuration-service:dms-restore-smoke-0123456789ab"
            ) -join "`n"
            [System.IO.File]::WriteAllText($path, $content + "`n")
            return $path
        }
    }

    It "keeps only the core package and every other line, including the run's image tags" {
        $base = New-BaseEnvironmentFile
        $target = Join-Path $TestDrive "core-$([Guid]::NewGuid().ToString('N')).env"

        $record = Write-RestoreSmokeCoreOnlyEnvironmentFile -BaseEnvironmentFile $base -TargetPath $target

        $record.Selection | Should -BeExactly "core-only"
        $record.FileName | Should -BeExactly ([System.IO.Path]::GetFileName($target))
        @($record.SelectedPackages) | Should -Be @("EdFi.DataStandard52.ApiSchema@1.0.335")
        @($record.RemovedPackages) | Should -Be @("EdFi.DataStandard52.TPDM.ApiSchema@1.0.335")
        $packages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $target)
        $packages.Count | Should -Be 1
        $packages[0].name | Should -BeExactly "EdFi.DataStandard52.ApiSchema"
        $packages[0].version | Should -BeExactly "1.0.335"
        $packages[0].feedUrl | Should -BeExactly "https://feed.example.invalid/index.json"

        $written = Get-Content -LiteralPath $target -Raw
        $schemaLine = @($written -split "`n" | Where-Object { $_ -like "SCHEMA_PACKAGES=*" })
        $schemaLine.Count | Should -Be 1
        $written.Replace($schemaLine[0], "<schema>") | Should -BeExactly ((Get-Content -LiteralPath $base -Raw).Replace($script:schemaBlock, "<schema>"))
        $written | Should -Match '(?m)^DMS_IMAGE_TAG=dms-restore-smoke-0123456789ab$'
        $written | Should -Match '(?m)^DMS_CONFIG_DOCKER_IMAGE=local/ed-fi-api-configuration-service:dms-restore-smoke-0123456789ab$'
    }

    It "derives the core-only selection from the repository's .env.example" {
        $base = Join-Path $TestDrive "env-example-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../.env.example") -Destination $base
        $target = Join-Path $TestDrive "core-$([Guid]::NewGuid().ToString('N')).env"

        $record = Write-RestoreSmokeCoreOnlyEnvironmentFile -BaseEnvironmentFile $base -TargetPath $target

        @($record.SelectedPackages) | Should -HaveCount 1
        @($record.SelectedPackages)[0] | Should -Match '^EdFi\.DataStandard52\.ApiSchema@\d+\.\d+\.\d+$'
        @($record.RemovedPackages | Where-Object { $_ -like "EdFi.DataStandard52.TPDM.ApiSchema@*" }) | Should -HaveCount 1
        @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $target | ForEach-Object { "$($_.name)@$($_.version)" }) | Should -Be @($record.SelectedPackages)
    }

    It "refuses, writing nothing, when SCHEMA_PACKAGES <case>" -ForEach @(
        @{ Case = "lists only the core package"; Block = "SCHEMA_PACKAGES='[{ `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard52.ApiSchema`" }]'"; Expected = "lists only the core package, so a core-only selection would equal the default one." }
        @{ Case = "lists no core package"; Block = "SCHEMA_PACKAGES='[{ `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard52.TPDM.ApiSchema`" }]'"; Expected = "lists 0 core packages (EdFi.DataStandard<NN>.ApiSchema); expected exactly one." }
        @{ Case = "lists two core packages"; Block = "SCHEMA_PACKAGES='[{ `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard52.ApiSchema`" }, { `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard61.ApiSchema`" }, { `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard52.TPDM.ApiSchema`" }]'"; Expected = "lists 2 core packages (EdFi.DataStandard<NN>.ApiSchema); expected exactly one." }
        @{ Case = "has an entry without a version"; Block = "SCHEMA_PACKAGES='[{ `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard52.ApiSchema`" }, { `"name`": `"EdFi.DataStandard52.TPDM.ApiSchema`" }]'"; Expected = "has an entry without both name and version." }
        @{ Case = "is declared twice"; Block = "SCHEMA_PACKAGES='[]'`nSCHEMA_PACKAGES='[{ `"version`": `"1.0.335`", `"name`": `"EdFi.DataStandard52.ApiSchema`" }]'"; Expected = "SCHEMA_PACKAGES 2 times; expected exactly once." }
        @{ Case = "is not declared"; Block = "OTHER_KEY=1"; Expected = "SCHEMA_PACKAGES 0 times; expected exactly once." }
        @{ Case = "is not a quoted array"; Block = "SCHEMA_PACKAGES=[]"; Expected = "is not a quoted JSON array." }
    ) {
        $base = New-BaseEnvironmentFile -SchemaPackages $Block
        $target = Join-Path $TestDrive "core-$([Guid]::NewGuid().ToString('N')).env"

        { Write-RestoreSmokeCoreOnlyEnvironmentFile -BaseEnvironmentFile $base -TargetPath $target } | Should -Throw -ExpectedMessage "Cannot derive a core-only env: *$Expected"
        Test-Path -LiteralPath $target | Should -BeFalse
    }
}

Describe "Read-RestoreSmokeWorkspaceSelection" {
    BeforeAll {
        $script:coreHash = "c0" * 32

        function script:New-TestWorkspace {
            param(
                [object]$SelectedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335"),
                [object]$SelectedExtensions = @(),
                [string]$ApiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json",
                [object[]]$Projects = @(@{ projectEndpointName = "ed-fi"; isExtensionProject = $false; schemaPath = "ed-fi/ApiSchema.json" }),
                [switch]$OmitSelectedPackages,
                [switch]$OmitApiSchemaManifest
            )

            $root = Join-Path $TestDrive "bootstrap-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path (Join-Path $root "ApiSchema") -Force | Out-Null
            $schema = [ordered]@{ selectionMode = "standard"; selectedExtensions = $SelectedExtensions; effectiveSchemaHash = $script:coreHash; apiSchemaManifestPath = $ApiSchemaManifestPath }
            if (-not $OmitSelectedPackages) {
                $schema.selectedPackages = $SelectedPackages
            }
            [ordered]@{ version = 1; schema = $schema } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root "bootstrap-manifest.json")
            if (-not $OmitApiSchemaManifest) {
                [ordered]@{ version = 1; projects = $Projects } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root "ApiSchema/bootstrap-api-schema-manifest.json")
            }
            return $root
        }
    }

    It "reads a core-only workspace" {
        $record = Read-RestoreSmokeWorkspaceSelection -BootstrapRoot (New-TestWorkspace)

        $record.Reason | Should -BeNullOrEmpty
        @($record.SelectedPackages) | Should -Be @("EdFi.DataStandard52.ApiSchema@1.0.335")
        @($record.SelectedExtensions) | Should -BeNullOrEmpty
        $record.CoreProjectEndpointName | Should -BeExactly "ed-fi"
        @($record.ProjectSchemas) | Should -Be @("edfi")
        $record.EffectiveSchemaHash | Should -BeExactly $script:coreHash
    }

    It "reads a Core+TPDM workspace as two projects" {
        $projects = @(
            @{ projectEndpointName = "ed-fi"; isExtensionProject = $false; schemaPath = "ed-fi/ApiSchema.json" }
            @{ projectEndpointName = "tpdm"; isExtensionProject = $true; schemaPath = "tpdm/ApiSchema.json" }
        )
        $record = Read-RestoreSmokeWorkspaceSelection -BootstrapRoot (New-TestWorkspace -SelectedPackages @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335") -SelectedExtensions @("tpdm") -Projects $projects)

        $record.Reason | Should -BeNullOrEmpty
        @($record.ProjectSchemas) | Should -Be @("edfi", "tpdm")
        @($record.SelectedPackages) | Should -HaveCount 2
    }

    It "records a workspace without schema.selectedPackages as null, not as an empty selection" {
        $record = Read-RestoreSmokeWorkspaceSelection -BootstrapRoot (New-TestWorkspace -OmitSelectedPackages)

        $record.Reason | Should -BeNullOrEmpty
        $null -eq $record.SelectedPackages | Should -BeTrue
        @($record.ProjectSchemas) | Should -Be @("edfi")
    }

    It "reports, without throwing, <case>" -ForEach @(
        @{ Case = "an escaping ApiSchema manifest path"; Arguments = @{ ApiSchemaManifestPath = "../outside.json" }; Expected = "schema.apiSchemaManifestPath '../outside.json' is not a path inside the workspace" }
        @{ Case = "an empty path segment"; Arguments = @{ ApiSchemaManifestPath = "ApiSchema//bootstrap-api-schema-manifest.json" }; Expected = "schema.apiSchemaManifestPath 'ApiSchema//bootstrap-api-schema-manifest.json' is not a path inside the workspace" }
        @{ Case = "a missing ApiSchema manifest"; Arguments = @{ OmitApiSchemaManifest = $true }; Expected = "the staged ApiSchema manifest 'ApiSchema/bootstrap-api-schema-manifest.json' is missing" }
        @{ Case = "two core projects"; Arguments = @{ Projects = @(@{ projectEndpointName = "ed-fi"; isExtensionProject = $false; schemaPath = "a.json" }, @{ projectEndpointName = "other"; isExtensionProject = $false; schemaPath = "b.json" }) }; Expected = "the staged ApiSchema manifest declares 2 core projects; expected exactly one" }
        @{ Case = "a non-array selectedExtensions"; Arguments = @{ SelectedExtensions = "tpdm" }; Expected = "schema.selectedExtensions is not an array" }
    ) {
        $record = Read-RestoreSmokeWorkspaceSelection -BootstrapRoot (New-TestWorkspace @Arguments)

        $record.Reason | Should -BeExactly $Expected
        $record.ProjectSchemas | Should -BeNullOrEmpty
    }

    It "reports a rooted ApiSchema manifest path" {
        $root = New-TestWorkspace -ApiSchemaManifestPath (Join-Path $TestDrive "elsewhere.json")

        (Read-RestoreSmokeWorkspaceSelection -BootstrapRoot $root).Reason | Should -BeLike "schema.apiSchemaManifestPath '*elsewhere.json' is not a path inside the workspace"
    }

    It "reports a missing workspace, a missing schema section, and unreadable JSON" {
        (Read-RestoreSmokeWorkspaceSelection -BootstrapRoot (Join-Path $TestDrive "absent")).Reason | Should -BeExactly "the active workspace has no bootstrap-manifest.json"

        $noSchema = Join-Path $TestDrive "no-schema-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $noSchema | Out-Null
        Set-Content -LiteralPath (Join-Path $noSchema "bootstrap-manifest.json") -Value '{ "version": 1 }'
        (Read-RestoreSmokeWorkspaceSelection -BootstrapRoot $noSchema).Reason | Should -BeExactly "bootstrap-manifest.json has no schema section"

        $broken = Join-Path $TestDrive "broken-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $broken | Out-Null
        Set-Content -LiteralPath (Join-Path $broken "bootstrap-manifest.json") -Value '{ "schema": '
        (Read-RestoreSmokeWorkspaceSelection -BootstrapRoot $broken).Reason | Should -BeLike "the active workspace could not be read: *"
    }
}

Describe "Get-RestoreSmokeCatalogSelection and Get-RestoreSmokeRefusalState" {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot "../../DatabaseTemplates/Template-RestoreCore.psm1") -Force
        $script:coreHash = "c0" * 32
        $script:seedHash = "ab" * 32

        # A fake engine and daemon answered from $global:RestoreSmokeSelectionTest: schema rows,
        # the EffectiveSchema rows, container and volume names, and per-call failures.
        Mock docker -ModuleName RestoreSmokeProbes {
            $state = $global:RestoreSmokeSelectionTest
            $line = $args -join " "
            $state.Calls.Add($line)
            $step = "unknown"
            if ($line.Contains("pg_namespace") -or $line.Contains("sys.schemas")) { $step = "schemas" }
            elseif ($line.Contains("EffectiveSchema")) { $step = "effective" }
            elseif ($args[0] -eq "ps") { $step = "ps" }
            elseif ($args[0] -eq "volume" -and $args[1] -eq "ls") { $step = "volumes" }
            if ($state.Fail.ContainsKey($step)) {
                $global:LASTEXITCODE = 1
                return $state.Fail[$step]
            }
            $global:LASTEXITCODE = 0
            switch ($step) {
                "schemas" { return $state.SchemaRows }
                "effective" { return $state.EffectiveRows }
                "ps" { return $state.Containers }
                "volumes" { return $state.Volumes }
                default { $global:LASTEXITCODE = 99; return "unexpected docker call: $line" }
            }
        }

        function script:Reset-SelectionDocker {
            param(
                [string[]]$SchemaRows = @("auth", "dms", "edfi", "public", "tracked_changes_edfi"),
                [string[]]$EffectiveRows = @("1.0.0|$($script:coreHash)|42|$($script:seedHash)"),
                [string[]]$Containers = @(),
                [string[]]$Volumes = @(),
                [hashtable]$Fail = @{}
            )

            $global:RestoreSmokeSelectionTest = @{
                Calls         = [System.Collections.Generic.List[string]]::new()
                SchemaRows    = $SchemaRows
                EffectiveRows = $EffectiveRows
                Containers    = $Containers
                Volumes       = $Volumes
                Fail          = $Fail
            }
        }
    }

    AfterAll {
        Remove-Variable -Name RestoreSmokeSelectionTest -Scope Global -ErrorAction SilentlyContinue
    }

    It "partitions the PostgreSQL catalog with the restore consumer's own queries" {
        Reset-SelectionDocker

        $record = Get-RestoreSmokeCatalogSelection -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice"

        $record.Reason | Should -BeNullOrEmpty
        @($record.SchemaNames) | Should -Be @("auth", "dms", "edfi", "public", "tracked_changes_edfi")
        @($record.ProjectSchemas) | Should -Be @("edfi")
        @($record.TrackedChangesProjects) | Should -Be @("edfi")
        $record.EffectiveSchemaHash | Should -BeExactly $script:coreHash
        $calls = $global:RestoreSmokeSelectionTest.Calls
        $calls[0] | Should -BeExactly ("exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -tA -c " + (Get-InventorySchemaQuerySql -DatabaseEngine postgresql -Purpose InventoryEnumeration))
        $calls[1] | Should -BeExactly ("exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -tA -c " + (Get-EffectiveSchemaRowQuerySql -DatabaseEngine postgresql))
    }

    It "partitions a SQL Server catalog holding an extension project" {
        Reset-SelectionDocker -SchemaRows @("dbo", "dms", "edfi", "tpdm", "tracked_changes_tpdm")

        $record = Get-RestoreSmokeCatalogSelection -DatabaseEngine mssql -DatabaseName "edfi_datamanagementservice"

        @($record.ProjectSchemas) | Should -Be @("edfi", "tpdm")
        @($record.TrackedChangesProjects) | Should -Be @("tpdm")
        $global:RestoreSmokeSelectionTest.Calls[0].Contains("/opt/mssql-tools18/bin/sqlcmd") | Should -BeTrue
        $global:RestoreSmokeSelectionTest.Calls[0].EndsWith((Get-InventorySchemaQuerySql -DatabaseEngine mssql -Purpose InventoryEnumeration)) | Should -BeTrue
    }

    It "reports, without throwing, <case>" -ForEach @(
        @{ Case = "a failed schema query"; Arguments = @{ Fail = @{ schemas = "FATAL: database does not exist" } }; Expected = "the catalog schema query against 'edfi_datamanagementservice' exited 1: FATAL: database does not exist" }
        @{ Case = "a failed EffectiveSchema query"; Arguments = @{ Fail = @{ effective = "ERROR: relation does not exist" } }; Expected = "the dms.EffectiveSchema query against 'edfi_datamanagementservice' exited 1: ERROR: relation does not exist" }
        @{ Case = "two EffectiveSchema rows"; Arguments = @{ EffectiveRows = @("1.0.0|$("c0" * 32)|42|$("ab" * 32)", "1.0.0|$("c0" * 32)|42|$("ab" * 32)") }; Expected = "the dms.EffectiveSchema row of 'edfi_datamanagementservice' could not be read: Expected exactly one dms.EffectiveSchema row, found 2. The source database is not a provisioned DMS datastore." }
    ) {
        Reset-SelectionDocker @Arguments

        $record = Get-RestoreSmokeCatalogSelection -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice"

        $record.Reason | Should -BeExactly $Expected
        $record.EffectiveSchemaHash | Should -BeNullOrEmpty
    }

    It "records the project's containers and volumes, the workspace, and the restore candidates" {
        Reset-SelectionDocker -Volumes @("dms-published_dms-mssql-2025")
        $composeRoot = Join-Path $TestDrive "compose-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path (Join-Path $composeRoot ".bootstrap-restore/candidate-0123"), (Join-Path $composeRoot ".bootstrap-restore/derived") -Force | Out-Null

        $state = Get-RestoreSmokeRefusalState -ComposeProject "dms-published" -BootstrapRoot (Join-Path $composeRoot ".bootstrap") -RestoreWorkspaceRoot (Join-Path $composeRoot ".bootstrap-restore")

        $state.Reason | Should -BeNullOrEmpty
        @($state.Containers) | Should -BeNullOrEmpty
        $null -eq $state.Containers | Should -BeFalse
        @($state.Volumes) | Should -Be @("dms-published_dms-mssql-2025")
        $state.WorkspacePresent | Should -BeFalse
        @($state.CandidateDirectories) | Should -Be @("candidate-0123")
        @($global:RestoreSmokeSelectionTest.Calls) | Should -Be @(
            "ps -a --filter label=com.docker.compose.project=dms-published --format {{.Names}}"
            "volume ls --filter label=com.docker.compose.project=dms-published --format {{.Name}}"
        )

        New-Item -ItemType Directory -Path (Join-Path $composeRoot ".bootstrap") | Out-Null
        Reset-SelectionDocker -Containers @("ed-fi-api")
        $present = Get-RestoreSmokeRefusalState -ComposeProject "dms-published" -BootstrapRoot (Join-Path $composeRoot ".bootstrap") -RestoreWorkspaceRoot (Join-Path $composeRoot ".bootstrap-restore")
        $present.WorkspacePresent | Should -BeTrue
        @($present.Containers) | Should -Be @("ed-fi-api")
    }

    It "reports a failed <step> listing and leaves the Docker lists unknown" -ForEach @(
        @{ Step = "ps"; Expected = "docker ps exited 1: Cannot connect to the Docker daemon" }
        @{ Step = "volumes"; Expected = "docker volume ls exited 1: Cannot connect to the Docker daemon" }
    ) {
        Reset-SelectionDocker -Fail @{ $Step = "Cannot connect to the Docker daemon" }

        $state = Get-RestoreSmokeRefusalState -ComposeProject "dms-published" -BootstrapRoot (Join-Path $TestDrive "none") -RestoreWorkspaceRoot (Join-Path $TestDrive "none-restore")

        $state.Reason | Should -BeExactly $Expected
        $null -eq $state.Containers | Should -BeTrue
        $null -eq $state.Volumes | Should -BeTrue
    }
}

Describe "Get-RestoreSmokeSelectionDefect" {
    BeforeAll {
        $script:coreHash = "c0" * 32
        $script:defaultHash = "d0" * 32
        $script:corePackageId = "EdFi.DataStandard52.ApiSchema@1.0.335"
        $script:tpdmPackageId = "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"

        function script:New-SelectionProof {
            return [pscustomobject]@{
                RestoreExecution = "extension-selection#1"
                PackageFixture   = "core-only-minimal"
                PackageSha256    = ("c1" * 32)
                Workspace        = [pscustomobject]@{ Manifest = "bootstrap-manifest.json"; SelectedPackages = @($script:corePackageId); SelectedExtensions = @(); CoreProjectEndpointName = "ed-fi"; ProjectSchemas = @("edfi"); EffectiveSchemaHash = $script:coreHash; Reason = $null }
                Catalog          = [pscustomobject]@{ DatabaseName = "edfi_datamanagementservice"; SchemaNames = @("auth", "dms", "edfi", "public"); ProjectSchemas = @("edfi"); TrackedChangesProjects = @("edfi"); EffectiveSchemaHash = $script:coreHash; Reason = $null }
            }
        }

        function script:New-SelectionPackage {
            return [pscustomobject]@{ PackageFixture = "core-only-minimal"; TemplateKind = "Minimal"; Sha256 = ("c1" * 32); RestoreManifestProjects = @("edfi"); RestoreManifestEffectiveSchemaHash = $script:coreHash; Verified = $true }
        }
    }

    It "finds no defect when the workspace, the restore manifest, and the catalog all select the core project" {
        @(Get-RestoreSmokeSelectionDefect -Proof (New-SelectionProof) -Package (New-SelectionPackage) -ExpectedProject @("edfi") -ExpectedPackage @($script:corePackageId)) | Should -BeNullOrEmpty
    }

    It "compares package identities case-insensitively, as the wrapper does" {
        $proof = New-SelectionProof
        $proof.Workspace.SelectedPackages = @($script:corePackageId.ToLowerInvariant())

        @(Get-RestoreSmokeSelectionDefect -Proof $proof -Package (New-SelectionPackage) -ExpectedProject @("edfi") -ExpectedPackage @($script:corePackageId)) | Should -BeNullOrEmpty
    }

    It "reports exactly one defect when <case>" -ForEach @(
        @{ Case = "the workspace also staged TPDM"; Mutate = { param($case) $case.Proof.Workspace.SelectedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335") }; Expected = "the workspace staged packages [EdFi.DataStandard52.ApiSchema@1.0.335, EdFi.DataStandard52.TPDM.ApiSchema@1.0.335], but the selection env selects [EdFi.DataStandard52.ApiSchema@1.0.335]" }
        @{ Case = "the workspace staged another core version"; Mutate = { param($case) $case.Proof.Workspace.SelectedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.334") }; Expected = "the workspace staged packages [EdFi.DataStandard52.ApiSchema@1.0.334], but the selection env selects [EdFi.DataStandard52.ApiSchema@1.0.335]" }
        @{ Case = "the workspace records no selectedPackages"; Mutate = { param($case) $case.Proof.Workspace.SelectedPackages = $null }; Expected = "the workspace manifest records no schema.selectedPackages" }
        @{ Case = "the workspace selects TPDM"; Mutate = { param($case) $case.Proof.Workspace.ProjectSchemas = @("edfi", "tpdm") }; Expected = "the workspace selects projects [edfi, tpdm], expected [edfi]" }
        @{ Case = "the workspace could not be read"; Mutate = { param($case) $case.Proof.Workspace.Reason = "the active workspace has no bootstrap-manifest.json" }; Expected = "the active workspace could not be read: the active workspace has no bootstrap-manifest.json" }
        @{ Case = "the workspace was not recorded"; Mutate = { param($case) $case.Proof.Workspace = $null }; Expected = "the active workspace's selection was not recorded" }
        @{ Case = "the restore manifest declares TPDM"; Mutate = { param($case) $case.Package.RestoreManifestProjects = @("edfi", "tpdm") }; Expected = "the restore manifest declares projects [edfi, tpdm], expected [edfi]" }
        @{ Case = "the catalog holds a TPDM schema"; Mutate = { param($case) $case.Proof.Catalog.ProjectSchemas = @("edfi", "tpdm") }; Expected = "the target catalog holds project schemas [edfi, tpdm], expected [edfi]" }
        @{ Case = "the catalog repeats the core schema"; Mutate = { param($case) $case.Proof.Catalog.ProjectSchemas = @("edfi", "edfi") }; Expected = "the target catalog holds project schemas [edfi, edfi], expected [edfi]" }
        @{ Case = "the catalog holds no project schema"; Mutate = { param($case) $case.Proof.Catalog.ProjectSchemas = @() }; Expected = "the target catalog holds project schemas [], expected [edfi]" }
        @{ Case = "the catalog holds a TPDM tracked_changes companion"; Mutate = { param($case) $case.Proof.Catalog.TrackedChangesProjects = @("edfi", "tpdm") }; Expected = "the target catalog holds tracked_changes companions of projects outside the selection: [tpdm]" }
        @{ Case = "the catalog could not be read"; Mutate = { param($case) $case.Proof.Catalog.Reason = "the catalog schema query against 'edfi_datamanagementservice' exited 2: refused" }; Expected = "the target catalog could not be read: the catalog schema query against 'edfi_datamanagementservice' exited 2: refused" }
        @{ Case = "the catalog was not recorded"; Mutate = { param($case) $case.Proof.Catalog = $null }; Expected = "the target catalog's selection was not recorded" }
        @{ Case = "the workspace hash differs"; Mutate = { param($case) $case.Proof.Workspace.EffectiveSchemaHash = "d0" * 32 }; Expected = "the effective schema hashes disagree: workspace $("d0" * 32), restore manifest $("c0" * 32), target catalog $("c0" * 32)" }
        @{ Case = "the restore manifest hash differs"; Mutate = { param($case) $case.Package.RestoreManifestEffectiveSchemaHash = "d0" * 32 }; Expected = "the effective schema hashes disagree: workspace $("c0" * 32), restore manifest $("d0" * 32), target catalog $("c0" * 32)" }
        @{ Case = "the catalog hash differs"; Mutate = { param($case) $case.Proof.Catalog.EffectiveSchemaHash = "d0" * 32 }; Expected = "the effective schema hashes disagree: workspace $("c0" * 32), restore manifest $("c0" * 32), target catalog $("d0" * 32)" }
        @{ Case = "the restore manifest hash is not lowercase hex"; Mutate = { param($case) $case.Package.RestoreManifestEffectiveSchemaHash = "C0" * 32 }; Expected = "the restore manifest effective schema hash '$("C0" * 32)' is not 64 lowercase hex" }
        @{ Case = "the catalog hash is missing"; Mutate = { param($case) $case.Proof.Catalog.EffectiveSchemaHash = $null }; Expected = "the target catalog effective schema hash '' is not 64 lowercase hex" }
    ) {
        $proof = New-SelectionProof
        $package = New-SelectionPackage
        & $Mutate ([pscustomobject]@{ Proof = $proof; Package = $package })

        @(Get-RestoreSmokeSelectionDefect -Proof $proof -Package $package -ExpectedProject @("edfi") -ExpectedPackage @($script:corePackageId)) | Should -Be @($Expected)
    }

    It "reports a missing package record" {
        @(Get-RestoreSmokeSelectionDefect -Proof (New-SelectionProof) -Package $null -ExpectedProject @("edfi") -ExpectedPackage @($script:corePackageId)) | Should -Be @("no package record exists for the restored package, so its restore manifest is unknown")
    }

    It "reports undefined expectations instead of passing" {
        $defects = @(Get-RestoreSmokeSelectionDefect -Proof (New-SelectionProof) -Package (New-SelectionPackage) -ExpectedProject @() -ExpectedPackage @())

        $defects | Should -Contain "no expected project schemas are defined for the selection"
        $defects | Should -Contain "the selection env records no selected packages"
    }
}

Describe "Get-RestoreSmokeSelectionRefusalDefect" {
    BeforeAll {
        $script:coreHash = "c0" * 32
        $script:defaultHash = "d0" * 32
        $script:defaultSha = "d1" * 32
        $script:coreSha = "c1" * 32

        function script:New-RefusalPackage {
            param([string]$Fixture)

            if ($Fixture -eq "default-minimal") {
                return [pscustomobject]@{ PackageFixture = "default-minimal"; TemplateKind = "Minimal"; Sha256 = $script:defaultSha; RestoreManifestProjects = @("edfi", "tpdm"); RestoreManifestEffectiveSchemaHash = $script:defaultHash }
            }
            return [pscustomobject]@{ PackageFixture = "core-only-minimal"; TemplateKind = "Minimal"; Sha256 = $script:coreSha; RestoreManifestProjects = @("edfi"); RestoreManifestEffectiveSchemaHash = $script:coreHash }
        }

        function script:New-RefusalState {
            return [pscustomobject]@{ ComposeProject = "dms-published"; Containers = @(); Volumes = @("dms-published_dms-mssql-2025"); WorkspacePresent = $false; CandidateDirectories = @("candidate-old"); Reason = $null }
        }

        function script:New-Refusal {
            $sentence = Get-RestoreSmokeSelectionRefusalExpectedMessage -PackageEffectiveSchemaHash $script:defaultHash -CandidateEffectiveSchemaHash $script:coreHash
            return [pscustomobject]@{
                Leg                  = "extension-selection"
                PackageFixture       = "default-minimal"
                EnvironmentSelection = "core-only"
                PackageFile          = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.999.nupkg"
                PackageSha256        = $script:defaultSha
                Before               = (New-RefusalState)
                After                = (New-RefusalState)
                Refused              = $true
                Message              = $sentence
            }
        }
    }

    It "states the cross-check's effective-schema-hash sentence" {
        Get-RestoreSmokeSelectionRefusalExpectedMessage -PackageEffectiveSchemaHash "aa" -CandidateEffectiveSchemaHash "bb" | Should -BeExactly "Effective schema hash mismatch: the restore manifest declares 'aa' but the candidate workspace staged 'bb'."
    }

    It "matches what the production cross-check throws for a Core+TPDM package against a core-only candidate, and only then the project set" {
        # The production comparison, in its own process so the restore module's imports do not
        # reach this session: the hash is compared before the project set, so differing
        # selections are refused on the hash; with equal hashes the project set is what differs.
        $restoreModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../bootstrap-restore.psm1"))
        $restoreCoreModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../DatabaseTemplates/Template-RestoreCore.psm1"))
        $probe = @'
param([string]$RestoreModule, [string]$RestoreCoreModule, [string]$ManifestHash, [string]$CandidateHash)
$ErrorActionPreference = "Stop"
Import-Module $RestoreCoreModule -Force
Import-Module $RestoreModule -Force
$manifest = [pscustomobject]@{ databaseEngine = "postgresql"; documentJsonColumnType = (Get-RestoreDocumentJsonBaselineType -DatabaseEngine postgresql); dataStandardVersion = "5.2.0"; apiSchemaFormatVersion = "1.0.0"; effectiveSchemaHash = $ManifestHash; relationalMappingVersion = "v3"; projects = @("edfi", "tpdm") }
$fact = [pscustomobject]@{ DataStandardVersion = "5.2.0"; ApiSchemaFormatVersion = "1.0.0"; EffectiveSchemaHash = $CandidateHash; SelectedExtensions = [string[]]@(); CoreProjectEndpointName = "ed-fi"; SchemaFilePaths = [string[]]@() }
try {
    Assert-RestoreManifestMatchesCandidate -Manifest $manifest -CandidateFact $fact -DatabaseEngine postgresql -CandidateRelationalMappingVersion "v3"
    "no refusal"
}
catch {
    $_.Exception.Message
}
'@
        $probePath = Join-Path $TestDrive "cross-check-probe.ps1"
        Set-Content -LiteralPath $probePath -Value $probe
        $pwshPath = (Get-Process -Id $PID).Path

        $differing = & $pwshPath -NoProfile -NonInteractive -File $probePath -RestoreModule $restoreModule -RestoreCoreModule $restoreCoreModule -ManifestHash $script:defaultHash -CandidateHash $script:coreHash 2>&1
        $equal = & $pwshPath -NoProfile -NonInteractive -File $probePath -RestoreModule $restoreModule -RestoreCoreModule $restoreCoreModule -ManifestHash $script:coreHash -CandidateHash $script:coreHash 2>&1

        ($differing | Out-String).Trim() | Should -BeExactly (Get-RestoreSmokeSelectionRefusalExpectedMessage -PackageEffectiveSchemaHash $script:defaultHash -CandidateEffectiveSchemaHash $script:coreHash)
        ($equal | Out-String).Trim() | Should -BeExactly "Project set mismatch: the restore manifest declares projects [edfi, tpdm] but the candidate workspace stages [edfi]."
    }

    It "finds no defect for a refusal by the cross-check that changed nothing" {
        $refusal = New-Refusal
        $refusal.Message = "bootstrap failed: $($refusal.Message) (restore preflight)"

        @(Get-RestoreSmokeSelectionRefusalDefect -Refusal $refusal -DefaultPackage (New-RefusalPackage "default-minimal") -SelectedPackage (New-RefusalPackage "core-only-minimal")) | Should -BeNullOrEmpty
    }

    It "reports exactly one defect when <case>" -ForEach @(
        @{ Case = "the restore was not refused"; Mutate = { param($case) $case.Refusal.Refused = $false; $case.Refusal.Message = $null }; Expected = "the default package was not refused under the core-only env" }
        @{ Case = "the refusal came from the project-set comparison"; Mutate = { param($case) $case.Refusal.Message = "Project set mismatch: the restore manifest declares projects [edfi, tpdm] but the candidate workspace stages [edfi]." }; Expected = "the refusal was not the package-to-candidate cross-check's effective schema hash mismatch: Project set mismatch: the restore manifest declares projects [edfi, tpdm] but the candidate workspace stages [edfi]." }
        @{ Case = "the refusal names the hashes the other way round"; Mutate = { param($case) $case.Refusal.Message = Get-RestoreSmokeSelectionRefusalExpectedMessage -PackageEffectiveSchemaHash ("c0" * 32) -CandidateEffectiveSchemaHash ("d0" * 32) }; Expected = "the refusal was not the package-to-candidate cross-check's effective schema hash mismatch: Effective schema hash mismatch: the restore manifest declares '$("c0" * 32)' but the candidate workspace staged '$("d0" * 32)'." }
        @{ Case = "the stop proof refused instead"; Mutate = { param($case) $case.Refusal.Message = "Restore refused: compose project 'dms-published' still has running containers: ed-fi-api." }; Expected = "the refusal was not the package-to-candidate cross-check's effective schema hash mismatch: Restore refused: compose project 'dms-published' still has running containers: ed-fi-api." }
        @{ Case = "the attempted package is the core-only one"; Mutate = { param($case) $case.Refusal.PackageSha256 = "c1" * 32 }; Expected = "the attempted package '$("c1" * 32)' is not the default package" }
        @{ Case = "both packages declare one hash"; Mutate = { param($case) $case.Default.RestoreManifestEffectiveSchemaHash = "c0" * 32 }; Expected = "the default and core-only packages declare the same effective schema hash, so the refusal cannot show the selections differ" }
        @{ Case = "a package hash is malformed"; Mutate = { param($case) $case.Selected.RestoreManifestEffectiveSchemaHash = "not-a-hash" }; Expected = "the default and core-only packages' effective schema hashes are not both 64 lowercase hex" }
        @{ Case = "a container existed before"; Mutate = { param($case) $case.Refusal.Before.Containers = @("dms-postgresql") }; Expected = "containers existed before the attempt: [dms-postgresql]" }
        @{ Case = "a container exists after"; Mutate = { param($case) $case.Refusal.After.Containers = @("dms-postgresql") }; Expected = "containers existed after the attempt: [dms-postgresql]" }
        @{ Case = "a volume appeared"; Mutate = { param($case) $case.Refusal.After.Volumes = @("dms-published_dms-mssql-2025", "dms-published_dms-postgresql") }; Expected = "the project's volumes changed across the attempt: before [dms-published_dms-mssql-2025], after [dms-published_dms-mssql-2025, dms-published_dms-postgresql]" }
        @{ Case = "a volume disappeared"; Mutate = { param($case) $case.Refusal.After.Volumes = @() }; Expected = "the project's volumes changed across the attempt: before [dms-published_dms-mssql-2025], after []" }
        @{ Case = "a workspace existed before"; Mutate = { param($case) $case.Refusal.Before.WorkspacePresent = $true }; Expected = "an active .bootstrap workspace existed before the attempt (or was not checked)" }
        @{ Case = "a workspace exists after"; Mutate = { param($case) $case.Refusal.After.WorkspacePresent = $true }; Expected = "an active .bootstrap workspace existed after the attempt (or was not checked)" }
        @{ Case = "the workspace was not checked"; Mutate = { param($case) $case.Refusal.After.PSObject.Properties.Remove("WorkspacePresent") }; Expected = "an active .bootstrap workspace existed after the attempt (or was not checked)" }
        @{ Case = "a candidate workspace was left behind"; Mutate = { param($case) $case.Refusal.After.CandidateDirectories = @("candidate-old", "candidate-new") }; Expected = "the attempt left candidate workspaces behind: [candidate-new]" }
        @{ Case = "the state before could not be observed"; Mutate = { param($case) $case.Refusal.Before.Reason = "docker ps exited 1: Cannot connect" }; Expected = "the project state before the attempt could not be observed: docker ps exited 1: Cannot connect" }
        @{ Case = "the state after was not recorded"; Mutate = { param($case) $case.Refusal.After = $null }; Expected = "the project state after the attempt was not recorded" }
    ) {
        $refusal = New-Refusal
        $defaultPackage = New-RefusalPackage "default-minimal"
        $selectedPackage = New-RefusalPackage "core-only-minimal"
        & $Mutate ([pscustomobject]@{ Refusal = $refusal; Default = $defaultPackage; Selected = $selectedPackage })

        @(Get-RestoreSmokeSelectionRefusalDefect -Refusal $refusal -DefaultPackage $defaultPackage -SelectedPackage $selectedPackage) | Should -Be @($Expected)
    }

    It "reports a missing refusal and missing package records" {
        @(Get-RestoreSmokeSelectionRefusalDefect -Refusal $null -DefaultPackage (New-RefusalPackage "default-minimal") -SelectedPackage (New-RefusalPackage "core-only-minimal")) | Should -Be @("no refusal was recorded")
        @(Get-RestoreSmokeSelectionRefusalDefect -Refusal (New-Refusal) -DefaultPackage $null -SelectedPackage (New-RefusalPackage "core-only-minimal")) | Should -Be @("the default and the core-only package records are both needed to judge the refusal")
    }
}

Describe "Get-RestoreSmokeResultClassification for extension-selection" {
    BeforeAll {
        $script:coreHash = "c0" * 32
        $script:defaultHash = "d0" * 32
        $script:defaultSha = "d1" * 32
        $script:coreSha = "c1" * 32
        $script:corePackageId = "EdFi.DataStandard52.ApiSchema@1.0.335"
        $script:tpdmPackageId = "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"
        $script:sourceIdentity = @{ "default-minimal" = "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"; "core-only-minimal" = "7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22" }
        $script:restoredIdentity = "d1000000-0000-4000-8000-000000000007"

        function script:New-ExtensionPackage {
            param([string]$Fixture)

            $sha = if ($Fixture -eq "default-minimal") { $script:defaultSha } else { $script:coreSha }
            $projects = if ($Fixture -eq "default-minimal") { @("edfi", "tpdm") } else { @("edfi") }
            $hash = if ($Fixture -eq "default-minimal") { $script:defaultHash } else { $script:coreHash }
            return [pscustomobject]@{ PackageFixture = $Fixture; TemplateKind = "Minimal"; PackageFile = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.999.nupkg"; Sha256 = $sha; RestoreManifestProjects = $projects; RestoreManifestEffectiveSchemaHash = $hash; Verified = $true; Reason = $null }
        }

        function script:New-ExtensionBinding {
            param([string]$Fixture)

            $identity = $script:sourceIdentity[$Fixture]
            $sha = if ($Fixture -eq "default-minimal") { $script:defaultSha } else { $script:coreSha }
            return [pscustomobject]@{
                PackageFixture = $Fixture
                TemplateKind   = "Minimal"
                SourceDatabase = "edfi_datamanagementservice"
                BeforeBackup   = [pscustomobject]@{ ExitCode = 0; Rows = @($identity); Identity = $identity; Reason = $null }
                AfterBackup    = [pscustomobject]@{ ExitCode = 0; Rows = @($identity); Identity = $identity; Reason = $null }
                PackageFile    = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.999.nupkg"
                PackageSha256  = $sha
                Bound          = $true
                Reason         = $null
            }
        }

        function script:New-ExtensionProvenance {
            $identity = $script:sourceIdentity["core-only-minimal"]
            $observations = [System.Collections.Generic.List[object]]::new()
            $observations.Add([pscustomobject]@{
                    Label                   = "leg-extension-selection"
                    Reason                  = $null
                    Services                = [ordered]@{
                        dms    = [pscustomobject]@{ Container = "ed-fi-api"; ImageId = "sha256:dms"; ImageRef = "edfialliance/ed-fi-api:dms-restore-smoke-0123456789ab"; RepoDigests = @(); RepoDigestsReason = $null }
                        config = [pscustomobject]@{ Container = "dms-config-service"; ImageId = "sha256:config"; ImageRef = "local/ed-fi-api-configuration-service:dms-restore-smoke-0123456789ab"; RepoDigests = @(); RepoDigestsReason = $null }
                        db     = [pscustomobject]@{ Container = "dms-postgresql"; ImageId = "sha256:pg"; ImageRef = "postgres:16"; RepoDigests = @("postgres@sha256:" + ("a" * 64)); RepoDigestsReason = $null }
                    }
                    StackStart              = "stack-start#1"
                    StagedSelection         = [pscustomobject]@{ Source = "bootstrap-manifest.json schema.selectedPackages"; ManifestSha256 = ("e1" * 32); ManifestLastWriteTimeUtc = "2026-10-08T11:01:00.0000000Z"; StagedPackages = @($script:corePackageId); Reason = $null }
                })
            $stackStarts = [System.Collections.Generic.List[object]]::new()
            $stackStarts.Add([pscustomobject]@{ StackStart = "stack-start#1"; Label = "leg-extension-selection"; Restore = $true; Selection = "core-only"; EnvironmentFile = ".env.smoke-core-only"; RequestedPackages = @($script:corePackageId); RequestedReason = $null; WorkspaceBefore = [pscustomobject]@{ Present = $false; Sha256 = $null; LastWriteTimeUtc = $null; Reason = "the active workspace has no bootstrap-manifest.json" }; StartedUtc = "2026-10-08T11:00:00.0000000Z" })
            $apiReads = [System.Collections.Generic.List[object]]::new()
            $apiReads.Add([pscustomobject]@{ Mode = "seeded"; DataStoreId = 1; Reads = @([pscustomobject]@{ Resource = "academicSubjectDescriptors"; StatusCode = 200; Count = 5 }); RestoreExecution = "extension-selection#1"; Label = "leg-extension-selection" })
            $restored = [System.Collections.Generic.List[object]]::new()
            $restored.Add([pscustomobject]@{ RestoreExecution = "extension-selection#1"; PackageFixture = "core-only-minimal"; TemplateKind = "Minimal"; PackageSha256 = $script:coreSha; ExitCode = 0; Rows = @($script:restoredIdentity); Identity = $script:restoredIdentity; Reason = $null })
            $refusalState = { [pscustomobject]@{ ComposeProject = "dms-published"; Containers = @(); Volumes = @(); WorkspacePresent = $false; CandidateDirectories = @(); Reason = $null } }
            return [ordered]@{
                Legs                   = @("extension-selection")
                ExploratoryPackage     = $false
                SourceAtStart          = [pscustomobject]@{ Revision = ("1" * 40); Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
                SourceAtEnd            = [pscustomobject]@{ Revision = ("1" * 40); Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
                SchemaTools            = [pscustomobject]@{ Verified = $true; Reason = $null; Sha256AtBuild = "aa"; Sha256AtEnd = "aa" }
                Images                 = [ordered]@{
                    Dms    = [ordered]@{ ImageId = "sha256:dms"; Verified = $true; Reason = $null }
                    Config = [ordered]@{ ImageId = "sha256:config"; Verified = $true; Reason = $null }
                }
                StackStarts            = $stackStarts
                StackObservations      = $observations
                Packages               = @((New-ExtensionPackage "default-minimal"), (New-ExtensionPackage "core-only-minimal"))
                ApiReads               = $apiReads
                SourceIdentityBindings = @((New-ExtensionBinding "default-minimal"), (New-ExtensionBinding "core-only-minimal"))
                PackageInspections     = @([pscustomobject]@{ PackageFixture = "core-only-minimal"; TemplateKind = "Minimal"; PackageSha256 = $script:coreSha; InspectionDatabase = "restore_smoke_inspect_0123456789ab"; Rows = @($identity); Identity = $identity; Reason = $null; Cleanup = [pscustomobject]@{ Complete = $true; Reasons = @() } })
                RestoredIdentities     = $restored
                SelectionEnvironments  = @([pscustomobject]@{ Selection = "core-only"; FileName = ".env.smoke-core-only"; SelectedPackages = @($script:corePackageId); RemovedPackages = @($script:tpdmPackageId) })
                SelectionProofs        = @([pscustomobject]@{
                        RestoreExecution = "extension-selection#1"
                        PackageFixture   = "core-only-minimal"
                        PackageSha256    = $script:coreSha
                        Workspace        = [pscustomobject]@{ SelectedPackages = @($script:corePackageId); SelectedExtensions = @(); CoreProjectEndpointName = "ed-fi"; ProjectSchemas = @("edfi"); EffectiveSchemaHash = $script:coreHash; Reason = $null }
                        Catalog          = [pscustomobject]@{ ProjectSchemas = @("edfi"); TrackedChangesProjects = @(); EffectiveSchemaHash = $script:coreHash; Reason = $null }
                    })
                SelectionRefusals      = @([pscustomobject]@{
                        Leg           = "extension-selection"
                        PackageFixture = "default-minimal"
                        PackageSha256 = $script:defaultSha
                        Before        = (& $refusalState)
                        After         = (& $refusalState)
                        Refused       = $true
                        Message       = (Get-RestoreSmokeSelectionRefusalExpectedMessage -PackageEffectiveSchemaHash $script:defaultHash -CandidateEffectiveSchemaHash $script:coreHash)
                    })
            }
        }
    }

    It "is final with the core-only restore's selection proof, identity, and API read, and the default package's refusal" {
        $classification = Get-RestoreSmokeResultClassification -Provenance (New-ExtensionProvenance)

        @($classification.Reasons) | Should -BeNullOrEmpty
        $classification.Final | Should -BeTrue
    }

    It "is non-final with exactly the reasons when <case>" -ForEach @(
        @{ Case = "no selection proof was recorded"; Mutate = { param($p) $p.SelectionProofs = @() }; Expected = @("restore extension-selection#1: no selection proof was recorded") }
        @{ Case = "two selection proofs were recorded"; Mutate = { param($p) $p.SelectionProofs = @($p.SelectionProofs[0], $p.SelectionProofs[0]) }; Expected = @("restore extension-selection#1: 2 selection proofs were recorded; expected exactly one") }
        @{ Case = "the workspace staged TPDM too"; Mutate = { param($p) $p.SelectionProofs[0].Workspace.SelectedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335") }; Expected = @("restore extension-selection#1: the workspace staged packages [EdFi.DataStandard52.ApiSchema@1.0.335, EdFi.DataStandard52.TPDM.ApiSchema@1.0.335], but the selection env selects [EdFi.DataStandard52.ApiSchema@1.0.335]") }
        @{ Case = "the catalog holds TPDM"; Mutate = { param($p) $p.SelectionProofs[0].Catalog.ProjectSchemas = @("edfi", "tpdm") }; Expected = @("restore extension-selection#1: the target catalog holds project schemas [edfi, tpdm], expected [edfi]") }
        @{ Case = "the proof names the default package"; Mutate = { param($p) $p.SelectionProofs[0].PackageSha256 = "d1" * 32 }; Expected = @("restore extension-selection#1: the selection proof's package SHA-256 '$("d1" * 32)' is not a core-only-minimal package built in this run", "restore extension-selection#1: no package record exists for the restored package, so its restore manifest is unknown") }
        @{ Case = "a proof names a restore no leg requires"; Mutate = { param($p) $p.SelectionProofs = @($p.SelectionProofs[0], [pscustomobject]@{ RestoreExecution = "package-directory#1" }) }; Expected = @("a selection proof was recorded for restore package-directory#1, which no selected leg requires") }
        @{ Case = "the core-only env was not recorded"; Mutate = { param($p) $p.SelectionEnvironments = @() }; Expected = @("leg-extension-selection: stack start 'stack-start#1' used the 'core-only' selection, which has 0 recorded envs; expected exactly one", "restore extension-selection#1: expected one recorded core-only env, found 0", "restore extension-selection#1: the selection env records no selected packages") }
        @{ Case = "the core-only restore's stack staged the default selection"; Mutate = { param($p) $p.StackObservations[0].StagedSelection.StagedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335") }; Expected = @("leg-extension-selection: the workspace staged [EdFi.DataStandard52.ApiSchema@1.0.335, EdFi.DataStandard52.TPDM.ApiSchema@1.0.335], but stack start 'stack-start#1' used the core-only selection [EdFi.DataStandard52.ApiSchema@1.0.335]") }
        @{ Case = "the core-only restore's observation is bound to the default source build's start"; Mutate = { param($p) $p.StackStarts.Add([pscustomobject]@{ StackStart = "stack-start#0"; Label = "build-source-datastore-default-minimal"; Restore = $false; Selection = "default"; WorkspaceBefore = $null; StartedUtc = "2026-10-08T10:00:00.0000000Z" }); $p.StackObservations[0].StackStart = "stack-start#0" }; Expected = @("leg-extension-selection: stack start 'stack-start#0' belongs to step 'build-source-datastore-default-minimal'") }
        @{ Case = "the core-only env removed nothing"; Mutate = { param($p) $p.SelectionEnvironments[0].RemovedPackages = @() }; Expected = @("restore extension-selection#1: the core-only env removed no package, so it does not differ from the default selection") }
        @{ Case = "no refusal was recorded"; Mutate = { param($p) $p.SelectionRefusals = @() }; Expected = @("leg extension-selection: no refusal of the default package under the core-only env was recorded") }
        @{ Case = "two refusals were recorded"; Mutate = { param($p) $p.SelectionRefusals = @($p.SelectionRefusals[0], $p.SelectionRefusals[0]) }; Expected = @("leg extension-selection: 2 refusals were recorded; expected exactly one") }
        @{ Case = "the default package was not refused"; Mutate = { param($p) $p.SelectionRefusals[0].Refused = $false }; Expected = @("leg extension-selection: the default package was not refused under the core-only env") }
        @{ Case = "a container started during the refusal"; Mutate = { param($p) $p.SelectionRefusals[0].After.Containers = @("dms-postgresql") }; Expected = @("leg extension-selection: containers existed after the attempt: [dms-postgresql]") }
        @{ Case = "the restore used the default package"; Mutate = { param($p) $p.RestoredIdentities[0].PackageSha256 = "d1" * 32 }; Expected = @("default-minimal package $("d1" * 32): it was not independently inspected", "restore extension-selection#1: it restored the default-minimal package; the leg restores core-only-minimal") }
        @{ Case = "the restore left no API read"; Mutate = { param($p) $p.ApiReads.Clear() }; Expected = @("restore extension-selection#1: no served-data API read was recorded") }
        @{ Case = "the restore left no identity"; Mutate = { param($p) $p.RestoredIdentities.Clear() }; Expected = @("restore extension-selection#1: no restored SourceIdentity was recorded") }
        @{ Case = "the core-only capture is filed under the default fixture"; Mutate = { param($p) $p.SourceIdentityBindings[1].PackageFixture = "default-minimal" }; Expected = @("core-only-minimal package $("c1" * 32): the bound capture is for a default-minimal package") }
        @{ Case = "two packages were recorded for one fixture"; Mutate = { param($p) $p.Packages[0].PackageFixture = "core-only-minimal" }; Expected = @("2 packages were recorded for fixture core-only-minimal; expected one", "core-only-minimal package $("d1" * 32): the bound capture is for a default-minimal package", "leg extension-selection: the default and the core-only package records are both needed to judge the refusal") }
        @{ Case = "a package names an unknown fixture"; Mutate = { param($p) $p.Packages[0].PackageFixture = "minimal" }; Expected = @("package EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.999.nupkg: 'minimal' is not a known package fixture", "minimal package $("d1" * 32): the bound capture is for a default-minimal package", "leg extension-selection: the default and the core-only package records are both needed to judge the refusal") }
        @{ Case = "a package's kind disagrees with its fixture"; Mutate = { param($p) $p.Packages[1].TemplateKind = "Populated" }; Expected = @("package (core-only-minimal): it is recorded as a Populated package, but the fixture is Minimal") }
    ) {
        $provenance = New-ExtensionProvenance
        & $Mutate $provenance

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be $Expected
    }

    It "reports a refusal or a selection proof recorded when extension-selection is not selected" {
        $provenance = New-ExtensionProvenance
        $provenance.Legs = @("tampered-package")
        $provenance.RestoredIdentities.Clear()
        $provenance.ApiReads.Clear()
        $provenance.PackageInspections = @()

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        @($classification.Reasons) | Should -Be @(
            "a selection proof was recorded for restore extension-selection#1, which no selected leg requires"
            "a selection refusal was recorded for leg 'extension-selection', which no selected leg requires"
        )
    }
}

Describe "Get-RestoreSmokeResultClassification" {
    BeforeAll {
        $script:startRevision = "1111111111111111111111111111111111111111"
        $script:otherRevision = "2222222222222222222222222222222222222222"
        $script:engineDigest = "postgres@sha256:" + ("a" * 64)
        $script:minimalSha = "a1" * 32
        $script:populatedSha = "b2" * 32
        $script:otherSha = "ff" * 32
        $script:inspectionDatabase = "restore_smoke_inspect_0123456789ab"
        $script:packageIdentity = @{ Minimal = "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"; Populated = "7a2d2d44-1e5f-4e1a-8c0f-5a6c9b4e3d22" }
        $script:restoredIdentity = @{
            "package-directory#1" = "d1000000-0000-4000-8000-000000000001"
            "package-directory#2" = "d1000000-0000-4000-8000-000000000002"
            "separate-config#1"   = "d1000000-0000-4000-8000-000000000003"
            "populated#1"         = "d1000000-0000-4000-8000-000000000005"
            "tampered-package#1"  = "d1000000-0000-4000-8000-000000000009"
        }

        # Expected reasons are written with placeholders, because -ForEach data is built before
        # this block runs.
        function script:Expand-FixtureText {
            param([string[]]$Text)

            foreach ($line in $Text) {
                $line.Replace("{minimalSha}", $script:minimalSha).Replace("{populatedSha}", $script:populatedSha).Replace("{otherSha}", $script:otherSha).Replace("{db}", $script:inspectionDatabase).Replace("{minimalId}", $script:packageIdentity.Minimal).Replace("{populatedId}", $script:packageIdentity.Populated).Replace("{pd1}", $script:restoredIdentity["package-directory#1"]).Replace("{pd2}", $script:restoredIdentity["package-directory#2"])
            }
        }

        function script:Get-FixtureName {
            param([string]$TemplateKind)

            if ($TemplateKind -eq "Populated") {
                return "default-populated"
            }
            return "default-minimal"
        }

        function script:Get-FixtureSha {
            param([string]$TemplateKind)

            if ($TemplateKind -eq "Populated") {
                return $script:populatedSha
            }
            return $script:minimalSha
        }

        # The records the smoke writes for one package of a kind: Get-RestoreSmokePackageProvenance,
        # Invoke-RestoreSmokeIdentityBoundPackageBuild, Invoke-RestoreSmokePackageInspection, and
        # Assert-RestoredSourceIdentity.
        function script:New-CompletePackage {
            param([string]$TemplateKind = "Minimal")

            return [pscustomobject]@{ PackageFixture = (Get-FixtureName $TemplateKind); TemplateKind = $TemplateKind; PackageFile = "EdFi.Api.$TemplateKind.Template.PostgreSql.5.2.0.1.0.999.nupkg"; Sha256 = (Get-FixtureSha $TemplateKind); Verified = $true; Reason = $null }
        }

        function script:New-CompleteBinding {
            param([string]$TemplateKind = "Minimal")

            $identity = $script:packageIdentity[$TemplateKind]
            return [pscustomobject]@{
                PackageFixture = (Get-FixtureName $TemplateKind)
                TemplateKind   = $TemplateKind
                SourceDatabase = "edfi_datamanagementservice"
                BeforeBackup   = [pscustomobject]@{ DatabaseName = "edfi_datamanagementservice"; ExitCode = 0; Rows = @($identity); Identity = $identity; Reason = $null }
                AfterBackup    = [pscustomobject]@{ DatabaseName = "edfi_datamanagementservice"; ExitCode = 0; Rows = @($identity); Identity = $identity; Reason = $null }
                PackageFile    = "EdFi.Api.$TemplateKind.Template.PostgreSql.5.2.0.1.0.999.nupkg"
                PackageSha256  = (Get-FixtureSha $TemplateKind)
                Bound          = $true
                Reason         = $null
            }
        }

        function script:New-CompleteInspection {
            param([string]$TemplateKind = "Minimal")

            $identity = $script:packageIdentity[$TemplateKind]
            return [pscustomobject]@{
                TemplateKind       = $TemplateKind
                DatabaseEngine     = "postgresql"
                PackageFile        = "EdFi.Api.$TemplateKind.Template.PostgreSql.5.2.0.1.0.999.nupkg"
                PackageSha256      = (Get-FixtureSha $TemplateKind)
                ArtifactFileName   = "EdFi.Api.$TemplateKind.Template.PostgreSql.sql"
                ArtifactSha256     = ("c3" * 32)
                InspectionDatabase = $script:inspectionDatabase
                Rows               = @($identity)
                Identity           = $identity
                Reason             = $null
                Cleanup            = [pscustomobject]@{ DatabaseOwned = $true; DatabaseDropped = $true; DatabaseAbsent = $true; ContainerFile = "/tmp/restore-smoke-inspect-0123456789ab.sql"; ContainerFileRemoved = $true; LocalDirectoryRemoved = $true; Complete = $true; Reasons = @() }
            }
        }

        function script:New-CompleteRestoredIdentity {
            param(
                [string]$RestoreExecution,

                [string]$TemplateKind = "Minimal",

                [string]$Identity
            )

            if ([string]::IsNullOrEmpty($Identity)) {
                $Identity = $script:restoredIdentity[$RestoreExecution]
            }
            return [pscustomobject]@{
                RestoreExecution = $RestoreExecution
                PackageFixture   = (Get-FixtureName $TemplateKind)
                TemplateKind     = $TemplateKind
                Label            = "leg-" + ($RestoreExecution -replace '#\d+$', '')
                TargetDatabase   = "edfi_datamanagementservice"
                PackageFile      = "EdFi.Api.$TemplateKind.Template.PostgreSql.5.2.0.1.0.999.nupkg"
                PackageSha256    = (Get-FixtureSha $TemplateKind)
                ExitCode         = 0
                Rows             = @($Identity)
                Identity         = $Identity
                Reason           = $null
            }
        }

        # The stack start Invoke-RestoreWrapper records: a default-selection restore into an empty
        # workspace, begun at -StartedUtc.
        function script:New-CompleteStackStart {
            param([string]$Id, [string]$Label, [string]$StartedUtc)

            return [pscustomobject]@{
                StackStart        = $Id
                Label             = $Label
                Restore           = $true
                Selection         = "default"
                EnvironmentFile   = ".env.smoke-images"
                RequestedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335")
                RequestedReason   = $null
                WorkspaceBefore   = [pscustomobject]@{ Present = $false; Sha256 = $null; LastWriteTimeUtc = $null; Reason = "the active workspace has no bootstrap-manifest.json" }
                StartedUtc        = $StartedUtc
            }
        }

        function script:New-CompleteObservation {
            param([string]$Label, [string]$StackStart, [string]$WrittenUtc)

            return [pscustomobject]@{
                Label                   = $Label
                Reason                  = $null
                Services                = [ordered]@{
                    dms    = [pscustomobject]@{ Container = "ed-fi-api"; ImageId = "sha256:dms"; ImageRef = "local/ed-fi-api:dms-restore-smoke-0123456789ab"; RepoDigests = @(); RepoDigestsReason = $null }
                    config = [pscustomobject]@{ Container = "dms-config-service"; ImageId = "sha256:config"; ImageRef = "local/ed-fi-api-configuration-service:dms-restore-smoke-0123456789ab"; RepoDigests = @(); RepoDigestsReason = $null }
                    db     = [pscustomobject]@{ Container = "dms-postgresql"; ImageId = "sha256:pg"; ImageRef = "postgres:16"; RepoDigests = @($script:engineDigest); RepoDigestsReason = $null }
                }
                StackStart              = $StackStart
                StagedSelection         = [pscustomobject]@{ Source = "bootstrap-manifest.json schema.selectedPackages"; ManifestSha256 = ("e1" * 32); ManifestLastWriteTimeUtc = $WrittenUtc; StagedPackages = @("EdFi.DataStandard52.ApiSchema@1.0.335", "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"); Reason = $null }
            }
        }

        # The record Test-RestoreSmokeApiRead returns, tagged as Assert-RestoredDatastore tags it.
        function script:New-CompleteApiRead {
            param(
                [string]$RestoreExecution,

                [ValidateSet("Minimal", "Populated")]
                [string]$TemplateKind = "Minimal"
            )

            $reads = [System.Collections.Generic.List[object]]::new()
            $reads.Add([pscustomobject]@{ Resource = "academicSubjectDescriptors"; Uri = "http://localhost:8080/data/ed-fi/academicSubjectDescriptors?limit=5"; StatusCode = 200; Count = 5 })
            $mode = "seeded"
            if ($TemplateKind -eq "Populated") {
                $reads.Add([pscustomobject]@{ Resource = "schools"; Uri = "http://localhost:8080/data/ed-fi/schools?limit=5"; StatusCode = 200; Count = 2 })
                $mode = "populated"
            }
            return [pscustomobject]@{
                Mode                = $mode
                CmsUrl              = "http://localhost:8081"
                DmsUrl              = "http://localhost:8080"
                DataStoreId         = 1
                DataStoreName       = "Local Development Data Store"
                DataStoreCandidates = @()
                StackGeneration     = 4
                TokenReused         = $false
                TokenRequestCount   = 1
                Reads               = $reads
                RestoreExecution    = $RestoreExecution
                Label               = "leg-" + ($RestoreExecution -replace '#\d+$', '')
            }
        }

        # A complete run: every final-evidence field present, two stack observations, and for each
        # of the three restores its legs perform (package-directory twice, separate-config once)
        # one complete served-data record and one restored identity, beside the Minimal package's
        # bound pre-backup capture and its independent inspection. Records are listed in the
        # order package-directory#1, separate-config#1, package-directory#2.
        function script:New-CompleteProvenance {
            $observations = [System.Collections.Generic.List[object]]::new()
            $observations.Add((New-CompleteObservation -Label "leg-package-directory" -StackStart "stack-start#1" -WrittenUtc "2026-10-08T10:01:00.0000000Z"))
            $observations.Add((New-CompleteObservation -Label "leg-separate-config" -StackStart "stack-start#2" -WrittenUtc "2026-10-08T11:01:00.0000000Z"))
            $stackStarts = [System.Collections.Generic.List[object]]::new()
            $stackStarts.Add((New-CompleteStackStart -Id "stack-start#1" -Label "leg-package-directory" -StartedUtc "2026-10-08T10:00:00.0000000Z"))
            $stackStarts.Add((New-CompleteStackStart -Id "stack-start#2" -Label "leg-separate-config" -StartedUtc "2026-10-08T11:00:00.0000000Z"))
            $apiReads = [System.Collections.Generic.List[object]]::new()
            $apiReads.Add((New-CompleteApiRead -RestoreExecution "package-directory#1"))
            $apiReads.Add((New-CompleteApiRead -RestoreExecution "separate-config#1"))
            $apiReads.Add((New-CompleteApiRead -RestoreExecution "package-directory#2"))
            $restored = [System.Collections.Generic.List[object]]::new()
            $restored.Add((New-CompleteRestoredIdentity -RestoreExecution "package-directory#1"))
            $restored.Add((New-CompleteRestoredIdentity -RestoreExecution "separate-config#1"))
            $restored.Add((New-CompleteRestoredIdentity -RestoreExecution "package-directory#2"))
            return [ordered]@{
                Legs                 = @("package-directory", "separate-config", "tampered-package")
                ApiReads             = $apiReads
                ExploratoryPackage   = $false
                SourceAtStart        = [pscustomobject]@{ Revision = $script:startRevision; Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
                SourceAtEnd          = [pscustomobject]@{ Revision = $script:startRevision; Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
                SchemaTools          = [pscustomobject]@{ Verified = $true; Reason = $null; Sha256AtBuild = "aa"; Sha256AtEnd = "aa" }
                Images               = [ordered]@{
                    Dms    = [ordered]@{ ImageId = "sha256:dms"; Verified = $true; Reason = $null }
                    Config = [ordered]@{ ImageId = "sha256:config"; Verified = $true; Reason = $null }
                }
                StackStarts          = $stackStarts
                StackObservations    = $observations
                Packages             = @(New-CompletePackage)
                SourceIdentityBindings = @(New-CompleteBinding)
                PackageInspections   = @(New-CompleteInspection)
                RestoredIdentities   = $restored
            }
        }

        # The smoke's ForeignStack record after a confirmed removal that left one allowed volume.
        function script:New-AllowedLeftoverForeignStack {
            param([AllowNull()] [object[]]$EndStates)

            $allowed = [pscustomobject]@{ Project = "dms-local"; Name = "dms-local_dms-mssql-2025"; Decision = "allowed"; CreatedAt = "2026-10-01T00:00:00Z"; Reason = "declared in mssql.yml as mssql storage" }
            return [ordered]@{
                RemovalConfirmed    = $true
                LeftoverVolumes     = [pscustomobject]@{ DatabaseEngine = "postgresql"; Volumes = @($allowed); Allowed = @($allowed); Blocked = @() }
                AllowedVolumesAtEnd = $EndStates
            }
        }
    }

    It "classifies a complete run with two stack observations as final" {
        $classification = Get-RestoreSmokeResultClassification -Provenance (New-CompleteProvenance)

        $classification.Final | Should -BeTrue
        $classification.Reasons | Should -BeNullOrEmpty
    }

    It "stays final when every allowed leftover volume was preserved, and when blocked ones were recorded" {
        $provenance = New-CompleteProvenance
        $provenance.ForeignStack = New-AllowedLeftoverForeignStack -EndStates @([pscustomobject]@{ Name = "dms-local_dms-mssql-2025"; Present = $true; Preserved = $true; Reason = $null })

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeTrue
        $classification.Reasons | Should -BeNullOrEmpty
    }

    It "is non-final with exactly the expected reason when <case>" -ForEach @(
        # Pre-existing conditions (2.1 and 2.1b).
        @{ Case = "the run is exploratory"; Mutate = { param($p) $p.ExploratoryPackage = $true }; Expected = "-ExploratoryPackage marks this run exploratory" }
        @{ Case = "the worktree was dirty at start"; Mutate = { param($p) $p.SourceAtStart.Clean = $false; $p.SourceAtStart.Porcelain = @(" M file") }; Expected = "SourceAtStart worktree is dirty (1 porcelain entries)" }
        @{ Case = "the worktree became dirty by the end"; Mutate = { param($p) $p.SourceAtEnd.Clean = $false; $p.SourceAtEnd.Porcelain = @(" M packages.lock.json") }; Expected = "SourceAtEnd worktree is dirty (1 porcelain entries)" }
        @{ Case = "the start revision could not be observed"; Mutate = { param($p) $p.SourceAtStart = [pscustomobject]@{ Revision = $null; Observed = $false; Clean = $false; Porcelain = @(); Reason = "git rev-parse exit 128" } }; Expected = "SourceAtStart not observed: git rev-parse exit 128" }
        @{ Case = "SchemaTools was never built"; Mutate = { param($p) $p.SchemaTools = $null }; Expected = "SchemaTools was not built in this run" }
        @{ Case = "the SchemaTools resolution did not match"; Mutate = { param($p) $p.SchemaTools.Verified = $false; $p.SchemaTools.Reason = "the bootstrap resolver selects 'x'" }; Expected = "SchemaTools not verified: the bootstrap resolver selects 'x'" }
        @{ Case = "the SchemaTools executable changed during the run"; Mutate = { param($p) $p.SchemaTools.Sha256AtEnd = "bb" }; Expected = "the SchemaTools executable changed during the run (or was not re-hashed at the end)" }
        @{ Case = "images were never built"; Mutate = { param($p) $p.Images = $null }; Expected = "images were not built in this run" }
        @{ Case = "no started stack was observed"; Mutate = { param($p) $p.StackObservations.Clear() }; Expected = "no started stack was observed, so the images actually used are unknown" }
        @{ Case = "the second observation ran a different DMS image (for example the stale shared tag's)"; Mutate = { param($p) $p.StackObservations[1].Services["dms"] = [pscustomobject]@{ ImageId = "sha256:stale" } }; Expected = "leg-separate-config: dms runs image sha256:stale, not the in-run build (sha256:dms)" }
        @{ Case = "the second observation ran a different config image"; Mutate = { param($p) $p.StackObservations[1].Services["config"] = [pscustomobject]@{ ImageId = "sha256:stale" } }; Expected = "leg-separate-config: config runs image sha256:stale, not the in-run build (sha256:config)" }
        @{ Case = "the second observation has no config container"; Mutate = { param($p) $p.StackObservations[1].Services.Remove("config") }; Expected = "leg-separate-config: no config container observed" }
        @{ Case = "the package was not verified"; Mutate = { param($p) $p.Packages[0].Verified = $false; $p.Packages[0].Reason = "sha mismatch" }; Expected = "package (default-minimal) not verified: sha mismatch" }

        # Revisions (2.1c).
        @{ Case = "clean start and end trees are at different revisions"; Mutate = { param($p) $p.SourceAtEnd.Revision = $script:otherRevision }; Expected = "the revision changed during the run (start 1111111111111111111111111111111111111111, end 2222222222222222222222222222222222222222)" }
        @{ Case = "the start revision is empty"; Mutate = { param($p) $p.SourceAtStart.Revision = "" }; Expected = "SourceAtStart revision is empty" }
        @{ Case = "the end revision is null"; Mutate = { param($p) $p.SourceAtEnd.Revision = $null }; Expected = "SourceAtEnd revision is empty" }
        @{ Case = "the end record has no Revision field"; Mutate = { param($p) $p.SourceAtEnd.PSObject.Properties.Remove("Revision") }; Expected = "SourceAtEnd revision is empty" }
        @{ Case = "the start record has no Observed field"; Mutate = { param($p) $p.SourceAtStart.PSObject.Properties.Remove("Observed") }; Expected = "SourceAtStart not observed: " }
        @{ Case = "the end record claims clean but lists porcelain entries"; Mutate = { param($p) $p.SourceAtEnd.Porcelain = @("?? stray.txt") }; Expected = "SourceAtEnd worktree is dirty (1 porcelain entries)" }
        @{ Case = "the end record has no Clean field"; Mutate = { param($p) $p.SourceAtEnd.PSObject.Properties.Remove("Clean") }; Expected = "SourceAtEnd worktree is dirty (0 porcelain entries)" }
        @{ Case = "the start revision was never captured"; Mutate = { param($p) $p.Remove("SourceAtStart") }; Expected = "SourceAtStart not captured" }

        # Allowed leftover volumes (2.1d).
        @{ Case = "an allowed leftover volume was gone at run end"; Mutate = { param($p) $p.ForeignStack = (New-AllowedLeftoverForeignStack -EndStates @([pscustomobject]@{ Name = "dms-local_dms-mssql-2025"; Present = $false; Preserved = $false; Reason = "inspection failed (docker exit 1: no such volume)" })) }; Expected = "leftover volume 'dms-local_dms-mssql-2025' was allowed by the preflight, but was not shown preserved at run end: inspection failed (docker exit 1: no such volume)" }
        @{ Case = "no run-end check was recorded for an allowed leftover volume"; Mutate = { param($p) $p.ForeignStack = (New-AllowedLeftoverForeignStack -EndStates $null) }; Expected = "leftover volume 'dms-local_dms-mssql-2025' was allowed by the preflight, but no run-end check was recorded" }
        @{ Case = "the run-end check covered a different volume"; Mutate = { param($p) $p.ForeignStack = (New-AllowedLeftoverForeignStack -EndStates @([pscustomobject]@{ Name = "dms-published_dms-mssql-2025"; Present = $true; Preserved = $true; Reason = $null })) }; Expected = "leftover volume 'dms-local_dms-mssql-2025' was allowed by the preflight, but no run-end check was recorded" }

        # Staged selection bound to its stack start, only the second observation lacking it (2.4a).
        @{ Case = "the second observation has no staged selection"; Mutate = { param($p) $p.StackObservations[1].PSObject.Properties.Remove("StagedSelection") }; Expected = "leg-separate-config: the staged schema selection was not observed" }
        @{ Case = "the second observation's workspace had no manifest"; Mutate = { param($p) $p.StackObservations[1].StagedSelection.StagedPackages = $null; $p.StackObservations[1].StagedSelection.Reason = "the active workspace has no bootstrap-manifest.json" }; Expected = "leg-separate-config: the staged schema selection was not observed: the active workspace has no bootstrap-manifest.json" }
        @{ Case = "the second observation's workspace staged no package"; Mutate = { param($p) $p.StackObservations[1].StagedSelection.StagedPackages = @() }; Expected = "leg-separate-config: the workspace manifest records no staged package" }
        @{ Case = "the second observation names no stack start"; Mutate = { param($p) $p.StackObservations[1].StackStart = $null }; Expected = "leg-separate-config: the observation names no stack start, so its staged selection is not bound to the stack it observed" }
        @{ Case = "the second observation's manifest predates its stack start"; Mutate = { param($p) $p.StackObservations[1].StagedSelection.ManifestLastWriteTimeUtc = "2026-10-08T10:01:00.0000000Z" }; Expected = "leg-separate-config: the workspace manifest was written at 2026-10-08T10:01:00.0000000Z, before stack start 'stack-start#2' began at 2026-10-08T11:00:00.0000000Z, so its selection is not this stack's" }
        @{ Case = "the stack starts were not recorded"; Mutate = { param($p) $p.Remove("StackStarts") }; Expected = @("leg-package-directory: 0 recorded stack starts are named 'stack-start#1'; expected exactly one", "leg-separate-config: 0 recorded stack starts are named 'stack-start#2'; expected exactly one") }
        @{ Case = "the second observation is bound to the first stack start"; Mutate = { param($p) $p.StackObservations[1].StackStart = "stack-start#1" }; Expected = @("leg-separate-config: stack start 'stack-start#1' belongs to step 'leg-package-directory'", "stack start 'stack-start#1' is named by 2 observations; each started stack is observed once") }

        # Served-data evidence per restore (2.2a).
        @{ Case = "only the first of two restore executions has a served-data record"; Mutate = { param($p) $p.ApiReads.RemoveAt(1) }; Expected = "restore separate-config#1: no served-data API read was recorded" }
        @{ Case = "only the second of two restore executions has a served-data record"; Mutate = { param($p) $p.ApiReads.RemoveAt(0) }; Expected = "restore package-directory#1: no served-data API read was recorded" }
        @{ Case = "the first restore's read was schema-only"; Mutate = { param($p) $p.ApiReads[0].Mode = "schema-only"; $p.ApiReads[0].Reads[0].Count = 0 }; Expected = "restore package-directory#1: the API read was schema-only (unseeded source), which cannot prove seeded served data" }
        @{ Case = "the second restore's descriptor read was not HTTP 200"; Mutate = { param($p) $p.ApiReads[1].Reads[0].StatusCode = 401 }; Expected = "restore separate-config#1: the academicSubjectDescriptors read returned HTTP 401, not 200" }
        @{ Case = "the second restore's descriptor read returned no rows"; Mutate = { param($p) $p.ApiReads[1].Reads[0].Count = 0 }; Expected = "restore separate-config#1: the academicSubjectDescriptors read returned no rows" }
        @{ Case = "the second restore's descriptor read has no row count"; Mutate = { param($p) $p.ApiReads[1].Reads[0].PSObject.Properties.Remove("Count") }; Expected = "restore separate-config#1: the academicSubjectDescriptors read recorded no row count" }
        @{ Case = "the second restore's record has no reads"; Mutate = { param($p) $p.ApiReads[1].Reads.Clear() }; Expected = "restore separate-config#1: no academicSubjectDescriptors read was recorded" }
        @{ Case = "the second restore's record has no Reads field"; Mutate = { param($p) $p.ApiReads[1].PSObject.Properties.Remove("Reads") }; Expected = "restore separate-config#1: no academicSubjectDescriptors read was recorded" }
        @{ Case = "the second restore's descriptor read was recorded twice"; Mutate = { param($p) $p.ApiReads[1].Reads.Add($p.ApiReads[1].Reads[0]) }; Expected = "restore separate-config#1: 2 academicSubjectDescriptors reads were recorded; expected exactly one" }
        @{ Case = "the second restore's record names no data store"; Mutate = { param($p) $p.ApiReads[1].DataStoreId = $null }; Expected = "restore separate-config#1: the API read names no data store" }
        @{ Case = "a Minimal restore's read is labelled populated"; Mutate = { param($p) $p.ApiReads[1].Mode = "populated" }; Expected = "restore separate-config#1: the API read mode is 'populated', expected 'seeded' for a Minimal restore" }
        @{ Case = "the second restore's record has no mode"; Mutate = { param($p) $p.ApiReads[1].PSObject.Properties.Remove("Mode") }; Expected = "restore separate-config#1: the API read mode is '', expected 'seeded' for a Minimal restore" }
        @{ Case = "an extra record names no restore execution"; Mutate = { param($p) $extra = New-CompleteApiRead -RestoreExecution ""; $p.ApiReads.Add($extra) }; Expected = "a served-data API read names no restore execution" }
        @{ Case = "an extra record belongs to a negative leg"; Mutate = { param($p) $p.ApiReads.Add((New-CompleteApiRead -RestoreExecution "tampered-package#1")) }; Expected = "a served-data API read was recorded for restore tampered-package#1, which no selected leg performs" }
        @{ Case = "an extra record belongs to a leg that was not selected"; Mutate = { param($p) $p.ApiReads.Add((New-CompleteApiRead -RestoreExecution "directory-feed#1")) }; Expected = "a served-data API read was recorded for restore directory-feed#1, which no selected leg performs" }
        @{ Case = "a selected leg has no defined requirement"; Mutate = { param($p) $p.Legs = @($p.Legs) + "mystery-leg" }; Expected = "leg 'mystery-leg' has no defined served-data requirement" }
        @{ Case = "the selected legs were not recorded"; Mutate = { param($p) $p.Remove("Legs") }; Expected = "the selected legs were not recorded, so the required served-data API reads and restored SourceIdentity records are unknown" }

        # Engine repository digest, only the second observation lacking it (2.1c).
        @{ Case = "the second observation's engine image has no digest, with the observed reason"; Mutate = { param($p) $p.StackObservations[1].Services["db"].RepoDigests = @(); $p.StackObservations[1].Services["db"].RepoDigestsReason = "the image has no repository digest (locally built or untagged)" }; Expected = "leg-separate-config: the db image sha256:pg has no observed repository digest: the image has no repository digest (locally built or untagged)" }
        @{ Case = "the second observation's engine record has no RepoDigests field"; Mutate = { param($p) $p.StackObservations[1].Services["db"].PSObject.Properties.Remove("RepoDigests") }; Expected = "leg-separate-config: the db image sha256:pg has no observed repository digest" }
        @{ Case = "the second observation's engine digest is only an image ID"; Mutate = { param($p) $p.StackObservations[1].Services["db"].RepoDigests = @("sha256:" + ("a" * 64)) }; Expected = "leg-separate-config: the db image sha256:pg has no observed repository digest" }
        @{ Case = "the second observation's engine digest is malformed"; Mutate = { param($p) $p.StackObservations[1].Services["db"].RepoDigests = @("postgres@sha256:abc") }; Expected = "leg-separate-config: the db image sha256:pg has no observed repository digest" }
        @{ Case = "the second observation has no db container"; Mutate = { param($p) $p.StackObservations[1].Services.Remove("db") }; Expected = "leg-separate-config: no db container observed" }
    ) {
        $provenance = New-CompleteProvenance
        & $Mutate $provenance

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be @($Expected)
    }

    It "is non-final, naming the build and every observation, when <case>" -ForEach @(
        @{ Case = "the DMS build recorded an ID but was not verified"; Mutate = { param($p) $p.Images.Dms.Verified = $false; $p.Images.Dms.Reason = "the run tag resolves elsewhere" }; Expected = @("Dms image not verified as built in this run: the run tag resolves elsewhere", "leg-package-directory: dms runs image sha256:dms, not the in-run build (none verified)", "leg-separate-config: dms runs image sha256:dms, not the in-run build (none verified)") }
        @{ Case = "the CMS build never ran"; Mutate = { param($p) $p.Images.Config = $null }; Expected = @("Config image not verified as built in this run: missing", "leg-package-directory: config runs image sha256:config, not the in-run build (none verified)", "leg-separate-config: config runs image sha256:config, not the in-run build (none verified)") }
    ) {
        $provenance = New-CompleteProvenance
        & $Mutate $provenance

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be $Expected
    }

    It "is non-final for each restore when <case>" -ForEach @(
        @{ Case = "the run recorded no served-data reads at all"; Mutate = { param($p) $p.Remove("ApiReads") }; Expected = @("restore package-directory#1: no served-data API read was recorded", "restore package-directory#2: no served-data API read was recorded", "restore separate-config#1: no served-data API read was recorded") }
        @{ Case = "the second restore's record is filed under the first restore"; Mutate = { param($p) $p.ApiReads[1].RestoreExecution = "package-directory#1" }; Expected = @("restore package-directory#1: 2 served-data API reads were recorded; expected exactly one", "restore separate-config#1: no served-data API read was recorded") }
        @{ Case = "both restores' reads were schema-only (-SkipSourceSeed)"; Mutate = { param($p) foreach ($record in $p.ApiReads) { $record.Mode = "schema-only"; $record.Reads[0].Count = 0 } }; Expected = @("restore package-directory#1: the API read was schema-only (unseeded source), which cannot prove seeded served data", "restore package-directory#2: the API read was schema-only (unseeded source), which cannot prove seeded served data", "restore separate-config#1: the API read was schema-only (unseeded source), which cannot prove seeded served data") }
    ) {
        $provenance = New-CompleteProvenance
        & $Mutate $provenance

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be $Expected
    }

    It "is non-final with exactly the SourceIdentity reasons when <case>" -ForEach @(
        # The pre-backup capture bound to the package hash.
        @{ Case = "no capture is bound to the package"; Mutate = { param($p) $p.SourceIdentityBindings = @() }; Expected = @("default-minimal package {minimalSha}: no pre-backup SourceIdentity capture is bound to it") }
        @{ Case = "two captures are bound to the package"; Mutate = { param($p) $p.SourceIdentityBindings = @($p.SourceIdentityBindings[0], (New-CompleteBinding)) }; Expected = @("default-minimal package {minimalSha}: 2 pre-backup SourceIdentity captures are bound to it; expected exactly one") }
        @{ Case = "the capture is bound to another package's hash"; Mutate = { param($p) $p.SourceIdentityBindings[0].PackageSha256 = $script:otherSha }; Expected = @("default-minimal package {minimalSha}: no pre-backup SourceIdentity capture is bound to it", "a pre-backup SourceIdentity capture (default-minimal) is bound to package SHA-256 {otherSha}, which no package built in this run has") }
        @{ Case = "a failed capture is bound to no package"; Mutate = { param($p) $p.SourceIdentityBindings = @($p.SourceIdentityBindings[0], [pscustomobject]@{ PackageFixture = "default-populated"; TemplateKind = "Populated"; PackageSha256 = $null; Bound = $false; Reason = "before the backup, the SourceIdentity query against 'edfi_datamanagementservice' exited 1: refused" }) }; Expected = @("a pre-backup SourceIdentity capture (default-populated) is bound to no package: before the backup, the SourceIdentity query against 'edfi_datamanagementservice' exited 1: refused") }
        @{ Case = "the capture is of another package fixture"; Mutate = { param($p) $p.SourceIdentityBindings[0].PackageFixture = "core-only-minimal" }; Expected = @("default-minimal package {minimalSha}: the bound capture is for a core-only-minimal package") }
        @{ Case = "the pre-backup read returned no row (its stored Identity is ignored)"; Mutate = { param($p) $p.SourceIdentityBindings[0].BeforeBackup.Rows = @() }; Expected = @("default-minimal package {minimalSha}: the pre-backup SourceIdentity has 0 rows; expected exactly one") }
        @{ Case = "the pre-backup read returned two rows"; Mutate = { param($p) $p.SourceIdentityBindings[0].BeforeBackup.Rows = @($script:packageIdentity.Minimal, $script:packageIdentity.Populated) }; Expected = @("default-minimal package {minimalSha}: the pre-backup SourceIdentity has 2 rows; expected exactly one") }
        @{ Case = "the pre-backup value is not a UUID"; Mutate = { param($p) $p.SourceIdentityBindings[0].BeforeBackup.Rows = @("not-a-uuid") }; Expected = @("default-minimal package {minimalSha}: the pre-backup SourceIdentity is not a UUID ('not-a-uuid')") }
        @{ Case = "the pre-backup value is a braced UUID"; Mutate = { param($p) $p.SourceIdentityBindings[0].BeforeBackup.Rows = @("{6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11}") }; Expected = @("default-minimal package {minimalSha}: the pre-backup SourceIdentity is not a UUID ('{6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11}')") }
        @{ Case = "the pre-backup value is the zero UUID"; Mutate = { param($p) $p.SourceIdentityBindings[0].BeforeBackup.Rows = @("00000000-0000-0000-0000-000000000000") }; Expected = @("default-minimal package {minimalSha}: the pre-backup SourceIdentity is the zero UUID") }
        @{ Case = "the pre-backup read is missing"; Mutate = { param($p) $p.SourceIdentityBindings[0].BeforeBackup = $null }; Expected = @("default-minimal package {minimalSha}: the pre-backup SourceIdentity has 0 rows; expected exactly one") }
        @{ Case = "the identity changed across the backup"; Mutate = { param($p) $p.SourceIdentityBindings[0].AfterBackup.Rows = @($script:packageIdentity.Populated) }; Expected = @("default-minimal package {minimalSha}: the source's SourceIdentity changed across the backup (before {minimalId}, after {populatedId})") }
        @{ Case = "the after-backup read is missing"; Mutate = { param($p) $p.SourceIdentityBindings[0].AfterBackup = $null }; Expected = @("default-minimal package {minimalSha}: the SourceIdentity read after the backup has 0 rows; expected exactly one") }

        # The independent inspection of the package.
        @{ Case = "the package was not inspected"; Mutate = { param($p) $p.PackageInspections = @() }; Expected = @("default-minimal package {minimalSha}: it was not independently inspected") }
        @{ Case = "the package was inspected twice"; Mutate = { param($p) $p.PackageInspections = @($p.PackageInspections[0], (New-CompleteInspection)) }; Expected = @("default-minimal package {minimalSha}: it was inspected 2 times; expected exactly once") }
        @{ Case = "the inspection failed"; Mutate = { param($p) $p.PackageInspections[0].Reason = "the replay into restore_smoke_inspect_0123456789ab exited 3: ERROR: syntax error" }; Expected = @("default-minimal package {minimalSha}: the independent inspection failed: the replay into {db} exited 3: ERROR: syntax error") }
        @{ Case = "the inspection read no row"; Mutate = { param($p) $p.PackageInspections[0].Rows = @() }; Expected = @("default-minimal package {minimalSha}: the inspected package SourceIdentity has 0 rows; expected exactly one") }
        @{ Case = "the inspection read two rows"; Mutate = { param($p) $p.PackageInspections[0].Rows = @($script:packageIdentity.Minimal, $script:packageIdentity.Minimal) }; Expected = @("default-minimal package {minimalSha}: the inspected package SourceIdentity has 2 rows; expected exactly one") }
        @{ Case = "the inspected value is not a UUID"; Mutate = { param($p) $p.PackageInspections[0].Rows = @("SourceIdentity") }; Expected = @("default-minimal package {minimalSha}: the inspected package SourceIdentity is not a UUID ('SourceIdentity')") }
        @{ Case = "the inspected value is the zero UUID"; Mutate = { param($p) $p.PackageInspections[0].Rows = @("00000000-0000-0000-0000-000000000000") }; Expected = @("default-minimal package {minimalSha}: the inspected package SourceIdentity is the zero UUID") }
        @{ Case = "the inspected identity differs from the bound capture"; Mutate = { param($p) $p.PackageInspections[0].Rows = @($script:packageIdentity.Populated) }; Expected = @("default-minimal package {minimalSha}: the inspected package SourceIdentity {populatedId} differs from the pre-backup capture {minimalId} bound to its SHA-256") }
        @{ Case = "the inspection is of another package's hash"; Mutate = { param($p) $p.PackageInspections[0].PackageSha256 = $script:otherSha }; Expected = @("package inspection {db}: it inspected package SHA-256 '{otherSha}', which no package built in this run has", "default-minimal package {minimalSha}: it was not independently inspected") }
        @{ Case = "the inspection database was not shown dropped"; Mutate = { param($p) $p.PackageInspections[0].Cleanup = [pscustomobject]@{ DatabaseOwned = $true; DatabaseDropped = $false; DatabaseAbsent = $false; ContainerFile = "/tmp/x.sql"; ContainerFileRemoved = $true; LocalDirectoryRemoved = $true; Complete = $false; Reasons = @("dropping restore_smoke_inspect_0123456789ab exited 1: ERROR: database is being accessed", "restore_smoke_inspect_0123456789ab was not shown absent after the drop") } }; Expected = @("package inspection {db}: its cleanup was not shown complete: dropping {db} exited 1: ERROR: database is being accessed; {db} was not shown absent after the drop") }
        @{ Case = "the inspection recorded no cleanup"; Mutate = { param($p) $p.PackageInspections[0].PSObject.Properties.Remove("Cleanup") }; Expected = @("package inspection {db}: its cleanup was not shown complete") }

        # The restored identity of each restore, including the repeated restore.
        @{ Case = "the repeated restore recorded no identity"; Mutate = { param($p) $p.RestoredIdentities.RemoveAt(2) }; Expected = @("restore package-directory#2: no restored SourceIdentity was recorded") }
        @{ Case = "the repeated restore left neither an API read nor an identity"; Mutate = { param($p) $p.RestoredIdentities.RemoveAt(2); $p.ApiReads.RemoveAt(2) }; Expected = @("restore package-directory#2: no served-data API read was recorded", "restore package-directory#2: no restored SourceIdentity was recorded") }
        @{ Case = "the repeated restore recorded two identities"; Mutate = { param($p) $p.RestoredIdentities.Add((New-CompleteRestoredIdentity -RestoreExecution "package-directory#2" -Identity "d1000000-0000-4000-8000-000000000008")) }; Expected = @("restore package-directory#2: 2 restored SourceIdentity records were recorded; expected exactly one") }
        @{ Case = "the repeated restore read no row"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @() }; Expected = @("restore package-directory#2: the restored SourceIdentity has 0 rows; expected exactly one") }
        @{ Case = "the repeated restore read two rows"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @($script:restoredIdentity["package-directory#2"], "d1000000-0000-4000-8000-000000000008") }; Expected = @("restore package-directory#2: the restored SourceIdentity has 2 rows; expected exactly one") }
        @{ Case = "the repeated restore's value is not a UUID"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @("NULL") }; Expected = @("restore package-directory#2: the restored SourceIdentity is not a UUID ('NULL')") }
        @{ Case = "the repeated restore's value is the zero UUID"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @("00000000-0000-0000-0000-000000000000") }; Expected = @("restore package-directory#2: the restored SourceIdentity is the zero UUID") }
        @{ Case = "the repeated restore reused the first restore's identity"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @($script:restoredIdentity["package-directory#1"].ToUpperInvariant()) }; Expected = @("restore package-directory#2: the restored SourceIdentity {pd1} equals restore package-directory#1's") }
        @{ Case = "the first restore kept the package's identity"; Mutate = { param($p) $p.RestoredIdentities[0].Rows = @($script:packageIdentity.Minimal.ToUpperInvariant()) }; Expected = @("restore package-directory#1: the restored SourceIdentity {minimalId} equals the package's inspected SourceIdentity") }
        @{ Case = "the repeated restore kept the package's identity"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @($script:packageIdentity.Minimal) }; Expected = @("restore package-directory#2: the restored SourceIdentity {minimalId} equals the package's inspected SourceIdentity") }
        @{ Case = "a later leg's restore reused the repeated restore's identity"; Mutate = { param($p) $p.RestoredIdentities[1].Rows = @($script:restoredIdentity["package-directory#2"]) }; Expected = @("restore separate-config#1: the restored SourceIdentity {pd2} equals restore package-directory#2's") }
        @{ Case = "the repeated restore names another package's hash"; Mutate = { param($p) $p.RestoredIdentities[2].PackageSha256 = $script:otherSha }; Expected = @("restore package-directory#2: its package SHA-256 '{otherSha}' matches no package built in this run") }
        @{ Case = "the repeated restore's record has no package hash"; Mutate = { param($p) $p.RestoredIdentities[2].PSObject.Properties.Remove("PackageSha256") }; Expected = @("restore package-directory#2: its package SHA-256 '' matches no package built in this run") }
        @{ Case = "an identity record names no restore"; Mutate = { param($p) $p.RestoredIdentities.Add((New-CompleteRestoredIdentity -RestoreExecution "" -Identity "d1000000-0000-4000-8000-000000000007")) }; Expected = @("a restored SourceIdentity record names no restore execution") }
        @{ Case = "an identity record belongs to a negative leg"; Mutate = { param($p) $p.RestoredIdentities.Add((New-CompleteRestoredIdentity -RestoreExecution "tampered-package#1")) }; Expected = @("a restored SourceIdentity was recorded for restore tampered-package#1, which no selected leg performs") }
        @{ Case = "the run recorded no restored identities"; Mutate = { param($p) $p.Remove("RestoredIdentities") }; Expected = @("restore package-directory#1: no restored SourceIdentity was recorded", "restore package-directory#2: no restored SourceIdentity was recorded", "restore separate-config#1: no restored SourceIdentity was recorded") }

        # No package was built, while its evidence names its hash.
        @{ Case = "no package was built"; Mutate = { param($p) $p.Packages = @() }; Expected = @(
                "no package was built in this run"
                "a pre-backup SourceIdentity capture (default-minimal) is bound to package SHA-256 {minimalSha}, which no package built in this run has"
                "package inspection {db}: it inspected package SHA-256 '{minimalSha}', which no package built in this run has"
                "restore package-directory#1: its package SHA-256 '{minimalSha}' matches no package built in this run"
                "restore package-directory#2: its package SHA-256 '{minimalSha}' matches no package built in this run"
                "restore separate-config#1: its package SHA-256 '{minimalSha}' matches no package built in this run"
            )
        }
    ) {
        $provenance = New-CompleteProvenance
        & $Mutate $provenance

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be @(Expand-FixtureText $Expected)
    }

    It "requires no served-data read or restored identity for a run of only negative legs, but still the bound pre-backup capture" {
        $provenance = New-CompleteProvenance
        $provenance.Legs = @("tampered-package", "contaminated-package", "running-stack")
        $provenance.ApiReads.Clear()
        $provenance.RestoredIdentities.Clear()
        $provenance.PackageInspections = @()

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeTrue
        $classification.Reasons | Should -BeNullOrEmpty

        $provenance.SourceIdentityBindings = @()
        $withoutCapture = Get-RestoreSmokeResultClassification -Provenance $provenance

        $withoutCapture.Final | Should -BeFalse
        @($withoutCapture.Reasons) | Should -Be @(Expand-FixtureText "default-minimal package {minimalSha}: no pre-backup SourceIdentity capture is bound to it")
    }

    Context "a Populated restore" {
        BeforeEach {
            $script:populated = New-CompleteProvenance
            $script:populated.Legs = @("populated")
            $script:populated.ApiReads.Clear()
            $script:populated.ApiReads.Add((New-CompleteApiRead -RestoreExecution "populated#1" -TemplateKind Populated))
            $script:populated.Packages = @(New-CompletePackage -TemplateKind Populated)
            $script:populated.SourceIdentityBindings = @(New-CompleteBinding -TemplateKind Populated)
            $script:populated.PackageInspections = @(New-CompleteInspection -TemplateKind Populated)
            $script:populated.RestoredIdentities.Clear()
            $script:populated.RestoredIdentities.Add((New-CompleteRestoredIdentity -RestoreExecution "populated#1" -TemplateKind Populated))
        }

        It "is final with non-empty descriptor and schools reads" {
            $classification = Get-RestoreSmokeResultClassification -Provenance $script:populated

            $classification.Final | Should -BeTrue
            $classification.Reasons | Should -BeNullOrEmpty
        }

        It "is non-final when <case>" -ForEach @(
            @{ Case = "the schools read is missing"; Mutate = { param($p) $p.ApiReads[0].Reads.RemoveAt(1) }; Expected = @("restore populated#1: no schools read was recorded") }
            @{ Case = "the schools read returned no rows"; Mutate = { param($p) $p.ApiReads[0].Reads[1].Count = 0 }; Expected = @("restore populated#1: the schools read returned no rows") }
            @{ Case = "the schools read was not HTTP 200"; Mutate = { param($p) $p.ApiReads[0].Reads[1].StatusCode = 403 }; Expected = @("restore populated#1: the schools read returned HTTP 403, not 200") }
            @{ Case = "the record is a seeded Minimal read"; Mutate = { param($p) $p.ApiReads[0] = New-CompleteApiRead -RestoreExecution "populated#1" }; Expected = @("restore populated#1: the API read mode is 'seeded', expected 'populated' for a Populated restore", "restore populated#1: no schools read was recorded") }
            @{ Case = "the record is schema-only"; Mutate = { param($p) $p.ApiReads[0].Mode = "schema-only" }; Expected = @("restore populated#1: the API read was schema-only (unseeded source), which cannot prove seeded served data") }
        ) {
            & $Mutate $script:populated

            $classification = Get-RestoreSmokeResultClassification -Provenance $script:populated

            $classification.Final | Should -BeFalse
            @($classification.Reasons) | Should -Be $Expected
        }
    }

    Context "a run that builds both a Minimal and a Populated package" {
        BeforeEach {
            $script:both = New-CompleteProvenance
            $script:both.Legs = @("package-directory", "populated")
            $script:both.ApiReads.RemoveAt(1)
            $script:both.ApiReads.Add((New-CompleteApiRead -RestoreExecution "populated#1" -TemplateKind Populated))
            $script:both.Packages = @((New-CompletePackage), (New-CompletePackage -TemplateKind Populated))
            $script:both.SourceIdentityBindings = @((New-CompleteBinding), (New-CompleteBinding -TemplateKind Populated))
            $script:both.PackageInspections = @((New-CompleteInspection), (New-CompleteInspection -TemplateKind Populated))
            $script:both.RestoredIdentities.RemoveAt(1)
            $script:both.RestoredIdentities.Add((New-CompleteRestoredIdentity -RestoreExecution "populated#1" -TemplateKind Populated))
        }

        It "is final when each package has its own capture and inspection and each restore its own identity" {
            $classification = Get-RestoreSmokeResultClassification -Provenance $script:both

            $classification.Final | Should -BeTrue
            $classification.Reasons | Should -BeNullOrEmpty
        }

        It "is non-final when <case>" -ForEach @(
            @{ Case = "only the Minimal package's capture was kept"; Mutate = { param($p) $p.SourceIdentityBindings = @($p.SourceIdentityBindings[0]) }; Expected = @("default-populated package {populatedSha}: no pre-backup SourceIdentity capture is bound to it") }
            @{ Case = "the Populated capture was filed under the Minimal package's hash"; Mutate = { param($p) $p.SourceIdentityBindings[1].PackageSha256 = $script:minimalSha }; Expected = @("default-minimal package {minimalSha}: 2 pre-backup SourceIdentity captures are bound to it; expected exactly one", "default-populated package {populatedSha}: no pre-backup SourceIdentity capture is bound to it") }
            @{ Case = "the Populated package was not inspected"; Mutate = { param($p) $p.PackageInspections = @($p.PackageInspections[0]) }; Expected = @("default-populated package {populatedSha}: it was not independently inspected") }
            @{ Case = "the populated restore names the Minimal package"; Mutate = { param($p) $p.RestoredIdentities[2].PackageSha256 = $script:minimalSha }; Expected = @("restore populated#1: it restored the default-minimal package; the leg restores default-populated") }
            @{ Case = "the populated restore kept the Populated package's identity"; Mutate = { param($p) $p.RestoredIdentities[2].Rows = @($script:packageIdentity.Populated) }; Expected = @("restore populated#1: the restored SourceIdentity {populatedId} equals the package's inspected SourceIdentity") }
        ) {
            & $Mutate $script:both

            $classification = Get-RestoreSmokeResultClassification -Provenance $script:both

            $classification.Final | Should -BeFalse
            @($classification.Reasons) | Should -Be @(Expand-FixtureText $Expected)
        }
    }

    It "reports a second observation that could not be taken at all, without throwing" {
        $provenance = New-CompleteProvenance
        $provenance.StackObservations[1] = [pscustomobject]@{ Label = "leg-separate-config"; Services = [ordered]@{}; Reason = "docker ps failed (exit 1)" }

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be @(
            "leg-separate-config: the stack could not be observed: docker ps failed (exit 1)"
            "leg-separate-config: no dms container observed"
            "leg-separate-config: no config container observed"
            "leg-separate-config: no db container observed"
            "leg-separate-config: the observation names no stack start, so its staged selection is not bound to the stack it observed"
            "leg-separate-config: the staged schema selection was not observed"
        )
    }

    It "labels a missing or unlabelled observation by its position, without throwing" {
        $provenance = New-CompleteProvenance
        $provenance.StackObservations.Add($null)
        $provenance.StackObservations[1].Label = ""
        $provenance.StackObservations[1].PSObject.Properties.Remove("StagedSelection")

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        @($classification.Reasons) | Should -Be @(
            "stack observation 2: stack start 'stack-start#2' belongs to step 'leg-separate-config'"
            "stack observation 2: the staged schema selection was not observed"
            "stack observation 3: the observation record is missing"
        )
    }

    It "classifies an observation whose Services field is missing, without throwing" {
        $provenance = New-CompleteProvenance
        $provenance.StackObservations[1].PSObject.Properties.Remove("Services")

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        @($classification.Reasons) | Should -Be @(
            "leg-separate-config: no dms container observed"
            "leg-separate-config: no config container observed"
            "leg-separate-config: no db container observed"
        )
    }

    It "classifies an empty provenance with a reason for every missing item, without throwing" {
        $classification = Get-RestoreSmokeResultClassification -Provenance ([ordered]@{})

        $classification.Final | Should -BeFalse
        @($classification.Reasons) | Should -Be @(
            "SourceAtStart not captured"
            "SourceAtEnd not captured"
            "SchemaTools was not built in this run"
            "images were not built in this run"
            "no started stack was observed, so the images actually used are unknown"
            "no package was built in this run"
            "the selected legs were not recorded, so the required served-data API reads and restored SourceIdentity records are unknown"
        )
    }

    It "classifies the smoke's initial provenance shape (fresh ledger, empty lists) without throwing" {
        $provenance = [ordered]@{
            ExploratoryPackage = $false
            SourceAtStart      = $null
            SourceAtEnd        = $null
            SchemaTools        = $null
            Images             = (New-RestoreSmokeImageLedger)
            StackObservations  = [System.Collections.Generic.List[object]]::new()
            Packages           = [System.Collections.Generic.List[object]]::new()
            SourceIdentityBindings = [System.Collections.Generic.List[object]]::new()
            PackageInspections = [System.Collections.Generic.List[object]]::new()
            RestoredIdentities = [System.Collections.Generic.List[object]]::new()
        }

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        $classification.Reasons | Should -Contain "Dms image not verified as built in this run: missing"
        $classification.Reasons | Should -Contain "no started stack was observed, so the images actually used are unknown"
        $classification.Reasons | Should -Contain "no package was built in this run"
    }
}

Describe "Invoke-BootstrapRestoreSmoke complete preflight path (sandboxed, no Docker)" {
    BeforeAll {
        $script:pwshPath = (Get-Process -Id $PID).Path

        function script:New-SmokeSandbox {
            param([switch]$GitRepository, [switch]$SchemaToolsSucceed, [switch]$WithoutEngineComposeFiles)

            # The smoke's checkout is a subdirectory; the driver, call log, and results live
            # beside it, so a git-repository sandbox stays clean while the smoke runs.
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
            $checkout = Join-Path $root "checkout"
            $composeRoot = Join-Path $checkout "eng/docker-compose"
            $testsRoot = Join-Path $composeRoot "tests"
            New-Item -ItemType Directory -Path $testsRoot -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Invoke-BootstrapRestoreSmoke.ps1") -Destination $testsRoot
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1") -Destination $testsRoot
            # The helper modules the probe module imports, at their repository paths.
            $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
            New-Item -ItemType Directory -Path (Join-Path $checkout "eng/smoke_test/modules"), (Join-Path $checkout "eng/DatabaseTemplates") -Force | Out-Null
            foreach ($helperModule in @("eng/docker-compose/env-utility.psm1", "eng/docker-compose/database-safety.psm1", "eng/Dms-Management.psm1", "eng/smoke_test/modules/SmokeTest.psm1", "eng/DatabaseTemplates/Template-RestoreCore.psm1", "eng/schema-package-utility.psm1")) {
                Copy-Item -LiteralPath (Join-Path $repoRoot $helperModule) -Destination (Join-Path $checkout $helperModule)
            }
            Set-Content -LiteralPath (Join-Path $composeRoot ".env.example") -Value "POSTGRES_DB_NAME=edfi_datamanagementservice"
            # The repository's real engine compose files: leftover volumes are identified against them.
            if (-not $WithoutEngineComposeFiles) {
                foreach ($composeFile in @("postgresql.yml", "mssql.yml")) {
                    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$composeFile") -Destination $composeRoot
                }
            }

            # Every start/bootstrap script the smoke could invoke is a stub that only logs: even a
            # defect in the fakes below cannot reach a real stack.
            foreach ($scriptName in @("start-local-dms.ps1", "start-published-dms.ps1", "bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                Set-Content -LiteralPath (Join-Path $composeRoot $scriptName) -Value ('Add-Content -LiteralPath $env:SMOKE_SANDBOX_LOG -Value ("script: ' + $scriptName + ' " + ($args -join " "))')
            }
            Set-Content -LiteralPath (Join-Path $composeRoot "bootstrap-schema-tool.psm1") -Value 'function Resolve-DmsSchemaTool { param([string]$RequestedPath) throw "the sandbox resolver must not be reached" }'
            # The image builds run from the checkout's source directories.
            New-Item -ItemType Directory -Path (Join-Path $checkout "src/dms"), (Join-Path $checkout "src/config") -Force | Out-Null

            $toolDirectory = $null
            if ($SchemaToolsSucceed) {
                # A built-looking api-schema-tools: the executable, and a dll with a product version
                # (a copy of an assembly that has one). The resolver stub returns exactly that path.
                $toolDirectory = Join-Path $root "schema-tools"
                New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
                $executableName = if ($IsWindows) { "api-schema-tools.exe" } else { "api-schema-tools" }
                $executablePath = [System.IO.Path]::GetFullPath((Join-Path $toolDirectory $executableName))
                Set-Content -LiteralPath $executablePath -Value "sandbox"
                Copy-Item -LiteralPath ([System.Management.Automation.PSObject].Assembly.Location) -Destination (Join-Path $toolDirectory "api-schema-tools.dll")
                Set-Content -LiteralPath (Join-Path $composeRoot "bootstrap-schema-tool.psm1") -Value ("function Resolve-DmsSchemaTool { param([string]`$RequestedPath) return '" + $executablePath + "' }")
            }

            $driverPath = Join-Path $root "driver.ps1"
            Set-Content -LiteralPath $driverPath -Value @'
param([string]$SmokePath, [string]$ArgumentsJson)
$state = $env:SMOKE_SANDBOX_DOCKER_STATE | ConvertFrom-Json -AsHashtable
if ($null -eq $state.Images) { $state.Images = @{} }
$global:SmokeSandboxState = $state
function global:Test-SmokeSandboxTeardownRan {
    param([string]$Project)
    Set-StrictMode -Off
    if (-not (Test-Path -LiteralPath $env:SMOKE_SANDBOX_LOG)) { return $false }
    $prefix = if ($Project) { "script: start-" + ($Project -replace "^dms-", "") + "-dms.ps1 " } else { "script: start-" }
    return @(Get-Content -LiteralPath $env:SMOKE_SANDBOX_LOG | Where-Object { $_.StartsWith($prefix) }).Count -gt 0
}
function global:Get-SmokeSandboxProjectState {
    # Once the project's teardown stub has run, the project holds only its AfterTeardown leftovers.
    param([string]$Project)
    Set-StrictMode -Off
    $projectState = $global:SmokeSandboxState[$Project]
    if ($null -eq $projectState) { return $null }
    if (Test-SmokeSandboxTeardownRan -Project $Project) { return $projectState.AfterTeardown }
    return $projectState
}
function global:docker {
    # The fakes run inside the smoke's strict-mode scope; they read optional keys of the state.
    Set-StrictMode -Off
    $state = $global:SmokeSandboxState
    Add-Content -LiteralPath $env:SMOKE_SANDBOX_LOG -Value ("docker: " + ($args -join " "))
    $global:LASTEXITCODE = 0
    if ($args[0] -eq "info") { return "29.0.0" }
    if ($args[0] -eq "image" -and $args[1] -eq "inspect") {
        $reference = [string]$args[2]
        if ($state.InspectFailure) { $global:LASTEXITCODE = 1; return $state.InspectFailure }
        if ($state.Images.ContainsKey($reference)) { return $state.Images[$reference] }
        $global:LASTEXITCODE = 1
        return "Error response from daemon: No such image: $reference"
    }
    if ($args[0] -eq "buildx") {
        $iidFile = [string]$args[[array]::IndexOf($args, "--iidfile") + 1]
        $key = [System.IO.Path]::GetFileNameWithoutExtension($iidFile)
        if ($state.FailBuild -eq $key) { $global:LASTEXITCODE = 1; return "ERROR: failed to solve" }
        $imageId = "sha256:" + ([string]$key[0]) * 64
        Set-Content -LiteralPath $iidFile -Value $imageId -NoNewline
        for ($i = 0; $i -lt $args.Count - 1; $i++) { if ($args[$i] -eq "-t") { $state.Images[[string]$args[$i + 1]] = $imageId } }
        return "built"
    }
    if ($args[0] -eq "image" -and $args[1] -eq "rm") {
        $state.Images.Remove([string]$args[2])
        return "Untagged: $($args[2])"
    }
    if ($args[0] -eq "ps" -or ($args[0] -eq "volume" -and $args[1] -eq "ls")) {
        if ($state.FailInventory) { $global:LASTEXITCODE = 1; return "Cannot connect to the Docker daemon" }
        if ($state.FailInventoryAfterTeardown -and (Test-SmokeSandboxTeardownRan)) { $global:LASTEXITCODE = 1; return "Cannot connect to the Docker daemon" }
        $filter = [string]($args | Where-Object { "$_" -like "label=com.docker.compose.project=*" } | Select-Object -First 1)
        $project = $filter.Substring("label=com.docker.compose.project=".Length)
        $projectState = Get-SmokeSandboxProjectState -Project $project
        if ($null -eq $projectState) { return }
        if ($args[0] -eq "ps") { return @($projectState.Containers) }
        return @($projectState.Volumes)
    }
    if ($args[0] -eq "volume" -and $args[1] -eq "inspect") {
        # A volume exists while a project's current listing shows it; its labels are the compose
        # labels its name implies unless VolumeInspect overrides them (or fails the inspection).
        $name = [string]$args[-1]
        $override = $null
        if ($null -ne $state.VolumeInspect) { $override = $state.VolumeInspect[$name] }
        if ($null -ne $override -and $override.Fail) { $global:LASTEXITCODE = 1; return $override.Fail }
        $listed = @(@("dms-local", "dms-published") | ForEach-Object { Get-SmokeSandboxProjectState -Project $_ } | Where-Object { $null -ne $_ } | ForEach-Object { $_.Volumes })
        if ($listed -notcontains $name) { $global:LASTEXITCODE = 1; return "Error response from daemon: get ${name}: no such volume" }
        $separator = $name.IndexOf("_")
        $labels = @{}
        if ($separator -gt 0) {
            $labels = @{ "com.docker.compose.project" = $name.Substring(0, $separator); "com.docker.compose.volume" = $name.Substring($separator + 1); "com.docker.compose.version" = "5.1.3" }
        }
        if ($null -ne $override -and $null -ne $override.Labels) { $labels = $override.Labels }
        return (@{ CreatedAt = "2026-10-01T00:00:00Z"; Driver = "local"; Labels = $labels; Name = $name; Scope = "local" } | ConvertTo-Json -Compress -Depth 5)
    }
}
function global:dotnet {
    Set-StrictMode -Off
    Add-Content -LiteralPath $env:SMOKE_SANDBOX_LOG -Value ("dotnet: " + ($args -join " "))
    $toolDirectory = $global:SmokeSandboxState.SchemaToolsTargetDir
    if ([string]::IsNullOrEmpty($toolDirectory)) { $global:LASTEXITCODE = 1; return }
    $global:LASTEXITCODE = 0
    if ($args[0] -eq "msbuild") { return $toolDirectory }
}
$smokeArguments = if ([string]::IsNullOrWhiteSpace($ArgumentsJson)) { @{} } else { $ArgumentsJson | ConvertFrom-Json -AsHashtable }
& $SmokePath @smokeArguments
exit $LASTEXITCODE
'@

            $head = if ($GitRepository) { New-CleanTestRepository -Directory $checkout } else { $null }

            return [pscustomobject]@{
                Smoke   = Join-Path $testsRoot "Invoke-BootstrapRestoreSmoke.ps1"
                Driver  = $driverPath
                Log     = Join-Path $root "calls.log"
                Results = Join-Path $root "results.json"
                Head    = $head
                ToolDirectory = $toolDirectory
            }
        }

        function script:Invoke-SandboxedSmoke {
            param(
                [Parameter(Mandatory)]
                [hashtable]$DockerState,

                [hashtable]$Arguments = @{},

                [switch]$GitRepository,

                [switch]$SchemaToolsSucceed,

                [switch]$WithoutEngineComposeFiles
            )

            $sandbox = New-SmokeSandbox -GitRepository:$GitRepository -SchemaToolsSucceed:$SchemaToolsSucceed -WithoutEngineComposeFiles:$WithoutEngineComposeFiles
            $DockerState = $DockerState.Clone()
            if ($SchemaToolsSucceed) {
                $DockerState.SchemaToolsTargetDir = $sandbox.ToolDirectory
            }
            $smokeArguments = @{ ResultsPath = $sandbox.Results } + $Arguments
            $saved = @{ Log = $env:SMOKE_SANDBOX_LOG; State = $env:SMOKE_SANDBOX_DOCKER_STATE }
            try {
                $env:SMOKE_SANDBOX_LOG = $sandbox.Log
                $env:SMOKE_SANDBOX_DOCKER_STATE = $DockerState | ConvertTo-Json -Depth 5 -Compress
                $output = & $script:pwshPath -NoProfile -NonInteractive -File $sandbox.Driver -SmokePath $sandbox.Smoke -ArgumentsJson ($smokeArguments | ConvertTo-Json -Compress) 2>&1
                $exitCode = $LASTEXITCODE
            }
            finally {
                $env:SMOKE_SANDBOX_LOG = $saved.Log
                $env:SMOKE_SANDBOX_DOCKER_STATE = $saved.State
            }

            return [pscustomobject]@{
                ExitCode = $exitCode
                Output   = ($output | Out-String)
                Calls    = if (Test-Path -LiteralPath $sandbox.Log) { @(Get-Content -LiteralPath $sandbox.Log) } else { @() }
                Results  = if (Test-Path -LiteralPath $sandbox.Results) { Get-Content -LiteralPath $sandbox.Results -Raw | ConvertFrom-Json } else { $null }
                Head     = $sandbox.Head
            }
        }

        $script:dms1440Shape = @{
            "dms-local" = @{
                Containers = @("ed-fi-api|exited|dms", "ed-fi-api-config-service|exited|config", "dms-postgresql|exited|db")
                Volumes    = @("dms-local_dms-mssql-2025", "dms-local_dms-postgresql")
            }
        }
    }

    Context "a foreign stack is present and removal is not confirmed" {
        It "refuses <case> and leaves every container and volume untouched" -ForEach @(
            @{ Case = "stopped containers with retained volumes (-Wrapper local)"; State = $null; Wrapper = "local"; Names = @("ed-fi-api (exited)", "dms-postgresql (exited)", "dms-local_dms-mssql-2025", "dms-local_dms-postgresql") }
            @{ Case = "stopped containers only"; State = @{ "dms-local" = @{ Containers = @("ed-fi-api|exited|dms"); Volumes = @() } }; Wrapper = "local"; Names = @("ed-fi-api (exited)") }
            @{ Case = "retained volumes only"; State = @{ "dms-local" = @{ Containers = @(); Volumes = @("dms-local_dms-postgresql") } }; Wrapper = "local"; Names = @("dms-local_dms-postgresql") }
            @{ Case = "a running stack"; State = @{ "dms-local" = @{ Containers = @("ed-fi-api|running|dms"); Volumes = @("dms-local_dms-postgresql") } }; Wrapper = "local"; Names = @("ed-fi-api (running)") }
            @{ Case = "a dms-local stack under -Wrapper published"; State = $null; Wrapper = "published"; Names = @("dms-local:", "ed-fi-api (exited)") }
            @{ Case = "a dms-published stack under -Wrapper local"; State = @{ "dms-published" = @{ Containers = @("ed-fi-api|exited|dms"); Volumes = @("dms-published_dms-postgresql") } }; Wrapper = "local"; Names = @("dms-published:", "dms-published_dms-postgresql") }
        ) {
            $dockerState = if ($null -eq $State) { $script:dms1440Shape } else { $State }

            $run = Invoke-SandboxedSmoke -DockerState $dockerState -Arguments @{ Wrapper = $Wrapper }

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*Refusing to run: Docker already holds a stack this smoke did not create*Nothing was stopped or removed*"
            foreach ($name in $Names) {
                $run.Output | Should -BeLike "*$name*"
            }
            $run.Output | Should -BeLike "*no teardown: the preflight did not authorize any Docker change*"

            # Preservation: no start/bootstrap script ran (no teardown, no restart), no build ran,
            # and every docker call was a read-only listing.
            @($run.Calls | Where-Object { $_ -like "script: *" }) | Should -BeNullOrEmpty
            @($run.Calls | Where-Object { $_ -like "dotnet: *" }) | Should -BeNullOrEmpty
            $dockerCalls = @($run.Calls | Where-Object { $_ -like "docker: *" })
            $dockerCalls.Count | Should -BeGreaterThan 0
            @($dockerCalls | Where-Object { $_ -notmatch '^docker: (info|ps -a|volume ls) ' }) | Should -BeNullOrEmpty

            $run.Results.Status | Should -Be "failed"
            $run.Results.Classification.Final | Should -BeFalse
            $run.Results.Provenance.ForeignStack.RemovalConfirmed | Should -BeFalse
            @($run.Results.Provenance.ForeignStack.RemovedProjects) | Should -BeNullOrEmpty
            @($run.Results.Steps.Name) | Should -Be @("preflight")
            $run.Results.Steps[0].Status | Should -Be "failed"
        }

        It "refuses when docker cannot inventory the guarded projects, without any teardown" {
            $run = Invoke-SandboxedSmoke -DockerState @{ FailInventory = $true }

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*Foreign Docker state is unknown, so nothing was stopped or removed*"
            @($run.Calls | Where-Object { $_ -like "script: *" -or $_ -like "dotnet: *" }) | Should -BeNullOrEmpty
            $run.Results.Classification.Final | Should -BeFalse
        }
    }

    Context "no foreign stack is present" {
        It "tears down only the selected <wrapper> project before building, and again after the build failure" -ForEach @(
            @{ Wrapper = "local"; Selected = "start-local-dms.ps1"; Other = "start-published-dms.ps1" }
            @{ Wrapper = "published"; Selected = "start-published-dms.ps1"; Other = "start-local-dms.ps1" }
        ) {
            $run = Invoke-SandboxedSmoke -DockerState @{} -Arguments @{ Wrapper = $Wrapper }

            $run.ExitCode | Should -Be 1
            $firstDotnet = [array]::FindIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "dotnet: *" })
            $firstDotnet | Should -BeGreaterThan 0
            $run.Calls[$firstDotnet - 1] | Should -BeLike "script: $Selected *"
            @($run.Calls | Where-Object { $_ -like "script: $Selected *" }).Count | Should -Be 2
            @($run.Calls | Where-Object { $_ -like "script: $Other *" }) | Should -BeNullOrEmpty
            $run.Results.Provenance.ForeignStack.RemovalConfirmed | Should -BeFalse
            @($run.Results.Steps.Name) | Should -Be @("preflight", "build-schema-tools")
        }

        It "records the Data Standard selection: <case>" -ForEach @(
            @{ Case = "published without -DataStandardVersion is not forwarded"; Arguments = @{ Wrapper = "published" }; Supplied = $false; Forwarded = $false; StandardVersion = "5.2.0" }
            @{ Case = "published with an explicit 5.2 is forwarded"; Arguments = @{ Wrapper = "published"; DataStandardVersion = "5.2" }; Supplied = $true; Forwarded = $true; StandardVersion = "5.2.0" }
            @{ Case = "local default is forwarded"; Arguments = @{ Wrapper = "local" }; Supplied = $false; Forwarded = $true; StandardVersion = "5.2.0" }
            @{ Case = "local 6.1 defaults the package segment"; Arguments = @{ DataStandardVersion = "6.1" }; Supplied = $true; Forwarded = $true; StandardVersion = "6.1.0" }
        ) {
            $run = Invoke-SandboxedSmoke -DockerState @{} -Arguments $Arguments

            $run.Results.Provenance.DataStandardVersionSupplied | Should -Be $Supplied
            $run.Results.Provenance.DataStandardVersionForwarded | Should -Be $Forwarded
            $run.Results.Provenance.StandardVersion | Should -Be $StandardVersion
        }
    }

    Context "an extension-selection run the smoke cannot perform as specified" {
        It "refuses <case> in the preflight step, before any docker or script call" -ForEach @(
            @{ Case = "the local wrapper"; Arguments = @{ Leg = @("extension-selection"); Wrapper = "local" }; Expected = "The extension-selection leg requires -Wrapper published*" }
            @{ Case = "an explicit Data Standard under the published wrapper"; Arguments = @{ Leg = @("extension-selection"); Wrapper = "published"; DataStandardVersion = "5.2" }; Expected = "The extension-selection leg refuses an explicit -DataStandardVersion*" }
        ) {
            $run = Invoke-SandboxedSmoke -DockerState @{} -Arguments $Arguments

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*FAILED: $Expected"
            $run.Calls | Should -BeNullOrEmpty
            @($run.Results.Steps.Name) | Should -Be @("preflight")
            $run.Results.Steps[0].Status | Should -Be "failed"
            $run.Results.Classification.Final | Should -BeFalse
            $run.Output | Should -BeLike "*no teardown: the preflight did not authorize any Docker change*"
        }

        It "lets an extension-selection run under the published wrapper without a Data Standard pass the preflight" {
            $run = Invoke-SandboxedSmoke -DockerState @{} -Arguments @{ Leg = @("extension-selection"); Wrapper = "published" }

            $run.ExitCode | Should -Be 1
            @($run.Calls | Where-Object { $_ -like "docker: info*" }) | Should -HaveCount 1
            @($run.Results.Steps.Name) | Should -Be @("preflight", "build-schema-tools")
            $run.Results.Steps[0].Status | Should -Be "ok"
        }
    }

    Context "a foreign stack is present and removal is confirmed" {
        It "tears down exactly the foreign projects in the preflight and records the confirmation" {
            $state = $script:dms1440Shape + @{ "dms-published" = @{ Containers = @(); Volumes = @("dms-published_dms-postgresql") } }

            $run = Invoke-SandboxedSmoke -DockerState $state -Arguments @{ ConfirmForeignStackRemoval = $true }

            $firstDotnet = [array]::FindIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "dotnet: *" })
            $firstDotnet | Should -BeGreaterThan 0
            $preflightScripts = @($run.Calls[0..($firstDotnet - 1)] | Where-Object { $_ -like "script: *" })
            $preflightScripts.Count | Should -Be 2
            $preflightScripts[0] | Should -BeLike "script: start-local-dms.ps1 *"
            $preflightScripts[1] | Should -BeLike "script: start-published-dms.ps1 *"
            foreach ($teardown in $preflightScripts) {
                $teardown | Should -BeLike "*-v*"
                $teardown | Should -BeLike "*-RemoveBootstrap*"
            }
            $run.Results.Provenance.ForeignStack.RemovalConfirmed | Should -BeTrue
            @($run.Results.Provenance.ForeignStack.RemovedProjects) | Should -Be @("dms-local", "dms-published")
            @($run.Results.Provenance.ForeignStack.RemainingAfterRemoval | ForEach-Object { $_.Containers } | Where-Object { $_ }) | Should -BeNullOrEmpty
            @($run.Results.Provenance.ForeignStack.RemainingAfterRemoval | ForEach-Object { $_.Volumes } | Where-Object { $_ }) | Should -BeNullOrEmpty
            $run.Results.Classification.Final | Should -BeFalse
        }

        It "continues beside only the other engine's storage, records why, and finds it preserved at the end (<engine>)" -ForEach $script:engineCases {
            $state = @{
                "dms-local" = @{
                    Containers    = @("dms-db|exited|db")
                    Volumes       = @("dms-local_$OtherKey", "dms-local_$SelectedKey")
                    AfterTeardown = @{ Containers = @(); Volumes = @("dms-local_$OtherKey") }
                }
            }

            $run = Invoke-SandboxedSmoke -DockerState $state -Arguments @{ ConfirmForeignStackRemoval = $true; DatabaseEngine = $Engine }

            $run.Output | Should -BeLike "*leaving volume dms-local_$OtherKey in place: declared in $OtherFile as $OtherEngine storage; this run uses $Engine*"
            @($run.Results.Steps.Name) | Should -Be @("preflight", "build-schema-tools")
            $run.Results.Steps[0].Status | Should -Be "ok"
            $leftovers = $run.Results.Provenance.ForeignStack.LeftoverVolumes
            @($leftovers.Allowed.Name) | Should -Be @("dms-local_$OtherKey")
            @($leftovers.Allowed)[0].Reason | Should -Be "declared in $OtherFile as $OtherEngine storage; this run uses $Engine, whose $SelectedFile does not declare it, so this run's down -v teardowns leave it in place"
            @($leftovers.Blocked) | Should -BeNullOrEmpty
            @($run.Results.Provenance.ForeignStack.EngineVolumeDefinitions.ComposeFile) | Should -Be @("postgresql.yml", "mssql.yml")

            # Authorization came back after the check: the build failure's teardown ran, after the build.
            $firstDotnet = [array]::FindIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "dotnet: *" })
            $teardowns = @(for ($i = 0; $i -lt $run.Calls.Count; $i++) { if ($run.Calls[$i] -like "script: start-local-dms.ps1 *") { $i } })
            $teardowns.Count | Should -Be 2
            $teardowns[1] | Should -BeGreaterThan $firstDotnet

            # Re-inspected after every teardown of the run, and found unchanged.
            $lastInspect = [array]::FindLastIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "docker: volume inspect *" })
            $lastInspect | Should -BeGreaterThan $teardowns[1]
            $atEnd = @($run.Results.Provenance.ForeignStack.AllowedVolumesAtEnd)
            $atEnd.Count | Should -Be 1
            $atEnd[0].Name | Should -Be "dms-local_$OtherKey"
            $atEnd[0].Preserved | Should -BeTrue
            @($run.Results.Classification.Reasons | Where-Object { $_ -like "leftover volume*" }) | Should -BeNullOrEmpty
        }

        It "refuses mixed allowed and blocked leftovers before any build, with no further Docker change (<engine>)" -ForEach $script:engineCases {
            $state = @{
                "dms-local"     = @{
                    Containers    = @("dms-db|exited|db")
                    Volumes       = @("dms-local_$OtherKey", "dms-local_$SelectedKey", "dms-local_dms-keycloak")
                    AfterTeardown = @{ Containers = @(); Volumes = @("dms-local_$OtherKey", "dms-local_$SelectedKey", "dms-local_dms-keycloak") }
                }
                "dms-published" = @{
                    Containers    = @()
                    Volumes       = @("dms-published_$OtherKey", "dms-published_$SelectedKey")
                    AfterTeardown = @{ Containers = @(); Volumes = @("dms-published_$OtherKey", "dms-published_$SelectedKey") }
                }
                VolumeInspect   = @{ "dms-published_$SelectedKey" = @{ Fail = "Error response from daemon: context deadline exceeded" } }
            }

            $run = Invoke-SandboxedSmoke -DockerState $state -Arguments @{ ConfirmForeignStackRemoval = $true; DatabaseEngine = $Engine }

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*The confirmed removal left volumes this run cannot identify as unrelated to its $Engine storage: *"
            $run.Output | Should -BeLike "*dms-local_$SelectedKey (it is the selected engine's ($Engine) storage, declared in $SelectedFile; the run needs fresh storage)*"
            $run.Output | Should -BeLike "*dms-local_dms-keycloak (volume 'dms-keycloak' is not declared by any engine compose file*"
            $run.Output | Should -BeLike "*dms-published_$SelectedKey (inspection failed (docker exit 1: Error response from daemon: context deadline exceeded))*"
            $run.Output | Should -BeLike "*no teardown: the confirmed removal left Docker state the preflight did not accept; it is left as found*"

            # The only teardowns are the confirmed removal of each foreign project, and they precede
            # the leftover check; nothing after the check changes Docker, and no build ran.
            $scriptCalls = @(for ($i = 0; $i -lt $run.Calls.Count; $i++) { if ($run.Calls[$i] -like "script: *") { $i } })
            $scriptCalls.Count | Should -Be 2
            $run.Calls[$scriptCalls[0]] | Should -BeLike "script: start-local-dms.ps1 *"
            $run.Calls[$scriptCalls[1]] | Should -BeLike "script: start-published-dms.ps1 *"
            $firstInspect = [array]::FindIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "docker: volume inspect *" })
            $firstInspect | Should -BeGreaterThan $scriptCalls[1]
            @($run.Calls[$scriptCalls[0]..($run.Calls.Count - 1)] | Where-Object { $_ -like "docker: *" -and $_ -notmatch '^docker: (ps -a|volume ls|volume inspect) ' }) | Should -BeNullOrEmpty
            @($run.Calls | Where-Object { $_ -like "dotnet: *" }) | Should -BeNullOrEmpty

            @($run.Results.Steps.Name) | Should -Be @("preflight")
            $run.Results.Steps[0].Status | Should -Be "failed"
            $leftovers = $run.Results.Provenance.ForeignStack.LeftoverVolumes
            @($leftovers.Allowed.Name) | Should -Be @("dms-local_$OtherKey", "dms-published_$OtherKey")
            @($leftovers.Blocked.Name) | Should -Be @("dms-local_$SelectedKey", "dms-local_dms-keycloak", "dms-published_$SelectedKey")
            # The refusal left the allowed volumes in place too.
            @($run.Results.Provenance.ForeignStack.AllowedVolumesAtEnd | Where-Object { $_.Preserved }).Count | Should -Be 2
            $run.Results.Classification.Final | Should -BeFalse
        }

        It "refuses before any build, with no failure teardown, when <case>" -ForEach @(
            @{
                Case     = "containers survive the confirmed removal"
                State    = @{ "dms-local" = @{ Containers = @("dms-postgresql|exited|db", "dms-mssql|exited|db"); Volumes = @(); AfterTeardown = @{ Containers = @("dms-mssql|exited|db"); Volumes = @() } } }
                Expected = "*The confirmed removal left containers in place: dms-local: containers dms-mssql (exited)*"
            }
            @{
                Case     = "a leftover volume cannot be inspected"
                State    = @{ "dms-local" = @{ Containers = @("dms-postgresql|exited|db"); Volumes = @("dms-local_dms-mssql-2025"); AfterTeardown = @{ Containers = @(); Volumes = @("dms-local_dms-mssql-2025") } }; VolumeInspect = @{ "dms-local_dms-mssql-2025" = @{ Fail = "permission denied" } } }
                Expected = "*dms-local_dms-mssql-2025 (inspection failed (docker exit 1: permission denied))*"
            }
            @{
                Case     = "a leftover volume's project label conflicts with its listing"
                State    = @{ "dms-local" = @{ Containers = @("dms-postgresql|exited|db"); Volumes = @("dms-local_dms-mssql-2025"); AfterTeardown = @{ Containers = @(); Volumes = @("dms-local_dms-mssql-2025") } }; VolumeInspect = @{ "dms-local_dms-mssql-2025" = @{ Labels = @{ "com.docker.compose.project" = "dms-published"; "com.docker.compose.volume" = "dms-mssql-2025" } } } }
                Expected = "*dms-local_dms-mssql-2025 (its com.docker.compose.project label is 'dms-published', but it was listed under 'dms-local')*"
            }
            @{
                Case     = "the inventory after the removal fails"
                State    = @{ "dms-local" = @{ Containers = @("dms-postgresql|exited|db"); Volumes = @("dms-local_dms-mssql-2025"); AfterTeardown = @{ Containers = @(); Volumes = @("dms-local_dms-mssql-2025") } }; FailInventoryAfterTeardown = $true }
                Expected = "*Refusing to run: could not list containers for compose project*"
            }
        ) {
            $run = Invoke-SandboxedSmoke -DockerState $State -Arguments @{ ConfirmForeignStackRemoval = $true }

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike $Expected
            $run.Output | Should -BeLike "*no teardown: the confirmed removal left Docker state the preflight did not accept; it is left as found*"
            @($run.Calls | Where-Object { $_ -like "script: *" }).Count | Should -Be 1
            @($run.Calls | Where-Object { $_ -like "dotnet: *" }) | Should -BeNullOrEmpty
            @($run.Results.Steps.Name) | Should -Be @("preflight")
            $run.Results.Steps[0].Status | Should -Be "failed"
        }

        It "refuses before removing anything when the engine compose files cannot be read" {
            $run = Invoke-SandboxedSmoke -DockerState $script:dms1440Shape -Arguments @{ ConfirmForeignStackRemoval = $true } -WithoutEngineComposeFiles

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*Cannot identify leftover volumes: the postgresql compose file 'postgresql.yml' was not found*"
            $run.Output | Should -BeLike "*no teardown: the preflight did not authorize any Docker change*"
            @($run.Calls | Where-Object { $_ -like "script: *" -or $_ -like "dotnet: *" }) | Should -BeNullOrEmpty
            @($run.Calls | Where-Object { $_ -like "docker: *" -and $_ -notmatch '^docker: (info|ps -a|volume ls) ' }) | Should -BeNullOrEmpty
        }
    }

    Context "the smoke's checkout is a clean git repository" {
        It "passes the preflight, reaches the SchemaTools build, and records the clean revision at start and end" {
            $run = Invoke-SandboxedSmoke -DockerState @{} -GitRepository

            $run.Head | Should -Match '^[0-9a-f]{40}$'
            $run.Results | Should -Not -BeNullOrEmpty
            @($run.Results.Steps.Name) | Should -Be @("preflight", "build-schema-tools")
            $run.Results.Steps[0].Status | Should -Be "ok"
            @($run.Calls | Where-Object { $_ -like "dotnet: build *" }).Count | Should -Be 1
            foreach ($point in @("SourceAtStart", "SourceAtEnd")) {
                $source = $run.Results.Provenance.$point
                $source.Observed | Should -BeTrue
                $source.Clean | Should -BeTrue
                $source.Revision | Should -Be $run.Head
                @($source.Porcelain).Count | Should -Be 0
            }
            @($run.Results.Classification.Reasons | Where-Object { $_ -like "SourceAt*" }) | Should -BeNullOrEmpty
        }

        It "still writes results, with the clean start revision, when the preflight refuses a foreign stack" {
            $run = Invoke-SandboxedSmoke -DockerState $script:dms1440Shape -GitRepository

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*Refusing to run: Docker already holds a stack this smoke did not create*"
            @($run.Calls | Where-Object { $_ -like "script: *" -or $_ -like "dotnet: *" }) | Should -BeNullOrEmpty
            $run.Results.Provenance.SourceAtStart.Clean | Should -BeTrue
            $run.Results.Provenance.SourceAtStart.Revision | Should -Be $run.Head
            @($run.Results.Steps.Name) | Should -Be @("preflight")
        }
    }

    Context "the image builds after a passing preflight" {
        BeforeAll {
            $script:staleImages = @{
                "local/ed-fi-api"                       = ("sha256:" + "5" * 64)
                "ed-fi-api-local"                       = ("sha256:" + "5" * 64)
                "local/ed-fi-api-configuration-service" = ("sha256:" + "6" * 64)
                "edfialliance/ed-fi-api"                = ("sha256:" + "7" * 64)
            }

            function script:Get-RunTagSet {
                param($Run)
                $runId = $Run.Results.Provenance.RunId
                $runId | Should -Match '^[0-9a-f]{12}$'
                return [pscustomobject]@{
                    RunId     = $runId
                    Dms       = "local/ed-fi-api:dms-restore-smoke-$runId"
                    Config    = "local/ed-fi-api-configuration-service:dms-restore-smoke-$runId"
                    Published = "edfialliance/ed-fi-api:dms-restore-smoke-$runId"
                }
            }

            # Only the authorized teardowns (selected project, -d -v -RemoveBootstrap) may run: no
            # bootstrap wrapper and no start script without the teardown shape.
            function script:Assert-OnlyAuthorizedTeardown {
                param($Run, [string]$StartScript, [int]$Count)
                $scriptCalls = @($Run.Calls | Where-Object { $_ -like "script: *" })
                $scriptCalls.Count | Should -Be $Count
                foreach ($call in $scriptCalls) {
                    $call | Should -BeLike "script: $StartScript *"
                    $call | Should -BeLike "*-d*"
                    $call | Should -BeLike "*-v*"
                    $call | Should -BeLike "*-RemoveBootstrap*"
                }
                @($Run.Calls | Where-Object { $_ -like "script: bootstrap-*" }) | Should -BeNullOrEmpty
            }

            function script:Assert-OnlyRunTagsTouched {
                param($Run, [string]$RunId)
                foreach ($call in @($Run.Calls | Where-Object { $_ -like "docker: *" })) {
                    $call | Should -Not -BeLike "docker: tag *"
                    foreach ($reference in @($call -split " " | Where-Object { $_ -like "*ed-fi-api*" })) {
                        $reference | Should -BeLike "*:dms-restore-smoke-$RunId"
                    }
                }
            }
        }

        It "fails on a DMS build error while stale shared images exist, starts nothing, and removes nothing it does not own" {
            $run = Invoke-SandboxedSmoke -DockerState @{ Images = $script:staleImages.Clone(); FailBuild = "dms" } -SchemaToolsSucceed
            $tags = Get-RunTagSet -Run $run

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*Image provenance could not be established for Dms: docker buildx build (Dms) exited 1*"
            @($run.Results.Steps.Name) | Should -Be @("preflight", "build-schema-tools", "build-images")
            @($run.Results.Steps.Status) | Should -Be @("ok", "ok", "failed")
            Assert-OnlyAuthorizedTeardown -Run $run -StartScript "start-local-dms.ps1" -Count 2
            $buildIndex = [array]::FindIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "docker: buildx *" })
            $teardownIndexes = @(for ($i = 0; $i -lt $run.Calls.Count; $i++) { if ($run.Calls[$i] -like "script: *") { $i } })
            $buildIndex | Should -BeGreaterThan $teardownIndexes[0]
            $teardownIndexes[1] | Should -BeGreaterThan $buildIndex
            @($run.Calls | Where-Object { $_ -like "docker: buildx *" }).Count | Should -Be 1

            $images = $run.Results.Provenance.Images
            $images.Dms.ExitCode | Should -Be 1
            $images.Dms.Verified | Should -BeFalse
            $images.Dms.ImageId | Should -BeNullOrEmpty
            $images.Config | Should -BeNullOrEmpty
            @($images.OwnedTags) | Should -Be @($tags.Dms, $tags.Config)
            @($images.Cleanup | ForEach-Object { "$($_.Tag)=$($_.Action)" }) | Should -Be @("$($tags.Dms)=absent", "$($tags.Config)=absent")
            @($run.Calls | Where-Object { $_ -like "docker: image rm *" }) | Should -BeNullOrEmpty
            $run.Results.Provenance.ForwardedImageKeys | Should -BeNullOrEmpty
            $run.Results.Classification.Final | Should -BeFalse
            @($run.Results.Classification.Reasons | Where-Object { $_ -eq "Dms image not verified as built in this run: docker buildx build (Dms) exited 1" }).Count | Should -Be 1
            Assert-OnlyRunTagsTouched -Run $run -RunId $tags.RunId
        }

        It "keeps the DMS record and removes only the DMS run tag, after the teardown, when the CMS build fails" {
            $run = Invoke-SandboxedSmoke -DockerState @{ Images = $script:staleImages.Clone(); FailBuild = "config" } -SchemaToolsSucceed
            $tags = Get-RunTagSet -Run $run

            $run.ExitCode | Should -Be 1
            $images = $run.Results.Provenance.Images
            $images.Dms.Verified | Should -BeTrue
            $images.Dms.ImageId | Should -Be ("sha256:" + "d" * 64)
            $images.Dms.IidFileContent | Should -Be ("sha256:" + "d" * 64)
            $images.Config.Verified | Should -BeFalse
            $images.Config.ExitCode | Should -Be 1
            $removals = @($run.Calls | Where-Object { $_ -like "docker: image rm *" })
            $removals | Should -Be @("docker: image rm $($tags.Dms)")
            $lastTeardown = [array]::FindLastIndex([string[]]$run.Calls, [Predicate[string]] { param($line) $line -like "script: *" })
            [array]::IndexOf([string[]]$run.Calls, $removals[0]) | Should -BeGreaterThan $lastTeardown
            Assert-OnlyAuthorizedTeardown -Run $run -StartScript "start-local-dms.ps1" -Count 2
            Assert-OnlyRunTagsTouched -Run $run -RunId $tags.RunId
        }

        It "forwards the run tags to the <wrapper> wrapper's env file and removes every owned tag at the end" -ForEach @(
            @{ Wrapper = "local"; StartScript = "start-local-dms.ps1" }
            @{ Wrapper = "published"; StartScript = "start-published-dms.ps1" }
        ) {
            $run = Invoke-SandboxedSmoke -DockerState @{ Images = $script:staleImages.Clone() } -Arguments @{ Wrapper = $Wrapper } -SchemaToolsSucceed
            $tags = Get-RunTagSet -Run $run

            @($run.Results.Steps.Name)[0..2] | Should -Be @("preflight", "build-schema-tools", "build-images")
            $run.Results.Steps[2].Status | Should -Be "ok"
            $forwarded = $run.Results.Provenance.ForwardedImageKeys
            $forwarded.DMS_CONFIG_DOCKER_IMAGE | Should -Be $tags.Config
            if ($Wrapper -eq "local") {
                $forwarded.DMS_DOCKER_IMAGE | Should -Be $tags.Dms
                @($run.Results.Provenance.Images.OwnedTags) | Should -Be @($tags.Dms, $tags.Config)
            }
            else {
                $forwarded.DMS_IMAGE_TAG | Should -Be "dms-restore-smoke-$($tags.RunId)"
                @($run.Results.Provenance.Images.OwnedTags) | Should -Be @($tags.Dms, $tags.Published, $tags.Config)
                @($run.Calls | Where-Object { $_ -like "docker: buildx *" })[0] | Should -BeLike "*-t $($tags.Dms) -t $($tags.Published) -f Dockerfile*"
            }
            $run.Results.Provenance.Images.Dms.Verified | Should -BeTrue
            $run.Results.Provenance.Images.Config.Verified | Should -BeTrue
            @($run.Results.Provenance.Images.Cleanup.Action | Select-Object -Unique) | Should -Be @("removed")
            @($run.Calls | Where-Object { $_ -like "docker: image rm *" }).Count | Should -Be @($run.Results.Provenance.Images.OwnedTags).Count
            Assert-OnlyAuthorizedTeardown -Run $run -StartScript $StartScript -Count 2
            Assert-OnlyRunTagsTouched -Run $run -RunId $tags.RunId
        }

        It "claims and builds nothing when the run tags' absence cannot be established" {
            $run = Invoke-SandboxedSmoke -DockerState @{ Images = $script:staleImages.Clone(); InspectFailure = "permission denied while trying to connect to the Docker daemon socket" } -SchemaToolsSucceed

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*Cannot establish that the run tag*no image tag was claimed*"
            @($run.Calls | Where-Object { $_ -like "docker: buildx *" -or $_ -like "docker: image rm *" }) | Should -BeNullOrEmpty
            @($run.Results.Provenance.Images.OwnedTags) | Should -BeNullOrEmpty
            @($run.Results.Provenance.Images.Cleanup) | Should -BeNullOrEmpty
            Assert-OnlyAuthorizedTeardown -Run $run -StartScript "start-local-dms.ps1" -Count 2
        }
    }
}
