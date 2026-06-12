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
        # These bootstrap-managed env vars are snapshotted and restored around each test so that
        # incidental host env state does not bleed into assertions. Set-BootstrapStartupEnvironment
        # activates them (schema + claims) when a valid manifest is present; they stay blank when
        # no manifest is present.
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

        foreach ($fileName in @("bootstrap-manifest.psm1", "bootstrap-schema-tool.psm1", "prepare-dms-schema.ps1", "prepare-dms-claims.ps1")) {
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $fileName) -Destination $dockerComposeRoot
        }

        $claimsSourceRoot = Join-Path $script:sourceRepoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend"
        $claimsTargetRoot = Join-Path $repoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend"
        New-Item -ItemType Directory -Path $claimsTargetRoot -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $claimsSourceRoot "Claims") -Destination $claimsTargetRoot -Recurse
        Copy-Item -LiteralPath (Join-Path $claimsSourceRoot "Deploy") -Destination $claimsTargetRoot -Recurse

        # Copy JsonSchemaForApiSchema.json so prepare-dms-schema.ps1 can include it in the staged workspace.
        $jsonSchemaSourceDir = Join-Path $script:sourceRepoRoot "src/dms/core/EdFi.DataManagementService.Core/ApiSchema"
        $jsonSchemaTargetDir = Join-Path $repoRoot "src/dms/core/EdFi.DataManagementService.Core/ApiSchema"
        New-Item -ItemType Directory -Path $jsonSchemaTargetDir -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $jsonSchemaSourceDir "JsonSchemaForApiSchema.json") -Destination $jsonSchemaTargetDir

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
            # DMS-1153: user-supplied fragment checks carry stagedOnly so the claims-ready
            # gate defers them — CMS does not load the staged claims workspace until
            # staged-claims activation (DMS-1154), so asserting them would fail a valid
            # bootstrap whose fragments are staged but not yet active.
            $check.stagedOnly | Should -BeTrue
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
            # DMS-1153: parent-derived checks carry isParent so the claims-ready gate can
            # defer them — parent grants materialize on leaf descendants via lineage and the
            # parent name never appears in /authorizationMetadata claims[].
            $check.isParent | Should -BeTrue
            # User-supplied fragments additionally carry stagedOnly (not loaded by CMS
            # until staged-claims activation, DMS-1154).
            $check.stagedOnly | Should -BeTrue
        }

        It "records the embedded baseline probe against a leaf resource claim, not a domain parent" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet)
            Invoke-PrepareClaim
            $manifest = Get-RootManifest

            $checks = @($manifest.claims.expectedVerificationChecks)
            $checks.Count | Should -BeGreaterOrEqual 1
            $baseline = $checks[0]
            $baseline.claimSetName | Should -Be "EdFiSandbox"
            # CMS /authorizationMetadata flattens the claims hierarchy to leaf resource claims
            # only (verified live in DMS-1153); a probe against a domains/* parent can never
            # verify. schoolYearType is the edFiTypes domain's leaf child in embedded claims.
            $baseline.resourceClaim | Should -Be "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"
            $baseline.action | Should -Be "Read"
            $baseline.resourceClaim | Should -Not -Match "/domains/" -Because "domain parents are never serialized in /authorizationMetadata claims[] and would hard-fail every gate run"
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

        It "discovers caller fragments recursively to match CMS ClaimsFragmentComposer" {
            # CMS scans -ClaimsDirectoryPath with SearchOption.AllDirectories. Bootstrap input
            # discovery must match so nested fragments are staged (and nested duplicates rejected).
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "nested-claims"
            $nestedDir = Join-Path $claimsDir "subdir"
            New-ExplicitClaimsetFragment -Path (Join-Path $nestedDir "010-tpdm-claimset.json")

            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir

            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims/010-tpdm-claimset.json") |
                Should -BeTrue
        }

        It "detects nested filename collisions across subdirectories" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "nested-collision"
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "a/010-tpdm-claimset.json")
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "b/010-tpdm-claimset.json")

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*filename collision*"
        }

        It "rejects a non-boolean isParent value because CMS deserializes IsParent as a strict bool" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "string-isparent"
            $fragment = [ordered]@{
                name = "EdFiSandbox"
                resourceClaims = @(
                    [ordered]@{
                        isParent = "true"
                        name = "http://example.org/identity/claims/widget"
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $claimsDir "010-string-isparent-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*malformed boolean for 'isParent'*"
        }

        It "rejects a resourceClaims value that is a single object instead of an array" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "scalar-resourceclaims"
            $fragment = [ordered]@{
                name = "EdFiSandbox"
                resourceClaims = [ordered]@{
                    isParent = $false
                    name = "http://example.org/identity/claims/widget"
                }
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $claimsDir "010-scalar-resourceclaims-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*resourceClaims value that is not a JSON array*"
        }

        It "rejects a claimSets value that is a single object instead of an array" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "scalar-claimsets"
            $fragment = [ordered]@{
                resourceClaims = @(
                    [ordered]@{
                        isParent = $true
                        name = "http://example.org/identity/claims/domains/explicitParent"
                        claimSets = [ordered]@{
                            name = "EdFiSandbox"
                            actions = @(
                                [ordered]@{
                                    name = "Read"
                                }
                            )
                        }
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $claimsDir "010-scalar-claimsets-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*claimSets value that is not a JSON array*"
        }

        It "rejects an actions value that is a single object instead of an array" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "scalar-actions"
            $fragment = [ordered]@{
                resourceClaims = @(
                    [ordered]@{
                        isParent = $true
                        name = "http://example.org/identity/claims/domains/explicitParent"
                        claimSets = @(
                            [ordered]@{
                                name = "EdFiSandbox"
                                actions = [ordered]@{
                                    name = "Read"
                                }
                            }
                        )
                    }
                )
            }
            New-Item -ItemType Directory -Path $claimsDir -Force | Out-Null
            $fragment | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $claimsDir "010-scalar-actions-claimset.json") -Encoding utf8

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*claimSets actions value that is not a JSON array*"
        }

        It "fails fast when manifest has stale claims/seed sections but claims workspace is missing" {
            # Symmetric to the prepare-dms-schema.ps1 guard: a partial prior state (manifest
            # records claims/seed but .bootstrap/claims was removed) must not silently rewrite
            # the manifest sections; teardown is the required remediation.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            $claimsDir = Join-Path $script:repo.RepoRoot "stale-claims-input"
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "010-tpdm-claimset.json")
            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir

            $stagedClaims = Join-Path $script:repo.BootstrapRoot "claims"
            Remove-Item -LiteralPath $stagedClaims -Recurse -Force

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*stale claims/seed sections*"

            Test-Path -LiteralPath $stagedClaims | Should -BeFalse
        }
    }

    Context "startup handoff" {
        It "activates staged DMS schema and CMS claims env vars when a valid bootstrap manifest is present" {
            # With a valid bootstrap manifest, Set-BootstrapStartupEnvironment returns $true and
            # activates both the staged schema workspace (USE_API_SCHEMA_PATH, API_SCHEMA_PATH,
            # DMS_API_SCHEMA_MOUNT_SOURCE, SCHEMA_PACKAGES cleared) and staged claims per
            # manifest claims.mode. The "Sample" extension produces Hybrid mode.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            # Pre-set stale process-env values to confirm bootstrap activation overrides them.
            $env:DMS_CONFIG_CLAIMS_SOURCE = "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"
            $env:SCHEMA_PACKAGES = "ambient-packages"

            Set-BootstrapStartupEnvironment | Should -BeTrue

            # Schema activation: USE_API_SCHEMA_PATH and API_SCHEMA_PATH are set; SCHEMA_PACKAGES
            # is cleared so run.sh performs no second download; DMS_API_SCHEMA_MOUNT_SOURCE is the
            # host-side source for bootstrap-dms.yml.
            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -Be "/app/ApiSchema"
            $env:SCHEMA_PACKAGES | Should -BeNullOrEmpty
            $env:DMS_API_SCHEMA_MOUNT_SOURCE | Should -Not -BeNullOrEmpty

            # Claims activation: manifest claims.mode=Hybrid (Sample extension) overrides the
            # stale Embedded process-env values.
            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -Not -BeNullOrEmpty
        }

        It "activates staged schema env vars in bootstrap mode and sets DMS_API_SCHEMA_MOUNT_SOURCE to the staged workspace" {
            # Bootstrap mode sets USE_API_SCHEMA_PATH=true, API_SCHEMA_PATH=/app/ApiSchema, and
            # DMS_API_SCHEMA_MOUNT_SOURCE to the absolute .bootstrap/ApiSchema path. SCHEMA_PACKAGES
            # is cleared so run.sh performs no second package download into the mounted workspace.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            # Pre-set values to confirm bootstrap activation writes the correct activation values.
            $env:USE_API_SCHEMA_PATH = "false"
            $env:API_SCHEMA_PATH = ""

            Set-BootstrapStartupEnvironment | Should -BeTrue

            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -Be "/app/ApiSchema"
            $env:DMS_API_SCHEMA_MOUNT_SOURCE | Should -Not -BeNullOrEmpty
            $env:SCHEMA_PACKAGES | Should -BeNullOrEmpty
        }

        It "manifest claims.mode governs in bootstrap mode and overrides caller-set process-env claims values" {
            # Bootstrap mode: manifest claims.mode is authoritative. "Sample" extension produces
            # Hybrid mode; any pre-set Embedded process-env values are overridden by activation.
            # DMS_CONFIG_CLAIMS_MOUNT_SOURCE is set to the staged .bootstrap/claims path.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            # Simulate process env set to Embedded by a prior caller; manifest says Hybrid.
            $env:DMS_CONFIG_CLAIMS_SOURCE = "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"

            Set-BootstrapStartupEnvironment | Should -BeTrue

            # Manifest claims.mode=Hybrid overrides the Embedded process-env values.
            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -Not -BeNullOrEmpty
        }

        It "Embedded manifest claims.mode overrides pre-set Hybrid process-env values in bootstrap mode" {
            # Core-only schema produces Embedded claims mode; the manifest governs even when the
            # process env was previously set to Hybrid (e.g. from .env defaults).
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet)
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            # Simulate Hybrid values left in process env from a prior session.
            $env:DMS_CONFIG_CLAIMS_SOURCE = "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/claims/path"

            Set-BootstrapStartupEnvironment | Should -BeTrue

            # Manifest claims.mode=Embedded overrides Hybrid; mount source is blanked.
            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -BeNullOrEmpty
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -BeNullOrEmpty
        }

        It "AddExtensionSecurityMetadata flag is ignored in bootstrap mode; manifest claims.mode governs" {
            # In bootstrap mode the manifest's claims.mode is authoritative. Passing
            # -AddExtensionSecurityMetadata has no effect on the claims activation: the
            # manifest-driven Hybrid (from Sample) is already applied by Set-BootstrapStartupEnvironment,
            # and the flag does not override it. Schema vars ARE set by bootstrap activation.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample"))
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            $env:DMS_CONFIG_CLAIMS_SOURCE = "Embedded"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = "/some/stale/path"

            Invoke-BootstrapStartupConfiguration -AddExtensionSecurityMetadata

            # Schema vars activated by bootstrap (flag does not touch these).
            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -Be "/app/ApiSchema"
            # Manifest claims.mode=Hybrid governs; flag does not override.
            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -Not -BeNullOrEmpty
        }

        It "Invoke-BootstrapStartupConfiguration returns a clean boolean so start scripts can conditionally append bootstrap-dms.yml" {
            # Callers (start-local-dms.ps1 / start-published-dms.ps1) capture the return value to
            # decide whether to append -f bootstrap-dms.yml to the compose file set. The helper must
            # return a boolean and must not emit status text on the PowerShell success output stream
            # (Write-Host is allowed; Write-Output / pipeline output is not).
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            # Without a manifest: must return $false cleanly, no extra output on the success stream.
            $outputNoManifest = Invoke-BootstrapStartupConfiguration -IsTeardown -AddExtensionSecurityMetadata:$false
            $outputNoManifest | Should -BeOfType [bool]
            $outputNoManifest | Should -BeFalse

            # With a manifest: must return $true cleanly.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet)
            Invoke-PrepareClaim
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force

            $outputWithManifest = Invoke-BootstrapStartupConfiguration
            $outputWithManifest | Should -BeOfType [bool]
            $outputWithManifest | Should -BeTrue
        }

        It "bootstrap-dms.yml exists with the dms volume override and is conditionally included by start scripts" {
            # bootstrap-dms.yml overrides the dms service with the staged ApiSchema volume mount.
            # local-dms.yml and published-dms.yml must NOT carry the ApiSchema mount directly (they
            # rely on bootstrap-dms.yml, included only when bootstrap mode is active). Config Service
            # keeps its mount-source env hook for bootstrap-mode claims activation.
            $localDms = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "local-dms.yml") -Raw
            $publishedDms = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "published-dms.yml") -Raw
            $localConfig = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "local-config.yml") -Raw

            $localDms | Should -Not -Match "/app/additional-claims"
            $publishedDms | Should -Not -Match "/app/additional-claims"
            $localDms | Should -Not -Match "/app/ApiSchema:ro"
            $localConfig | Should -Match "DMS_CONFIG_CLAIMS_MOUNT_SOURCE"

            # bootstrap-dms.yml must exist and declare the dms service volume override.
            $bootstrapDmsYml = Join-Path $script:sourceDockerComposeRoot "bootstrap-dms.yml"
            Test-Path -LiteralPath $bootstrapDmsYml | Should -BeTrue
            $bootstrapDmsContent = Get-Content -LiteralPath $bootstrapDmsYml -Raw
            $bootstrapDmsContent | Should -Match "dms:"
            $bootstrapDmsContent | Should -Match "/app/ApiSchema:ro"
            $bootstrapDmsContent | Should -Match "DMS_API_SCHEMA_MOUNT_SOURCE"

            # start-local-dms.ps1 and start-published-dms.ps1 must include bootstrap-dms.yml in
            # the compose file set when bootstrap mode is active (conditional on $bootstrapMode).
            foreach ($startScript in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot $startScript) -Raw
                $content | Should -Match "bootstrap-dms\.yml" -Because "$startScript must append -f bootstrap-dms.yml when bootstrap mode is active"
                $content | Should -Match "\`$bootstrapMode" -Because "$startScript must gate the bootstrap-dms.yml inclusion on the boolean returned by Invoke-BootstrapStartupConfiguration"
            }
        }

        It "retains AddExtensionSecurityMetadata as a transitional non-bootstrap hybrid claims path" {
            # The -AddExtensionSecurityMetadata switch is kept in the startup wrappers and build script
            # as a transitional helper for DLL-backed (non-bootstrap) E2E setups. It must be present
            # in all three files.
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

        It "restores bootstrap environment variables through the snapshot helper including DMS_API_SCHEMA_MOUNT_SOURCE and SCHEMA_PACKAGES" {
            # Snapshot/restore covers all bootstrap-managed env vars: USE_API_SCHEMA_PATH,
            # API_SCHEMA_PATH, DMS_API_SCHEMA_MOUNT_SOURCE, SCHEMA_PACKAGES (schema side) and
            # DMS_CONFIG_CLAIMS_SOURCE, DMS_CONFIG_CLAIMS_DIRECTORY, DMS_CONFIG_CLAIMS_MOUNT_SOURCE
            # (claims side). Both new vars (DMS_API_SCHEMA_MOUNT_SOURCE and SCHEMA_PACKAGES) are
            # now fully managed by Set-BootstrapStartupEnvironment and must round-trip correctly.
            Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
            Import-Module $script:repo.ManifestModule -Force
            $env:DMS_CONFIG_CLAIMS_SOURCE = "existing"
            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_CLAIMS_DIRECTORY", $null)
            $env:USE_API_SCHEMA_PATH = "true"
            [System.Environment]::SetEnvironmentVariable("API_SCHEMA_PATH", $null)
            $env:DMS_API_SCHEMA_MOUNT_SOURCE = "/prior/ApiSchema"
            $env:SCHEMA_PACKAGES = "prior-packages"
            $snapshot = Get-BootstrapEnvSnapshot
            $env:DMS_CONFIG_CLAIMS_SOURCE = "mutated"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
            $env:USE_API_SCHEMA_PATH = ""
            $env:API_SCHEMA_PATH = "/app/ApiSchema"
            $env:DMS_API_SCHEMA_MOUNT_SOURCE = "/mutated/ApiSchema"
            $env:SCHEMA_PACKAGES = "mutated-packages"

            Restore-BootstrapEnvSnapshot -Snapshot $snapshot

            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "existing"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -BeNullOrEmpty
            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -BeNullOrEmpty
            $env:DMS_API_SCHEMA_MOUNT_SOURCE | Should -Be "/prior/ApiSchema"
            $env:SCHEMA_PACKAGES | Should -Be "prior-packages"
        }

        It "start-local-dms.ps1 gates default connector registration on bootstrap mode in the -DmsOnly block" {
            # Bootstrap mode provisions the redesigned relational schema which does not include the
            # legacy dms.document table or the to_debezium publication that the default Debezium connector
            # requires. Both start scripts must check $bootstrapMode before calling setup-connectors.ps1
            # in the -DmsOnly block so the connector is never registered against a schema where its
            # required tables and publication do not exist.
            $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") -Raw

            # $bootstrapMode must participate in the connector gate (not just $SkipConnectorSetup alone)
            $content | Should -Match '\$bootstrapMode' -Because "start-local-dms.ps1 must gate the default connector on `$bootstrapMode so bootstrap mode never registers a connector against a schema without dms.document"

            # The script must still retain -SkipConnectorSetup for non-bootstrap harnesses
            $content | Should -Match 'SkipConnectorSetup' -Because "start-local-dms.ps1 must retain -SkipConnectorSetup for non-bootstrap harnesses (e.g. Instance Management E2E)"

            # The skip message for bootstrap mode must distinguish it from the explicit-flag path
            $content | Should -Match 'bootstrap mode provisions the redesigned relational schema' -Because "the bootstrap-mode skip message must explain why the connector is not registered"
        }

        It "start-published-dms.ps1 gates default connector registration on bootstrap mode in the -DmsOnly block" {
            $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1") -Raw

            $content | Should -Match '\$bootstrapMode' -Because "start-published-dms.ps1 must gate the default connector on `$bootstrapMode so bootstrap mode never registers a connector against a schema without dms.document"
            $content | Should -Match 'SkipConnectorSetup' -Because "start-published-dms.ps1 must retain -SkipConnectorSetup for non-bootstrap harnesses"
            $content | Should -Match 'bootstrap mode provisions the redesigned relational schema' -Because "the bootstrap-mode skip message must explain why the connector is not registered"
        }
    }

    Context "E2E wrappers" {
        It "keeps E2E setup on the DLL-backed schema path (non-bootstrap compatibility)" {
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

        It "InstanceManagement E2E setup does not pass -NoDataStore to start-local-dms.ps1 after de-scope" {
            # -NoDataStore was removed from start-local-dms.ps1 in DMS-1153. The InstanceManagement
            # E2E suite creates its own per-test databases rather than relying on the start script to
            # create a default data store, so the flag is simply dropped (not redirected to configure-local-data-store.ps1).
            $imSetup = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1") -Raw
            # Reject any start-local-dms.ps1 invocation that still carries the removed flag
            $imSetup | Should -Not -Match "start-local-dms\.ps1[^`n]*-NoDataStore"
        }

        It "DataManagementService E2E setup calls configure-local-data-store.ps1 to create the default data store" {
            # start-local-dms.ps1 no longer creates a data store automatically after DMS-1153.
            # The DataManagementService E2E setup must call configure-local-data-store.ps1 explicitly
            # so a default route-unqualified data store is present before the tests run.
            $dmsSetup = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1") -Raw
            $dmsSetup | Should -Match "configure-local-data-store\.ps1"
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
