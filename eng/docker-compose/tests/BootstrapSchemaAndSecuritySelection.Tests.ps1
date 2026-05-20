# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe "Story 00 bootstrap" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:hashA = "0000000000000000000000000000000000000000000000000000000000000001"
        $script:hashB = "0000000000000000000000000000000000000000000000000000000000000002"
        # Story 04 boundary: USE_API_SCHEMA_PATH, API_SCHEMA_PATH, SCHEMA_PACKAGES,
        # DMS_API_SCHEMA_MOUNT_SOURCE are NOT set by Set-BootstrapStartupEnvironment (Story 04 owns
        # that flip). They are included here for isolation only so that incidental env state from
        # the host environment does not bleed into assertion tests that expect those vars to be absent.
        $script:bootstrapEnvVars = @(
            "USE_API_SCHEMA_PATH",
            "API_SCHEMA_PATH",
            "SCHEMA_PACKAGES",
            "DMS_API_SCHEMA_MOUNT_SOURCE",
            "DMS_CONFIG_CLAIMS_SOURCE",
            "DMS_CONFIG_CLAIMS_DIRECTORY",
            "DMS_CONFIG_CLAIMS_MOUNT_SOURCE"
        )

    function script:New-TestDirectory {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-story00-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    function script:New-IsolatedBootstrapRepo {
        $repoRoot = New-TestDirectory
        $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
        New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

        foreach ($fileName in @("bootstrap-manifest.psm1", "prepare-dms-schema.ps1", "prepare-dms-claims.ps1")) {
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $fileName) -Destination $dockerComposeRoot
        }

        $claimsSourceRoot = Join-Path $script:sourceRepoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend"
        $claimsTargetRoot = Join-Path $repoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend"
        New-Item -ItemType Directory -Path $claimsTargetRoot -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $claimsSourceRoot "Claims") -Destination $claimsTargetRoot -Recurse
        Copy-Item -LiteralPath (Join-Path $claimsSourceRoot "Deploy") -Destination $claimsTargetRoot -Recurse

        return [pscustomobject]@{
            RepoRoot = $repoRoot
            DockerComposeRoot = $dockerComposeRoot
            BootstrapRoot = Join-Path $dockerComposeRoot ".bootstrap"
            PrepareSchemaScript = Join-Path $dockerComposeRoot "prepare-dms-schema.ps1"
            PrepareClaimsScript = Join-Path $dockerComposeRoot "prepare-dms-claims.ps1"
            ManifestModule = Join-Path $dockerComposeRoot "bootstrap-manifest.psm1"
        }
    }

    function script:New-FakeSchemaTool {
        param(
            [Parameter(Mandatory)]
            [string]
            $Directory,

            [string]
            $Hash = $script:hashA,

            [int]
            $ExitCode = 0
        )

        $path = Join-Path $Directory "fake-dms-schema.ps1"
        @"
param([Parameter(ValueFromRemainingArguments = `$true)][string[]] `$Arguments)
Write-Output "Effective schema hash: $Hash"
exit $ExitCode
"@ | Set-Content -LiteralPath $path -Encoding utf8
        return $path
    }

    function script:New-ApiSchemaFile {
        param(
            [Parameter(Mandatory)]
            [string]
            $Path,

            [Parameter(Mandatory)]
            [string]
            $ProjectName,

            [Parameter(Mandatory)]
            [string]
            $ProjectEndpointName,

            [bool]
            $IsExtensionProject
        )

        $schema = [ordered]@{
            apiSchemaVersion = "1.0.0"
            projectSchema = [ordered]@{
                projectName = $ProjectName
                projectEndpointName = $ProjectEndpointName
                isExtensionProject = $IsExtensionProject
                resourceSchemas = [ordered]@{}
                openApiBaseDocuments = [ordered]@{
                    resources = [ordered]@{
                        openapi = "3.0.1"
                    }
                }
            }
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        $schema | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
    }

    function script:New-ApiSchemaSet {
        param(
            [string[]]
            $Extensions = @()
        )

        $schemaDir = Join-Path $script:repo.RepoRoot "schema"
        New-ApiSchemaFile `
            -Path (Join-Path $schemaDir "ApiSchema.json") `
            -ProjectName "Ed-Fi" `
            -ProjectEndpointName "ed-fi" `
            -IsExtensionProject $false

        foreach ($extension in $Extensions) {
            New-ApiSchemaFile `
                -Path (Join-Path $schemaDir "ApiSchema-$extension-EXTENSION.json") `
                -ProjectName $extension `
                -ProjectEndpointName $extension.ToLowerInvariant() `
                -IsExtensionProject $true
        }

        return $schemaDir
    }

    function script:Invoke-PrepareSchema {
        param(
            [Parameter(Mandatory)]
            [string]
            $ApiSchemaPath,

            [string]
            $Hash = $script:hashA,

            [int]
            $ToolExitCode = 0
        )

        $tool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $Hash -ExitCode $ToolExitCode
        & $script:repo.PrepareSchemaScript -ApiSchemaPath $ApiSchemaPath -SchemaToolPath $tool | Out-Null
    }

    function script:Invoke-PrepareClaim {
        param(
            [string]
            $ClaimsDirectoryPath
        )

        if ([string]::IsNullOrWhiteSpace($ClaimsDirectoryPath)) {
            & $script:repo.PrepareClaimsScript | Out-Null
            return
        }

        & $script:repo.PrepareClaimsScript -ClaimsDirectoryPath $ClaimsDirectoryPath | Out-Null
    }

    function script:Get-RootManifest {
        return Get-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") -Raw |
            ConvertFrom-Json
    }

    function script:New-ExplicitClaimsetFragment {
        param(
            [Parameter(Mandatory)]
            [string]
            $Path,

            [string]
            $ClaimSetName = "EdFiSandbox",

            [string]
            $ResourceClaim = "http://example.org/identity/claims/widget",

            [string]
            $Action = "Read"
        )

        $fragment = [ordered]@{
            name = $ClaimSetName
            resourceClaims = @(
                [ordered]@{
                    isParent = $false
                    name = $ResourceClaim
                    authorizationStrategyOverridesForCRUD = @(
                        [ordered]@{
                            actionName = $Action
                        }
                    )
                }
            )
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
    }
    }

    BeforeEach {
        $script:envSnapshot = @{}
        foreach ($name in $script:bootstrapEnvVars) {
            $script:envSnapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
        }

        $script:repo = New-IsolatedBootstrapRepo
    }

    AfterEach {
        foreach ($name in $script:bootstrapEnvVars) {
            [System.Environment]::SetEnvironmentVariable($name, $script:envSnapshot[$name])
        }

        if ($null -ne $script:repo -and (Test-Path -LiteralPath $script:repo.RepoRoot)) {
            Remove-Item -LiteralPath $script:repo.RepoRoot -Recurse -Force
        }
    }

    Context "schema staging" {
        It "rejects missing ApiSchemaPath and Story 06 schema parameters" {
            { & $script:repo.PrepareSchemaScript } |
                Should -Throw -ExpectedMessage "*Story 06*"

            { & $script:repo.PrepareSchemaScript -Extensions sample } |
                Should -Throw -ExpectedMessage "*Story 06*"
        }

        It "stages core plus extension schemas and records the Story 00 manifest contract" {
            $schemaDir = New-ApiSchemaSet -Extensions @("Sample")

            Invoke-PrepareSchema -ApiSchemaPath $schemaDir
            $manifest = Get-RootManifest

            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                Should -BeTrue
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Sample/ApiSchema.json") |
                Should -BeTrue
            $manifest.schema.selectionMode | Should -Be "ApiSchemaPath"
            $manifest.schema.selectedExtensions | Should -Contain "sample"
            $manifest.schema.effectiveSchemaHash | Should -Be $script:hashA
            $manifest.schema.apiSchemaManifestPath | Should -Be "ApiSchema/bootstrap-api-schema-manifest.json"
        }

        It "copies optional schema-adjacent runtime content and preserves schema JSON payloads" {
            $schemaDir = New-ApiSchemaSet
            "discovery" | Set-Content -LiteralPath (Join-Path $schemaDir "discovery-spec.json") -Encoding utf8
            New-Item -ItemType Directory -Path (Join-Path $schemaDir "xsd") -Force | Out-Null
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $schemaDir "xsd/Ed-Fi-Core.xsd") -Encoding utf8

            Invoke-PrepareSchema -ApiSchemaPath $schemaDir
            $apiSchemaManifest = Get-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json") -Raw |
                ConvertFrom-Json
            $stagedCore = Get-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") -Raw |
                ConvertFrom-Json

            $apiSchemaManifest.projects[0].discoverySpecPath | Should -Be "content/Ed-Fi/discovery-spec.json"
            $apiSchemaManifest.projects[0].xsdDirectory | Should -Be "content/Ed-Fi/xsd"
            $stagedCore.projectSchema.openApiBaseDocuments.resources.openapi | Should -Be "3.0.1"
        }

        It "detects normalized-path collisions before finalizing a workspace" {
            $schemaDir = Join-Path $script:repo.RepoRoot "schema"
            New-ApiSchemaFile -Path (Join-Path $schemaDir "ApiSchema.json") -ProjectName "Ed-Fi" -ProjectEndpointName "ed-fi" -IsExtensionProject $false
            New-ApiSchemaFile -Path (Join-Path $schemaDir "ApiSchema-SameA-EXTENSION.json") -ProjectName "Same!" -ProjectEndpointName "same-a" -IsExtensionProject $true
            New-ApiSchemaFile -Path (Join-Path $schemaDir "ApiSchema-SameB-EXTENSION.json") -ProjectName "Same?" -ProjectEndpointName "same-b" -IsExtensionProject $true

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir } |
                Should -Throw -ExpectedMessage "*Normalized path collision*"
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                Should -BeFalse
        }

        It "reuses identical workspaces and rejects changed schema selections" {
            $schemaDir = New-ApiSchemaSet -Extensions @("Sample")

            Invoke-PrepareSchema -ApiSchemaPath $schemaDir
            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir } |
                Should -Not -Throw
            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir -Hash $script:hashB } |
                Should -Throw -ExpectedMessage "*effective schema hash mismatch*"
        }

        It "accepts ApiSchema*.json files without the -EXTENSION suffix and discovers them recursively" {
            $schemaDir = Join-Path $script:repo.RepoRoot "schema"
            New-ApiSchemaFile -Path (Join-Path $schemaDir "ApiSchema.json") -ProjectName "Ed-Fi" -ProjectEndpointName "ed-fi" -IsExtensionProject $false
            New-ApiSchemaFile -Path (Join-Path $schemaDir "nested/ApiSchema-Sample.json") -ProjectName "Sample" -ProjectEndpointName "sample" -IsExtensionProject $true

            Invoke-PrepareSchema -ApiSchemaPath $schemaDir
            $manifest = Get-RootManifest

            $manifest.schema.selectedExtensions | Should -Contain "sample"
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Sample/ApiSchema.json") | Should -BeTrue
        }

        It "rejects ambiguous schema-adjacent content ownership when extensions share a directory with discovery/xsd content" {
            $schemaDir = Join-Path $script:repo.RepoRoot "schema"
            New-ApiSchemaFile -Path (Join-Path $schemaDir "ApiSchema.json") -ProjectName "Ed-Fi" -ProjectEndpointName "ed-fi" -IsExtensionProject $false
            $sharedDir = Join-Path $schemaDir "shared"
            New-ApiSchemaFile -Path (Join-Path $sharedDir "ApiSchema-First.json") -ProjectName "First" -ProjectEndpointName "first" -IsExtensionProject $true
            New-ApiSchemaFile -Path (Join-Path $sharedDir "ApiSchema-Second.json") -ProjectName "Second" -ProjectEndpointName "second" -IsExtensionProject $true
            # Schema-adjacent content in the shared dir is what makes ownership ambiguous.
            "discovery" | Set-Content -LiteralPath (Join-Path $sharedDir "discovery-spec.json") -Encoding utf8

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir } |
                Should -Throw -ExpectedMessage "*Ambiguous schema-adjacent content ownership*"
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") | Should -BeFalse
        }

        It "accepts multiple extensions sharing a directory without a core schema when no schema-adjacent content is present" {
            $schemaDir = Join-Path $script:repo.RepoRoot "schema"
            New-ApiSchemaFile -Path (Join-Path $schemaDir "ApiSchema.json") -ProjectName "Ed-Fi" -ProjectEndpointName "ed-fi" -IsExtensionProject $false
            $sharedDir = Join-Path $schemaDir "shared"
            New-ApiSchemaFile -Path (Join-Path $sharedDir "ApiSchema-First.json") -ProjectName "First" -ProjectEndpointName "first" -IsExtensionProject $true
            New-ApiSchemaFile -Path (Join-Path $sharedDir "ApiSchema-Second.json") -ProjectName "Second" -ProjectEndpointName "second" -IsExtensionProject $true

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir } | Should -Not -Throw
            $manifest = Get-RootManifest
            $manifest.schema.selectedExtensions | Should -Contain "first"
            $manifest.schema.selectedExtensions | Should -Contain "second"
        }

        It "shares discovery-spec and xsd content across all projects in a single source directory" {
            $schemaDir = New-ApiSchemaSet -Extensions @("Sample", "Homograph")
            "discovery" | Set-Content -LiteralPath (Join-Path $schemaDir "discovery-spec.json") -Encoding utf8
            New-Item -ItemType Directory -Path (Join-Path $schemaDir "xsd") -Force | Out-Null
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $schemaDir "xsd/Ed-Fi-Core.xsd") -Encoding utf8

            Invoke-PrepareSchema -ApiSchemaPath $schemaDir
            $apiSchemaManifest = Get-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json") -Raw |
                ConvertFrom-Json

            # Every project in the shared directory references the same staged content paths.
            $sharedDiscovery = $apiSchemaManifest.projects[0].discoverySpecPath
            $sharedXsd = $apiSchemaManifest.projects[0].xsdDirectory
            $sharedDiscovery | Should -Not -BeNullOrEmpty
            foreach ($project in $apiSchemaManifest.projects) {
                $project.discoverySpecPath | Should -Be $sharedDiscovery
                $project.xsdDirectory | Should -Be $sharedXsd
            }
        }

        It "fails when dms-schema hashing fails" {
            $schemaDir = New-ApiSchemaSet

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir -ToolExitCode 1 } |
                Should -Throw -ExpectedMessage "*dms-schema hash failed*"
        }

        It "fails fast when manifest has stale claims/seed sections but ApiSchema workspace is missing" {
            $schemaDir = New-ApiSchemaSet

            # Seed the manifest with stale claims and seed sections but no schema section
            # and no .bootstrap/ApiSchema directory present (partial prior state)
            $staleManifest = [ordered]@{
                version = 1
                claims = [ordered]@{
                    mode = "Embedded"
                    directory = "claims"
                }
                seed = [ordered]@{
                    extensionNamespacePrefixes = @()
                }
            }
            $bootstrapRoot = Join-Path $script:repo.DockerComposeRoot ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
            $staleManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $bootstrapRoot "bootstrap-manifest.json") -Encoding utf8

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir } |
                Should -Throw -ExpectedMessage "*stale claims/seed sections*"

            # No ApiSchema workspace should have been created
            Test-Path -LiteralPath (Join-Path $bootstrapRoot "ApiSchema") |
                Should -BeFalse

            # The manifest must not have been modified: claims/seed still present, no schema section added
            $manifestAfter = Get-Content -LiteralPath (Join-Path $bootstrapRoot "bootstrap-manifest.json") -Raw |
                ConvertFrom-Json
            $manifestAfter.claims | Should -Not -BeNullOrEmpty
            $manifestAfter.seed | Should -Not -BeNullOrEmpty
            $manifestAfter.schema | Should -BeNullOrEmpty
        }
    }

    Context "claims staging" {
        It "writes Embedded claims mode for core-only schema" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet)

            Invoke-PrepareClaim
            $manifest = Get-RootManifest

            $manifest.claims.mode | Should -Be "Embedded"
            @(Get-ChildItem -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims") -File).Count |
                Should -Be 0
        }

        It "auto-stages Sample and Homograph fragments without staging baseline fragments" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample", "Homograph"))

            Invoke-PrepareClaim
            $manifest = Get-RootManifest
            $claimFiles = @(Get-ChildItem -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims") -File | ForEach-Object Name)

            $manifest.claims.mode | Should -Be "Hybrid"
            $claimFiles | Should -Contain "004-sample-extension-claimset.json"
            $claimFiles | Should -Contain "005-homograph-extension-claimset.json"
            $claimFiles | Should -Not -Contain "001-namespace-claimset.json"
            $manifest.seed.extensionNamespacePrefixes | Should -Contain "uri://sample.ed-fi.org"
        }

        It "requires caller-supplied claims for unmapped extensions such as TPDM" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))

            { Invoke-PrepareClaim } |
                Should -Throw -ExpectedMessage "*ClaimsDirectoryPath is required*TPDM*"
        }

        It "stages caller fragments and records expected verification checks with the fragment's raw resource name" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "custom-claims"
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "010-tpdm-claimset.json")

            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir
            $manifest = Get-RootManifest

            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims/010-tpdm-claimset.json") |
                Should -BeTrue
            # Story 00 records the fragment's resource name verbatim. CMS (or a future shared
            # composer helper) owns matching it against the composed hierarchy at startup.
            $check = @($manifest.claims.expectedVerificationChecks |
                Where-Object { $_.resourceClaim -eq "http://example.org/identity/claims/widget" })[0]
            $check.claimSetName | Should -Be "EdFiSandbox"
            $check.action | Should -Be "Read"
        }

        It "records explicit parent claimSets readiness checks structurally" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "parent-explicit"
            $fragment = [ordered]@{
                resourceClaims = @(
                    [ordered]@{
                        isParent = $true
                        name = "http://example.org/identity/claims/domains/explicitParent"
                        claimSets = @(
                            [ordered]@{
                                name = "EdFiSandbox"
                                actions = @(
                                    [ordered]@{
                                        name = "Read"
                                    }
                                )
                            }
                        )
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-parent-explicit-claimset.json") -Encoding utf8

            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir
            $manifest = Get-RootManifest

            $check = @($manifest.claims.expectedVerificationChecks |
                Where-Object { $_.resourceClaim -eq "http://example.org/identity/claims/domains/explicitParent" })[0]
            $check.claimSetName | Should -Be "EdFiSandbox"
            $check.action | Should -Be "Read"
        }

        It "rejects explicit claimSets on non-parent resource claims because CMS does not compose them" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "non-parent-explicit"
            $fragment = [ordered]@{
                resourceClaims = @(
                    [ordered]@{
                        isParent = $false
                        name = "http://example.org/identity/claims/widgets"
                        claimSets = @(
                            [ordered]@{
                                name = "EdFiSandbox"
                                actions = @(
                                    [ordered]@{
                                        name = "Read"
                                    }
                                )
                            }
                        )
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-non-parent-explicit-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*non-parent resourceClaims entry with claimSets*"
        }

        It "rejects malformed, duplicate, or unknown claim fragments" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))

            $badJsonDir = Join-Path $script:repo.RepoRoot "bad-json"
            New-Item -ItemType Directory -Path $badJsonDir -Force | Out-Null
            "{" | Set-Content -LiteralPath (Join-Path $badJsonDir "010-bad-claimset.json") -Encoding utf8
            { Invoke-PrepareClaim -ClaimsDirectoryPath $badJsonDir } |
                Should -Throw -ExpectedMessage "*malformed JSON*"

            $unknownDir = Join-Path $script:repo.RepoRoot "unknown"
            New-ExplicitClaimsetFragment -Path (Join-Path $unknownDir "010-unknown-claimset.json") -ClaimSetName "NotAClaimSet"
            { Invoke-PrepareClaim -ClaimsDirectoryPath $unknownDir } |
                Should -Throw -ExpectedMessage "*unknown effective claim set*"
        }

        It "accepts parent-only fragments without a top-level claim-set name" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "parent-only"
            $fragment = [ordered]@{
                resourceClaims = @(
                    [ordered]@{
                        isParent = $true
                        name = "http://example.org/identity/claims/domain"
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-parent-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Not -Throw
        }

        It "reuses identical claims workspaces and rejects changed fragment sets" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "custom-claims"
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "010-tpdm-claimset.json")

            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir
            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Not -Throw

            # Use a distinct resource URI so this assertion exercises the fingerprint-mismatch path.
            New-ExplicitClaimsetFragment `
                -Path (Join-Path $claimsDir "011-extra-claimset.json") `
                -ResourceClaim "http://example.org/identity/claims/anotherWidget"
            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*claims fingerprint mismatch*"
        }

        It "rejects a fragment whose resourceClaims contains a non-object entry" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "bad-resource-claims"
            $fragment = [ordered]@{
                name = "EdFiSandbox"
                resourceClaims = @("not-an-object")
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-bad-resource-claims-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*not a JSON object*"
        }

        It "rejects a parent resourceClaim missing 'name'" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "parent-missing-name"
            $fragment = [ordered]@{
                name = "FragLabel"
                resourceClaims = @(
                    [ordered]@{
                        isParent = $true
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-parent-missing-name-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*missing 'name'*"
        }

        It "requires a top-level name for implicit non-parent claim-set use" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "implicit"
            $fragment = [ordered]@{
                resourceClaims = @(
                    [ordered]@{
                        isParent = $false
                        name = "http://example.org/identity/claims/implicit"
                        authorizationStrategyOverridesForCRUD = @(
                            [ordered]@{
                                actionName = "Read"
                            }
                        )
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-implicit-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*missing top-level name*"
        }

        It "rejects an implicit non-parent resourceClaim missing 'name'" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "implicit-missing-name"
            $fragment = [ordered]@{
                name = "FragLabel"
                resourceClaims = @(
                    [ordered]@{
                        isParent = $false
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $claimsDir "010-implicit-missing-name-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*missing 'name'*"
        }
    }

    Context "startup handoff" {
        It "validates the bootstrap manifest without activating staged DMS schema or staged CMS claims startup" {
            # Story 04 boundary: with a valid bootstrap manifest present, Set-BootstrapStartupEnvironment
            # returns $true but does not activate either side of the staged runtime pair. DMS falls
            # back to its built-in DLL-backed schemas, so CMS must not be pointed at staged .bootstrap/claims.
            # The existing claims source/directory are left intact for the non-staged path.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            $env:DMS_CONFIG_CLAIMS_SOURCE = "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"
            $env:SCHEMA_PACKAGES = "ambient-packages"

            Set-BootstrapStartupEnvironment | Should -BeTrue

            $env:USE_API_SCHEMA_PATH | Should -BeNullOrEmpty
            $env:API_SCHEMA_PATH | Should -BeNullOrEmpty
            $env:SCHEMA_PACKAGES | Should -Be "ambient-packages"
            $env:DMS_API_SCHEMA_MOUNT_SOURCE | Should -BeNullOrEmpty

            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -BeNullOrEmpty
        }

        It "explicitly blanks process-env schema vars in bootstrap mode so docker compose --env-file cannot leak UseApiSchemaPath" {
            # The repo .env / .env.example / .env.e2e set USE_API_SCHEMA_PATH=true and
            # API_SCHEMA_PATH=/app/ApiSchema for the eventual Story 04 end state. Until Story 04
            # ships ContentProvider's loose-JSON path, the bootstrap helper must override those
            # values in the process environment so compose's ${VAR:-default} falls back to
            # UseApiSchemaPath=false + empty ApiSchemaPath, keeping DMS on built-in DLL-backed assemblies.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            $env:USE_API_SCHEMA_PATH = "true"
            $env:API_SCHEMA_PATH = "/app/ApiSchema"

            Set-BootstrapStartupEnvironment | Should -BeTrue

            $env:USE_API_SCHEMA_PATH | Should -BeNullOrEmpty
            $env:API_SCHEMA_PATH | Should -BeNullOrEmpty
        }

        It "clears stale process-env claims mount source in bootstrap mode until staged runtime startup is enabled" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            # Simulate process env populated by an outer caller. Source/directory remain caller-owned
            # for DLL-backed startup, while mount source is cleared to avoid staged .bootstrap/claims.
            $env:DMS_CONFIG_CLAIMS_SOURCE = "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"

            Set-BootstrapStartupEnvironment | Should -BeTrue

            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -BeNullOrEmpty
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -BeNullOrEmpty
        }

        It "keeps AddExtensionSecurityMetadata working when a bootstrap manifest exists but staged runtime startup is deferred" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            $env:DMS_CONFIG_CLAIMS_SOURCE = "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"

            Invoke-BootstrapStartupConfiguration -AddExtensionSecurityMetadata

            $env:USE_API_SCHEMA_PATH | Should -BeNullOrEmpty
            $env:API_SCHEMA_PATH | Should -BeNullOrEmpty
            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -BeNullOrEmpty
        }

        It "Invoke-BootstrapStartupConfiguration does not depend on a return value so callers can snapshot env independently" {
            # Reviewer concern: if the helper threw before assigning $bootstrapStartup, the caller's
            # finally would null-bind on $bootstrapStartup.EnvSnapshot and skip Pop-Location. The
            # helper now returns nothing; callers capture Get-BootstrapEnvSnapshot themselves before
            # Push-Location, so Restore + Pop always run cleanly.
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            $result = Invoke-BootstrapStartupConfiguration -IsTeardown -AddExtensionSecurityMetadata:$false
            $result | Should -BeNullOrEmpty
        }

        It "keeps bootstrap mounts out of the default DMS service and leaves Config Service mount source optional" {
            # bootstrap-dms.yml is removed in Story 00 (Story 04 will re-introduce runtime DMS wiring).
            # local-dms.yml and published-dms.yml must not carry additional-claims or ApiSchema mounts.
            # Config Service keeps the mount-source env hook for the non-bootstrap transition path and
            # the future Story 04 staged claims activation.
            $localDms = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "local-dms.yml") -Raw
            $publishedDms = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "published-dms.yml") -Raw
            $localConfig = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "local-config.yml") -Raw

            $localDms | Should -Not -Match "/app/additional-claims"
            $publishedDms | Should -Not -Match "/app/additional-claims"
            $localDms | Should -Not -Match "/app/ApiSchema:ro"
            $localConfig | Should -Match "DMS_CONFIG_CLAIMS_MOUNT_SOURCE"

            # bootstrap-dms.yml must not exist until Story 04 re-enables the runtime DMS bootstrap path
            Test-Path -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-dms.yml") | Should -BeFalse
        }

        It "retains AddExtensionSecurityMetadata as a transitional non-bootstrap hybrid claims path" {
            # The -AddExtensionSecurityMetadata switch is kept in the startup wrappers and build script
            # as a transitional helper for DLL-backed (non-bootstrap) E2E until Story 04 moves runtime
            # loading onto the staged bootstrap workspace. It must be present in all three files.
            foreach ($path in @(
                (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"),
                (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"),
                (Join-Path $script:sourceRepoRoot "build-dms.ps1")
            )) {
                $content = Get-Content -LiteralPath $path -Raw
                $content | Should -Match "AddExtensionSecurityMetadata"
            }
        }

        It "sets Hybrid claims env vars when AddExtensionSecurityMetadata is passed without a bootstrap manifest" {
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            Invoke-BootstrapStartupConfiguration -AddExtensionSecurityMetadata

            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
        }

        It "clears DMS_CONFIG_CLAIMS_MOUNT_SOURCE when AddExtensionSecurityMetadata is passed without a bootstrap manifest" {
            # Pre-set a stale ambient value that a prior bootstrap session might have left behind.
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"

            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            Invoke-BootstrapStartupConfiguration -AddExtensionSecurityMetadata

            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -BeNullOrEmpty
        }

        It "leaves non-bootstrap schema env vars untouched when AddExtensionSecurityMetadata is passed" {
            # .env.e2e uses USE_API_SCHEMA_PATH=true plus SCHEMA_PACKAGES to load Sample/Homograph
            # schema packages. The transitional non-bootstrap helper only enables Hybrid claims.
            $env:USE_API_SCHEMA_PATH = "true"
            $env:API_SCHEMA_PATH = "/app/ApiSchema"

            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            Invoke-BootstrapStartupConfiguration -AddExtensionSecurityMetadata

            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -Be "/app/ApiSchema"
        }

        It "restores bootstrap environment variables through the snapshot helper" {
            # Snapshot/restore covers the process env vars this helper blanks or mutates while preserving
            # the Story 04 boundary: SCHEMA_PACKAGES and DMS_API_SCHEMA_MOUNT_SOURCE are not managed here.
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force
            $env:DMS_CONFIG_CLAIMS_SOURCE = "existing"
            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_CLAIMS_DIRECTORY", $null)
            $env:USE_API_SCHEMA_PATH = "true"
            [System.Environment]::SetEnvironmentVariable("API_SCHEMA_PATH", $null)
            $snapshot = Get-BootstrapEnvSnapshot
            $env:DMS_CONFIG_CLAIMS_SOURCE = "mutated"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
            $env:USE_API_SCHEMA_PATH = ""
            $env:API_SCHEMA_PATH = "/app/ApiSchema"

            Restore-BootstrapEnvSnapshot -Snapshot $snapshot

            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "existing"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -BeNullOrEmpty
            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -BeNullOrEmpty
        }
    }

    Context "E2E wrappers" {
        It "keeps E2E setup on the DLL-backed schema path until Story 04" {
            foreach ($path in @(
                (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"),
                (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1")
            )) {
                $content = Get-Content -LiteralPath $path -Raw
                $content | Should -Not -Match "UseBootstrapWorkspace"
                $content | Should -Not -Match "prepare-dms-schema"
                $content | Should -Not -Match "prepare-dms-claims"
                $content | Should -Match "DLL-backed schema"
            }
        }

        It "passes AddExtensionSecurityMetadata in E2E setup scripts to enable Hybrid claims for extension schemas" {
            # Confirm that both E2E setup wrappers pass -AddExtensionSecurityMetadata to start-local-dms.ps1
            # so extension claimset fragments (e.g. Sample, Homograph) are loaded in non-bootstrap mode.
            foreach ($path in @(
                (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"),
                (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1")
            )) {
                $content = Get-Content -LiteralPath $path -Raw
                $content | Should -Match "AddExtensionSecurityMetadata"
            }
        }

        It "build-dms.ps1 teardown invocations include -RemoveBootstrap to wipe stale bootstrap workspace" {
            # Confirm that both teardown invocations in Start-DockerEnvironment pass -RemoveBootstrap so
            # a manually-staged .bootstrap/ from a developer session cannot hijack the subsequent E2E start.
            $buildScript = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "build-dms.ps1") -Raw
            $buildScript | Should -Match "start-local-dms\.ps1.*-d.*-v.*-RemoveBootstrap"
            $buildScript | Should -Match "start-published-dms\.ps1.*-d.*-v.*-RemoveBootstrap"
        }

        It "E2E setup wrappers contain defensive .bootstrap removal step before DLL-backed startup" {
            # Confirm that both E2E setup wrappers defensively remove .bootstrap/ before invoking
            # start-local-dms.ps1 so a stale bootstrap workspace cannot hijack the DLL-backed run
            # even when a developer skips teardown between sessions.
            foreach ($path in @(
                (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"),
                (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1")
            )) {
                $content = Get-Content -LiteralPath $path -Raw
                $content | Should -Match "Remove-Item -LiteralPath"
                $content | Should -Match "\.bootstrap"
            }
        }
    }
}
