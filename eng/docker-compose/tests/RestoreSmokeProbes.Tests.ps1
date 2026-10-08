# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Behavioral tests for the restore smoke's decision and evidence helpers (RestoreSmokeProbes.psm1)
# and for the smoke's complete foreign-stack rejection path. No test reaches a real Docker daemon:
# module tests mock docker/dotnet/pwsh, and the rejection-path tests run a sandbox copy of the
# smoke in a child pwsh whose start/bootstrap scripts are logging stubs and whose docker/dotnet
# are logging fakes.

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
    Remove-Variable -Name RestoreSmokeTestDocker, RestoreSmokeTestTargetDir, RestoreSmokeTestPwshExit, RestoreSmokeImageTestState, RestoreSmokeTestVolumes, RestoreSmokeTestVolumeCalls -Scope Global -ErrorAction SilentlyContinue
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

Describe "Get-RestoreSmokeEffectiveSchemaPackageList" {
    It "reads SCHEMA_PACKAGES from the derived env file" {
        $file = Join-Path $TestDrive ".env.derived"
        Set-Content -LiteralPath $file -Value @("A=1", 'SCHEMA_PACKAGES=[{"name":"core"}]')

        (Get-RestoreSmokeEffectiveSchemaPackageList -DerivedEnvironmentFile $file).Value | Should -Be '[{"name":"core"}]'
    }

    It "reports a missing derived file instead of a value" {
        $result = Get-RestoreSmokeEffectiveSchemaPackageList -DerivedEnvironmentFile (Join-Path $TestDrive "absent.env")

        $result.Value | Should -BeNullOrEmpty
        $result.Reason | Should -Be "derived env file not present"
    }

    It "reports <case> as a reason, not as a value" -ForEach @(
        @{ Case = "a blank SCHEMA_PACKAGES"; Lines = @("A=1", "SCHEMA_PACKAGES=   "); Expected = "SCHEMA_PACKAGES is blank in the derived env file" }
        @{ Case = "an absent SCHEMA_PACKAGES"; Lines = @("A=1"); Expected = "SCHEMA_PACKAGES not set in the derived env file" }
    ) {
        $file = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N") + ".env")
        Set-Content -LiteralPath $file -Value $Lines

        $result = Get-RestoreSmokeEffectiveSchemaPackageList -DerivedEnvironmentFile $file

        $result.Value | Should -BeNullOrEmpty
        $result.Reason | Should -Be $Expected
    }

    It "reports an unreadable derived file as a reason instead of throwing" {
        $file = Join-Path $TestDrive "unreadable.env"
        Set-Content -LiteralPath $file -Value "SCHEMA_PACKAGES=x"
        Mock Get-Content -ModuleName RestoreSmokeProbes { throw "access denied" }

        $result = Get-RestoreSmokeEffectiveSchemaPackageList -DerivedEnvironmentFile $file

        $result.Value | Should -BeNullOrEmpty
        $result.Reason | Should -Be "derived env file could not be read: access denied"
    }
}

Describe "Get-RestoreSmokePackageProvenance" {
    BeforeAll {
        function script:New-FakeTemplatePackage {
            param([string]$Directory, [string]$RecordedSha)

            New-Item -ItemType Directory -Path $Directory -Force | Out-Null
            $contents = Join-Path $Directory "contents"
            New-Item -ItemType Directory -Path $contents -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $contents "restore-manifest.json") -Value '{"projects":["edfi","tpdm"]}'
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

        $record = Get-RestoreSmokePackageProvenance -PackageDirectory $directory -RestoreManifestFileName "restore-manifest.json" -TemplateKind Minimal

        $record.Verified | Should -BeTrue
        $record.Sha256 | Should -Be $sha
        $record.PackageId | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
        $record.PackageVersion | Should -Be "1.0.999"
        $record.AttestationProducer | Should -Be "restore-smoke-1234"
        $record.AttestationKeyId | Should -Be ("ab" * 32)
        $record.RestoreManifestProjects | Should -Be @("edfi", "tpdm")
    }

    It "is not verified when the attestation's recorded SHA does not match the file" {
        $directory = Join-Path $TestDrive "package-mismatch"
        $null = New-FakeTemplatePackage -Directory $directory -RecordedSha ("0" * 64)

        $record = Get-RestoreSmokePackageProvenance -PackageDirectory $directory -RestoreManifestFileName "restore-manifest.json" -TemplateKind Minimal

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

Describe "Get-RestoreSmokeResultClassification" {
    BeforeAll {
        $script:startRevision = "1111111111111111111111111111111111111111"
        $script:otherRevision = "2222222222222222222222222222222222222222"
        $script:engineDigest = "postgres@sha256:" + ("a" * 64)

        function script:New-CompleteObservation {
            param([string]$Label)

            return [pscustomobject]@{
                Label                   = $Label
                Reason                  = $null
                Services                = [ordered]@{
                    dms    = [pscustomobject]@{ Container = "ed-fi-api"; ImageId = "sha256:dms"; ImageRef = "local/ed-fi-api:dms-restore-smoke-0123456789ab"; RepoDigests = @(); RepoDigestsReason = $null }
                    config = [pscustomobject]@{ Container = "dms-config-service"; ImageId = "sha256:config"; ImageRef = "local/ed-fi-api-configuration-service:dms-restore-smoke-0123456789ab"; RepoDigests = @(); RepoDigestsReason = $null }
                    db     = [pscustomobject]@{ Container = "dms-postgresql"; ImageId = "sha256:pg"; ImageRef = "postgres:16"; RepoDigests = @($script:engineDigest); RepoDigestsReason = $null }
                }
                EffectiveSchemaPackages = [pscustomobject]@{ Source = ".bootstrap/.env.derived"; Value = '[{"name":"EdFi.DataStandard52.ApiSchema"}]'; Reason = $null }
            }
        }

        # A complete run: every final-evidence field present, SourceIdentity supplied (so its
        # pending Step 2.3 implementation cannot mask a check), and two stack observations.
        function script:New-CompleteProvenance {
            $observations = [System.Collections.Generic.List[object]]::new()
            $observations.Add((New-CompleteObservation -Label "leg-package-directory"))
            $observations.Add((New-CompleteObservation -Label "leg-separate-config"))
            return [ordered]@{
                ExploratoryPackage   = $false
                SourceAtStart        = [pscustomobject]@{ Revision = $script:startRevision; Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
                SourceAtEnd          = [pscustomobject]@{ Revision = $script:startRevision; Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
                SchemaTools          = [pscustomobject]@{ Verified = $true; Reason = $null; Sha256AtBuild = "aa"; Sha256AtEnd = "aa" }
                Images               = [ordered]@{
                    Dms    = [ordered]@{ ImageId = "sha256:dms"; Verified = $true; Reason = $null }
                    Config = [ordered]@{ ImageId = "sha256:config"; Verified = $true; Reason = $null }
                }
                StackObservations    = $observations
                Packages             = @([pscustomobject]@{ TemplateKind = "Minimal"; Verified = $true; Reason = $null })
                SourceIdentity       = "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"
                SourceIdentityReason = $null
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
        @{ Case = "no package was built"; Mutate = { param($p) $p.Packages = @() }; Expected = "no package was built in this run" }
        @{ Case = "the package was not verified"; Mutate = { param($p) $p.Packages[0].Verified = $false; $p.Packages[0].Reason = "sha mismatch" }; Expected = "package (Minimal) not verified: sha mismatch" }
        @{ Case = "the pre-backup SourceIdentity was not captured"; Mutate = { param($p) $p.SourceIdentity = $null; $p.SourceIdentityReason = "not implemented yet (Step 2.3)" }; Expected = "pre-backup SourceIdentity not captured: not implemented yet (Step 2.3)" }

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

        # Effective SCHEMA_PACKAGES, only the second observation lacking it (2.1c).
        @{ Case = "the second observation has no SCHEMA_PACKAGES field"; Mutate = { param($p) $p.StackObservations[1].PSObject.Properties.Remove("EffectiveSchemaPackages") }; Expected = "leg-separate-config: effective SCHEMA_PACKAGES was not observed" }
        @{ Case = "the second observation's SCHEMA_PACKAGES record is null"; Mutate = { param($p) $p.StackObservations[1].EffectiveSchemaPackages = $null }; Expected = "leg-separate-config: effective SCHEMA_PACKAGES was not observed" }
        @{ Case = "the second observation's SCHEMA_PACKAGES value is blank"; Mutate = { param($p) $p.StackObservations[1].EffectiveSchemaPackages.Value = "   " }; Expected = "leg-separate-config: effective SCHEMA_PACKAGES not observed: the value is blank" }
        @{ Case = "the second observation's SCHEMA_PACKAGES read failed"; Mutate = { param($p) $p.StackObservations[1].EffectiveSchemaPackages = [pscustomobject]@{ Source = "x"; Value = $null; Reason = "derived env file not present" } }; Expected = "leg-separate-config: effective SCHEMA_PACKAGES not observed: derived env file not present" }
        @{ Case = "the second observation's SCHEMA_PACKAGES record has no Value field"; Mutate = { param($p) $p.StackObservations[1].EffectiveSchemaPackages = [pscustomobject]@{ Source = "x" } }; Expected = "leg-separate-config: effective SCHEMA_PACKAGES not observed: the value is blank" }

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
            "leg-separate-config: effective SCHEMA_PACKAGES was not observed"
        )
    }

    It "labels a missing or unlabelled observation by its position, without throwing" {
        $provenance = New-CompleteProvenance
        $provenance.StackObservations.Add($null)
        $provenance.StackObservations[1].Label = ""
        $provenance.StackObservations[1].PSObject.Properties.Remove("EffectiveSchemaPackages")

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        @($classification.Reasons) | Should -Be @(
            "stack observation 2: effective SCHEMA_PACKAGES was not observed"
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
            "pre-backup SourceIdentity not captured: "
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
            SourceIdentity     = $null
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
