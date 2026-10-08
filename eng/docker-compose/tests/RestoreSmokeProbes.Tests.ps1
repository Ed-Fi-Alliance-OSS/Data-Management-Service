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
}

AfterAll {
    if ($script:createdDockerFallback) {
        Remove-Item function:global:docker -ErrorAction SilentlyContinue
    }
    Remove-Variable -Name RestoreSmokeTestDocker, RestoreSmokeTestTargetDir, RestoreSmokeTestPwshExit -Scope Global -ErrorAction SilentlyContinue
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

Describe "Invoke-RestoreSmokeImageBuild" {
    It "records each DockerBuild command, exit code, and the resulting image ID" {
        Mock pwsh -ModuleName RestoreSmokeProbes { $global:LASTEXITCODE = 0 }
        Mock docker -ModuleName RestoreSmokeProbes {
            $global:LASTEXITCODE = 0
            if ($args -contains "local/ed-fi-api") { return "sha256:dms" }
            return "sha256:config"
        }

        $images = Invoke-RestoreSmokeImageBuild -RepoRoot $TestDrive

        $images.Dms.Command | Should -BeLike '*build-dms.ps1" DockerBuild'
        $images.Dms.ExitCode | Should -Be 0
        $images.Dms.Image | Should -Be "local/ed-fi-api"
        $images.Dms.ImageId | Should -Be "sha256:dms"
        $images.Config.Command | Should -BeLike '*build-config.ps1" DockerBuild'
        $images.Config.Image | Should -Be "local/ed-fi-api-configuration-service"
        $images.Config.ImageId | Should -Be "sha256:config"
    }

    It "records no image ID, and never inspects, when a build fails" {
        Mock pwsh -ModuleName RestoreSmokeProbes { $global:LASTEXITCODE = 3 }
        Mock docker -ModuleName RestoreSmokeProbes { $global:LASTEXITCODE = 0; "sha256:stale" }

        $images = Invoke-RestoreSmokeImageBuild -RepoRoot $TestDrive

        $images.Dms.ImageId | Should -BeNullOrEmpty
        $images.Dms.Reason | Should -Be "build-dms.ps1 DockerBuild exited 3"
        $images.Config.ImageId | Should -BeNullOrEmpty
        Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 0 -Exactly
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
        Should -Invoke docker -ModuleName RestoreSmokeProbes -Times 1 -Exactly -ParameterFilter { $args[0] -eq "ps" -and $args -contains "label=com.docker.compose.project=dms-local" }
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

Describe "Get-RestoreSmokeResultClassification" {
    BeforeAll {
        function script:New-CompleteProvenance {
            $clean = [pscustomobject]@{ Observed = $true; Clean = $true; Porcelain = @(); Reason = $null }
            return [ordered]@{
                ExploratoryPackage   = $false
                SourceAtStart        = $clean
                SourceAtEnd          = $clean
                SchemaTools          = [pscustomobject]@{ Verified = $true; Reason = $null; Sha256AtBuild = "aa"; Sha256AtEnd = "aa" }
                Images               = [pscustomobject]@{
                    Dms    = [pscustomobject]@{ ImageId = "sha256:dms"; Reason = $null }
                    Config = [pscustomobject]@{ ImageId = "sha256:config"; Reason = $null }
                }
                StackObservations    = @(
                    [pscustomobject]@{
                        Label    = "leg-package-directory"
                        Services = [ordered]@{
                            dms    = [pscustomobject]@{ ImageId = "sha256:dms" }
                            config = [pscustomobject]@{ ImageId = "sha256:config" }
                            db     = [pscustomobject]@{ ImageId = "sha256:pg" }
                        }
                    }
                )
                Packages             = @([pscustomobject]@{ TemplateKind = "Minimal"; Verified = $true; Reason = $null })
                SourceIdentity       = "6f1c1c33-0d4e-4d0f-9b9e-4f5b8a3d2c11"
                SourceIdentityReason = $null
            }
        }
    }

    It "classifies a run as final only when every provenance condition holds" {
        $classification = Get-RestoreSmokeResultClassification -Provenance (New-CompleteProvenance)

        $classification.Final | Should -BeTrue
        $classification.Reasons | Should -BeNullOrEmpty
    }

    It "is non-final when <case>" -ForEach @(
        @{ Case = "the run is exploratory"; Mutate = { param($p) $p.ExploratoryPackage = $true }; Expected = "*-ExploratoryPackage marks this run exploratory*" }
        @{ Case = "the worktree was dirty at start"; Mutate = { param($p) $p.SourceAtStart = [pscustomobject]@{ Observed = $true; Clean = $false; Porcelain = @(" M file"); Reason = $null } }; Expected = "*SourceAtStart worktree is dirty*" }
        @{ Case = "the worktree became dirty by the end"; Mutate = { param($p) $p.SourceAtEnd = [pscustomobject]@{ Observed = $true; Clean = $false; Porcelain = @(" M packages.lock.json"); Reason = $null } }; Expected = "*SourceAtEnd worktree is dirty*" }
        @{ Case = "the revision could not be observed"; Mutate = { param($p) $p.SourceAtStart = [pscustomobject]@{ Observed = $false; Clean = $false; Porcelain = @(); Reason = "git rev-parse exit 128" } }; Expected = "*SourceAtStart not observed: git rev-parse exit 128*" }
        @{ Case = "SchemaTools was never built"; Mutate = { param($p) $p.SchemaTools = $null }; Expected = "*SchemaTools was not built in this run*" }
        @{ Case = "the SchemaTools resolution did not match"; Mutate = { param($p) $p.SchemaTools = [pscustomobject]@{ Verified = $false; Reason = "the bootstrap resolver selects 'x'"; Sha256AtBuild = "aa"; Sha256AtEnd = "aa" } }; Expected = "*SchemaTools not verified: the bootstrap resolver selects 'x'*" }
        @{ Case = "the SchemaTools executable changed during the run"; Mutate = { param($p) $p.SchemaTools.Sha256AtEnd = "bb" }; Expected = "*SchemaTools executable changed during the run*" }
        @{ Case = "images were never built"; Mutate = { param($p) $p.Images = $null }; Expected = "*images were not built in this run*" }
        @{ Case = "no started stack was observed"; Mutate = { param($p) $p.StackObservations = @() }; Expected = "*no started stack was observed*" }
        @{ Case = "the stack ran a different DMS image"; Mutate = { param($p) $p.StackObservations[0].Services["dms"] = [pscustomobject]@{ ImageId = "sha256:stale" } }; Expected = "*leg-package-directory: dms runs image sha256:stale, not the in-run build*" }
        @{ Case = "no config container was observed"; Mutate = { param($p) $p.StackObservations[0].Services.Remove("config") }; Expected = "*leg-package-directory: no config container observed*" }
        @{ Case = "no package was built"; Mutate = { param($p) $p.Packages = @() }; Expected = "*no package was built in this run*" }
        @{ Case = "the package was not verified"; Mutate = { param($p) $p.Packages = @([pscustomobject]@{ TemplateKind = "Minimal"; Verified = $false; Reason = "sha mismatch" }) }; Expected = "*package (Minimal) not verified: sha mismatch*" }
        @{ Case = "the pre-backup SourceIdentity was not captured"; Mutate = { param($p) $p.SourceIdentity = $null; $p.SourceIdentityReason = "not implemented yet (Step 2.3)" }; Expected = "*pre-backup SourceIdentity not captured: not implemented yet (Step 2.3)*" }
    ) {
        $provenance = New-CompleteProvenance
        & $Mutate $provenance

        $classification = Get-RestoreSmokeResultClassification -Provenance $provenance

        $classification.Final | Should -BeFalse
        @($classification.Reasons | Where-Object { $_ -like $Expected }).Count | Should -Be 1
    }
}

Describe "Invoke-BootstrapRestoreSmoke complete preflight path (sandboxed, no Docker)" {
    BeforeAll {
        $script:pwshPath = (Get-Process -Id $PID).Path

        function script:New-SmokeSandbox {
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
            $composeRoot = Join-Path $root "eng/docker-compose"
            $testsRoot = Join-Path $composeRoot "tests"
            New-Item -ItemType Directory -Path $testsRoot -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Invoke-BootstrapRestoreSmoke.ps1") -Destination $testsRoot
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1") -Destination $testsRoot
            Set-Content -LiteralPath (Join-Path $composeRoot ".env.example") -Value "POSTGRES_DB_NAME=edfi_datamanagementservice"

            # Every start/bootstrap script the smoke could invoke is a stub that only logs: even a
            # defect in the fakes below cannot reach a real stack.
            foreach ($scriptName in @("start-local-dms.ps1", "start-published-dms.ps1", "bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                Set-Content -LiteralPath (Join-Path $composeRoot $scriptName) -Value ('Add-Content -LiteralPath $env:SMOKE_SANDBOX_LOG -Value ("script: ' + $scriptName + ' " + ($args -join " "))')
            }
            Set-Content -LiteralPath (Join-Path $composeRoot "bootstrap-schema-tool.psm1") -Value 'function Resolve-DmsSchemaTool { param([string]$RequestedPath) throw "the sandbox resolver must not be reached" }'

            $driverPath = Join-Path $root "driver.ps1"
            Set-Content -LiteralPath $driverPath -Value @'
param([string]$SmokePath, [string]$ArgumentsJson)
$state = $env:SMOKE_SANDBOX_DOCKER_STATE | ConvertFrom-Json -AsHashtable
function global:docker {
    # The fakes run inside the smoke's strict-mode scope; they read optional keys of the state.
    Set-StrictMode -Off
    Add-Content -LiteralPath $env:SMOKE_SANDBOX_LOG -Value ("docker: " + ($args -join " "))
    $global:LASTEXITCODE = 0
    if ($args[0] -eq "info") { return "29.0.0" }
    if ($args[0] -eq "ps" -or ($args[0] -eq "volume" -and $args[1] -eq "ls")) {
        if ($state.FailInventory) { $global:LASTEXITCODE = 1; return "Cannot connect to the Docker daemon" }
        $filter = [string]($args | Where-Object { "$_" -like "label=com.docker.compose.project=*" } | Select-Object -First 1)
        $project = $filter.Substring("label=com.docker.compose.project=".Length)
        $projectState = $state[$project]
        if ($null -eq $projectState) { return }
        # Once the project's teardown stub has run, the project holds only its AfterTeardown leftovers.
        $teardownLine = "script: start-" + ($project -replace "^dms-", "") + "-dms.ps1 "
        if ((Test-Path -LiteralPath $env:SMOKE_SANDBOX_LOG) -and @(Get-Content -LiteralPath $env:SMOKE_SANDBOX_LOG | Where-Object { $_.StartsWith($teardownLine) }).Count -gt 0) {
            $projectState = $projectState.AfterTeardown
            if ($null -eq $projectState) { return }
        }
        if ($args[0] -eq "ps") { return @($projectState.Containers) }
        return @($projectState.Volumes)
    }
}
function global:dotnet {
    Add-Content -LiteralPath $env:SMOKE_SANDBOX_LOG -Value ("dotnet: " + ($args -join " "))
    $global:LASTEXITCODE = 1
}
$smokeArguments = if ([string]::IsNullOrWhiteSpace($ArgumentsJson)) { @{} } else { $ArgumentsJson | ConvertFrom-Json -AsHashtable }
& $SmokePath @smokeArguments
exit $LASTEXITCODE
'@

            return [pscustomobject]@{
                Smoke   = Join-Path $testsRoot "Invoke-BootstrapRestoreSmoke.ps1"
                Driver  = $driverPath
                Log     = Join-Path $root "calls.log"
                Results = Join-Path $root "results.json"
            }
        }

        function script:Invoke-SandboxedSmoke {
            param(
                [Parameter(Mandatory)]
                [hashtable]$DockerState,

                [hashtable]$Arguments = @{}
            )

            $sandbox = New-SmokeSandbox
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

        It "records volumes another engine leaves behind and continues" {
            $state = @{
                "dms-local" = @{
                    Containers    = @("dms-postgresql|exited|db")
                    Volumes       = @("dms-local_dms-mssql-2025", "dms-local_dms-postgresql")
                    AfterTeardown = @{ Containers = @(); Volumes = @("dms-local_dms-mssql-2025") }
                }
            }

            $run = Invoke-SandboxedSmoke -DockerState $state -Arguments @{ ConfirmForeignStackRemoval = $true }

            $run.Output | Should -BeLike "*the confirmed removal left volumes this run's engine does not use: dms-local: containers none; volumes dms-local_dms-mssql-2025*"
            $remaining = @($run.Results.Provenance.ForeignStack.RemainingAfterRemoval | Where-Object { $_.Project -eq "dms-local" })[0]
            @($remaining.Volumes) | Should -Be @("dms-local_dms-mssql-2025")
            @($run.Results.Steps.Name) | Should -Be @("preflight", "build-schema-tools")
            $run.Results.Steps[0].Status | Should -Be "ok"
        }

        It "stops before any build when containers survive the confirmed removal" {
            $state = @{
                "dms-local" = @{
                    Containers    = @("dms-postgresql|exited|db", "dms-mssql|exited|db")
                    Volumes       = @()
                    AfterTeardown = @{ Containers = @("dms-mssql|exited|db"); Volumes = @() }
                }
            }

            $run = Invoke-SandboxedSmoke -DockerState $state -Arguments @{ ConfirmForeignStackRemoval = $true }

            $run.ExitCode | Should -Be 1
            $run.Output | Should -BeLike "*The confirmed removal left containers in place: dms-local: containers dms-mssql (exited)*"
            @($run.Calls | Where-Object { $_ -like "dotnet: *" }) | Should -BeNullOrEmpty
            @($run.Results.Steps.Name) | Should -Be @("preflight")
            $run.Results.Steps[0].Status | Should -Be "failed"
        }
    }
}
