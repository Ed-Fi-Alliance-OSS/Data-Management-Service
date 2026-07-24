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

        foreach ($fileName in @("bootstrap-manifest.psm1", "bootstrap-schema-catalog.psm1", "bootstrap-schema-tool.psm1", "bootstrap-package-resolver.psm1", "prepare-dms-schema.ps1", "prepare-dms-claims.ps1")) {
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

        $path = Join-Path $Directory "fake-api-schema-tools.ps1"
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

    function script:Add-KnownExtensionCatalogEntry {
        <#
        .SYNOPSIS
        Appends an extra KnownExtensionClaimsMetadata assignment to the isolated repo's copy of the catalog
        module so tests can exercise the catalog-validation branches in prepare-dms-claims.ps1 without
        touching the real (source) catalog. Call after BeforeEach has staged $script:repo; the isolated repo
        is discarded in AfterEach.
        #>
        param(
            [Parameter(Mandatory)]
            [string]
            $Key,

            # A hashtable literal for the entry body, e.g. '@{ FragmnetFileName = "x.json" }'.
            [Parameter(Mandatory)]
            [string]
            $Body
        )

        $catalogPath = Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-catalog.psm1"
        $content = Get-Content -LiteralPath $catalogPath -Raw
        # Append the assignment after the built-in entries (which create the Ordinal-backed map) but
        # before the first function, so the module-scope statement runs at import. Anchoring on the first
        # function is stable across catalog edits and appends a self-contained statement rather than
        # splicing into a literal.
        $anchor = "function Get-StandardSchemaFeed"
        $index = $content.IndexOf($anchor)
        if ($index -lt 0) {
            throw "Could not find catalog function anchor in $catalogPath"
        }
        $statement = "`$script:KnownExtensionClaimsMetadata[`"$Key`"] = $Body`n`n"
        $content = $content.Insert($index, $statement)
        Set-Content -LiteralPath $catalogPath -Value $content -Encoding utf8
    }

    function script:Resolve-EmbeddedClaimLeaf {
        <#
        .SYNOPSIS
        Walks the embedded Claims.json claimsHierarchy for a resource-claim leaf and reports whether it
        exists, whether it is a leaf (no child claims), and whether a named claim set's action reaches it
        via claimSets on itself or any ancestor. Used to anchor the TPDM readiness-check literals to the
        embedded claims - a stronger guard than a raw string match. The runtime claims-ready gate remains
        authoritative for full composition; this is only the shift-left CI anchor.
        #>
        param(
            [Parameter(Mandatory)]
            $Nodes,

            [Parameter(Mandatory)]
            [string]
            $LeafName,

            [Parameter(Mandatory)]
            [string]
            $ClaimSetName,

            [Parameter(Mandatory)]
            [string]
            $Action,

            [bool]
            $AncestorGrants = $false
        )

        foreach ($node in @($Nodes)) {
            if ($null -eq $node) { continue }

            $nodeGrants = $AncestorGrants
            if ($node.PSObject.Properties['claimSets']) {
                foreach ($claimSet in @($node.claimSets)) {
                    if ($null -ne $claimSet -and [string]$claimSet.name -eq $ClaimSetName -and
                        $claimSet.PSObject.Properties['actions']) {
                        $actionNames = @($claimSet.actions | ForEach-Object { [string]$_.name })
                        if ($actionNames -contains $Action) { $nodeGrants = $true }
                    }
                }
            }

            $children = if ($node.PSObject.Properties['claims']) { @($node.claims) } else { @() }

            if ([string]$node.name -eq $LeafName) {
                return [pscustomobject]@{
                    Found        = $true
                    IsLeaf       = ($children.Count -eq 0)
                    GrantReaches = $nodeGrants
                }
            }

            if ($children.Count -gt 0) {
                $result = Resolve-EmbeddedClaimLeaf -Nodes $children -LeafName $LeafName `
                    -ClaimSetName $ClaimSetName -Action $Action -AncestorGrants $nodeGrants
                if ($result.Found) { return $result }
            }
        }

        return [pscustomobject]@{ Found = $false; IsLeaf = $false; GrantReaches = $false }
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

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop

    function script:New-FixtureNupkgForSchema {
        <#
        .SYNOPSIS
        Creates a minimal asset-only ApiSchema .nupkg fixture for standard-mode pipeline tests.
        The .nupkg contains contentFiles/any/any/ApiSchema/package-manifest.json and
        contentFiles/any/any/ApiSchema/ApiSchema.json with a valid projectSchema.
        Returns the absolute path to the .nupkg file.
        #>
        param(
            [Parameter(Mandatory)]
            [string]
            $FeedFolder,

            [Parameter(Mandatory)]
            [string]
            $PackageId,

            [Parameter(Mandatory)]
            [string]
            $Version,

            [Parameter(Mandatory)]
            [string]
            $ProjectName,

            [Parameter(Mandatory)]
            [string]
            $ProjectEndpointName,

            [bool]
            $IsExtensionProject = $false
        )

        $nupkgName = "$($PackageId.ToLowerInvariant()).$($Version.ToLowerInvariant()).nupkg"
        $nupkgPath = Join-Path $FeedFolder $nupkgName

        $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "dms-s00-nupkg-$([Guid]::NewGuid().ToString('N'))"
        $apiSchemaDir = Join-Path $stagingDir "contentFiles/any/any/ApiSchema"
        New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

        # Full package-manifest.json with all required fields.
        $manifest = [ordered]@{
            version             = 1
            packageId           = $PackageId
            projectName         = $ProjectName
            projectEndpointName = $ProjectEndpointName
            isExtensionProject  = $IsExtensionProject
            schemaPath          = "ApiSchema.json"
            discoverySpecPath   = $null
            xsdDirectory        = $null
        }
        $manifest | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

        # Valid ApiSchema.json with a proper projectSchema so Read-ApiSchemaIdentity succeeds.
        $apiSchema = [ordered]@{
            apiSchemaVersion = "1.0.0"
            projectSchema    = [ordered]@{
                projectName         = $ProjectName
                projectEndpointName = $ProjectEndpointName
                isExtensionProject  = $IsExtensionProject
                resourceSchemas     = [ordered]@{}
                openApiBaseDocuments = [ordered]@{
                    resources = [ordered]@{
                        openapi = "3.0.1"
                    }
                }
            }
        }
        $apiSchema | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

        [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $nupkgPath)
        Remove-Item -LiteralPath $stagingDir -Recurse -Force

        return $nupkgPath
    }

    function script:New-StandardModeFeed {
        <#
        .SYNOPSIS
        Creates a local fixture feed folder containing a core and optional extension .nupkg.
        Returns the path to the feed folder.
        #>
        param(
            [string[]]
            $ExtensionNames = @()
        )

        $feedFolder = New-TestDirectory
        New-Item -ItemType Directory -Path $feedFolder -Force | Out-Null

        # Core package
        New-FixtureNupkgForSchema `
            -FeedFolder $feedFolder `
            -PackageId "EdFi.DataStandard52.ApiSchema" `
            -Version "1.0.333" `
            -ProjectName "Ed-Fi" `
            -ProjectEndpointName "ed-fi" `
            -IsExtensionProject $false | Out-Null

        foreach ($name in $ExtensionNames) {
            $lower = $name.ToLowerInvariant()
            $title = $lower.Substring(0, 1).ToUpperInvariant() + $lower.Substring(1)
            New-FixtureNupkgForSchema `
                -FeedFolder $feedFolder `
                -PackageId "EdFi.DataStandard52.$title.ApiSchema" `
                -Version "1.0.333" `
                -ProjectName $title `
                -ProjectEndpointName $lower `
                -IsExtensionProject $true | Out-Null
        }

        return $feedFolder
    }

    function script:Invoke-PrepareSchemaStandard {
        # Direct standard mode without -EnvironmentFile uses the package-backed core-only fallback.
        param(
            [string]
            $FeedFolder,

            [string]
            $Hash = $script:hashA,

            [int]
            $ToolExitCode = 0
        )

        $tool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $Hash -ExitCode $ToolExitCode
        & $script:repo.PrepareSchemaScript `
            -PackageFeedUrl $FeedFolder `
            -SchemaToolPath $tool | Out-Null
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
        It "stages core-only schema in standard mode against a local fixture feed" {
            $feedFolder = New-StandardModeFeed
            try {
                Invoke-PrepareSchemaStandard -FeedFolder $feedFolder
                $manifest = Get-RootManifest

                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                    Should -BeTrue
                $manifest.schema.selectionMode | Should -Be "Standard"
                $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
                $manifest.schema.effectiveSchemaHash | Should -Be $script:hashA
                $manifest.schema.apiSchemaManifestPath | Should -Be "ApiSchema/bootstrap-api-schema-manifest.json"
            } finally {
                if (Test-Path -LiteralPath $feedFolder) {
                    Remove-Item -LiteralPath $feedFolder -Recurse -Force
                }
            }
        }

        It "does not accept an -Extensions parameter in standard mode" {
            # Standard mode derives package selection from -EnvironmentFile; -Extensions was removed and fails with the native
            # PowerShell "parameter cannot be found" error.
            { & $script:repo.PrepareSchemaScript -Extensions "sample" } |
                Should -Throw -ExpectedMessage "*parameter cannot be found*Extensions*"
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
            # Expert filesystem staging is not package-driven, so no package identity is recorded.
            $manifest.schema.PSObject.Properties['selectedPackages'] | Should -BeNullOrEmpty
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

        It "rejects nested XSD files before finalizing a staged ApiSchema workspace" {
            $schemaDir = New-ApiSchemaSet
            New-Item -ItemType Directory -Path (Join-Path $schemaDir "xsd/nested") -Force | Out-Null
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $schemaDir "xsd/Ed-Fi-Core.xsd") -Encoding utf8
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $schemaDir "xsd/nested/Interchange-Student.xsd") -Encoding utf8

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir } |
                Should -Throw -ExpectedMessage "*nested XSD file*Interchange-Student.xsd*flattened*"
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json") |
                Should -BeFalse
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

        It "fails when api-schema-tools hashing fails" {
            $schemaDir = New-ApiSchemaSet

            { Invoke-PrepareSchema -ApiSchemaPath $schemaDir -ToolExitCode 1 } |
                Should -Throw -ExpectedMessage "*api-schema-tools hash failed*"
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

        It "requires caller-supplied claims for unmapped extensions" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))

            { Invoke-PrepareClaim } |
                Should -Throw -ExpectedMessage "*ClaimsDirectoryPath is required*Acme*"
        }

        It "throws when a known-extension catalog entry contributes no security metadata (misspelled key)" {
            # A catalog entry whose only key is unrecognized (e.g. a misspelled 'FragmentFileName') contributes
            # nothing and must fail fast rather than be silently treated as fully mapped - which would stage
            # nothing, suppress the unmapped-extension guard, and yield runtime 403s the gate can't detect.
            Add-KnownExtensionCatalogEntry -Key "AcmeTypo" -Body '@{ FragmnetFileName = "x.json" }'
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("AcmeTypo"))

            { Invoke-PrepareClaim } |
                Should -Throw -ExpectedMessage "*AcmeTypo*contributes no security metadata*"
        }

        It "throws when a known-extension catalog entry contributes no security metadata (empty VerificationChecks)" {
            # An entry with an empty VerificationChecks list and no fragment/prefix has a recognized key but
            # still contributes nothing; the value-level guard must reject it, not just key presence.
            Add-KnownExtensionCatalogEntry -Key "AcmeEmpty" -Body '@{ VerificationChecks = @() }'
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("AcmeEmpty"))

            { Invoke-PrepareClaim } |
                Should -Throw -ExpectedMessage "*AcmeEmpty*contributes no security metadata*"
        }

        It "throws when a known-extension catalog VerificationChecks entry is malformed" {
            # A VerificationChecks entry missing a field would be silently dropped by
            # Add-ExpectedVerificationCheck's whitespace guard; -ThrowOnInvalid makes the catalog path reject it.
            Add-KnownExtensionCatalogEntry -Key "AcmeBadCheck" -Body '@{ VerificationChecks = @(@{ ClaimSetName = "EdFiSandbox"; ResourceClaim = ""; Action = "Read" }) }'
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("AcmeBadCheck"))

            { Invoke-PrepareClaim } |
                Should -Throw -ExpectedMessage "*Malformed verification check*AcmeBadCheck*"
        }

        It "stages core + TPDM in Embedded claims mode from embedded claims without a caller fragment" {
            # TPDM is a bootstrap-mapped extension whose claims are already carried by the embedded
            # DS 5.2 Claims.json, so a core + TPDM schema set needs no caller fragment and stages no
            # claim files - claims.mode stays Embedded. TPDM descriptor data uses the distinct
            # uri://tpdm.ed-fi.org namespace, so that prefix IS recorded for the SeedLoader credential.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))

            { Invoke-PrepareClaim } | Should -Not -Throw
            $manifest = Get-RootManifest

            $manifest.claims.mode | Should -Be "Embedded"
            @(Get-ChildItem -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims") -File).Count |
                Should -Be 0
            $manifest.schema.selectedExtensions | Should -Contain "tpdm"
            $manifest.seed.extensionNamespacePrefixes | Should -Contain "uri://tpdm.ed-fi.org"
        }

        It "records TPDM leaf verification checks so the claims-ready gate confirms CMS composed TPDM claims" {
            # The catalog TPDM entry contributes leaf readiness checks (a TPDM descriptor reachable
            # via domains/systemDescriptors and a TPDM resource reachable via domains/tpdm) so the
            # gate verifies CMS actually flattened TPDM claims into EdFiSandbox in /authorizationMetadata.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            Invoke-PrepareClaim
            $manifest = Get-RootManifest

            $checks = @($manifest.claims.expectedVerificationChecks)
            $descriptorCheck = @($checks |
                Where-Object { $_.resourceClaim -eq "http://ed-fi.org/identity/claims/tpdm/credentialStatusDescriptor" })[0]
            $resourceCheck = @($checks |
                Where-Object { $_.resourceClaim -eq "http://ed-fi.org/identity/claims/tpdm/evaluation" })[0]

            $descriptorCheck.claimSetName | Should -Be "EdFiSandbox"
            $descriptorCheck.action | Should -Be "Read"
            # Leaf checks are asserted directly by the gate; they must not carry the parent-defer flag.
            $descriptorCheck.PSObject.Properties.Name | Should -Not -Contain "isParent"
            $resourceCheck.claimSetName | Should -Be "EdFiSandbox"
            $resourceCheck.action | Should -Be "Read"
            $resourceCheck.PSObject.Properties.Name | Should -Not -Contain "isParent"
        }

        It "keeps Hybrid claims mode for core + Sample + TPDM and still records TPDM verification checks" {
            # Sample stages a fragment (Hybrid) and records its namespace prefix; TPDM stages nothing
            # but still contributes its embedded-claims readiness checks.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample", "TPDM"))
            Invoke-PrepareClaim
            $manifest = Get-RootManifest
            $claimFiles = @(Get-ChildItem -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims") -File | ForEach-Object Name)

            $manifest.claims.mode | Should -Be "Hybrid"
            $claimFiles | Should -Contain "004-sample-extension-claimset.json"
            $manifest.seed.extensionNamespacePrefixes | Should -Contain "uri://sample.ed-fi.org"
            @($manifest.claims.expectedVerificationChecks |
                Where-Object { $_.resourceClaim -eq "http://ed-fi.org/identity/claims/tpdm/evaluation" }).Count |
                Should -Be 1
        }

        It "stages the full built-in set (core + Sample + Homograph + TPDM) without a caller fragment" {
            # The documented headline scenario: the full in-repo DS 5.2 set bootstraps with no
            # -ClaimsDirectoryPath. Sample and Homograph stage fragments (Hybrid) and Sample and TPDM
            # record their descriptor namespace prefixes; TPDM stages nothing but still contributes
            # its embedded-claims readiness checks.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Sample", "Homograph", "TPDM"))

            { Invoke-PrepareClaim } | Should -Not -Throw
            $manifest = Get-RootManifest
            $claimFiles = @(Get-ChildItem -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims") -File | ForEach-Object Name)

            $manifest.claims.mode | Should -Be "Hybrid"
            $claimFiles | Should -Contain "004-sample-extension-claimset.json"
            $claimFiles | Should -Contain "005-homograph-extension-claimset.json"
            $manifest.seed.extensionNamespacePrefixes | Should -Contain "uri://sample.ed-fi.org"
            $manifest.seed.extensionNamespacePrefixes | Should -Contain "uri://tpdm.ed-fi.org"
            # Homograph resources stay on the core namespace, so it records no distinct prefix.
            $manifest.seed.extensionNamespacePrefixes | Should -Not -Contain "uri://homograph.ed-fi.org"
            @($manifest.claims.expectedVerificationChecks |
                Where-Object { $_.resourceClaim -eq "http://ed-fi.org/identity/claims/tpdm/evaluation" }).Count |
                Should -Be 1
        }

        It "resolves the TPDM catalog entry only for the exact Title-cased name (case-sensitive)" {
            # A look-alike custom extension (e.g. "Tpdm") must not resolve to the built-in TPDM
            # metadata and silently skip its required caller-supplied claims.
            Import-Module (Join-Path $script:sourceDockerComposeRoot "bootstrap-schema-catalog.psm1") -Force

            Get-StandardKnownExtensionInfo -ProjectName "TPDM" | Should -Not -BeNullOrEmpty
            Get-StandardKnownExtensionInfo -ProjectName "Tpdm" | Should -BeNullOrEmpty
            Get-StandardKnownExtensionInfo -ProjectName "tpdm" | Should -BeNullOrEmpty
        }

        It "anchors TPDM catalog checks to leaf claims that EdFiSandbox reaches in the embedded DS 5.2 Claims.json" {
            # The TPDM check URIs are hand-authored literals. Assert each is still (a) present, (b) a LEAF
            # resource claim, and (c) reachable by its claim set's action via claimSets on itself or an
            # ancestor - so a future claims rename OR restructure surfaces here, not as a confusing gate
            # failure at bootstrap. The runtime claims-ready gate stays authoritative for full composition.
            Import-Module (Join-Path $script:sourceDockerComposeRoot "bootstrap-schema-catalog.psm1") -Force
            $tpdm = Get-StandardKnownExtensionInfo -ProjectName "TPDM"
            $claimsPath = Join-Path $script:sourceRepoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Standards/ds52/Claims.json"
            $hierarchy = @((Get-Content -LiteralPath $claimsPath -Raw | ConvertFrom-Json).claimsHierarchy)

            @($tpdm.VerificationChecks).Count | Should -BeGreaterThan 0
            foreach ($check in $tpdm.VerificationChecks) {
                $resolved = Resolve-EmbeddedClaimLeaf -Nodes $hierarchy -LeafName $check.ResourceClaim `
                    -ClaimSetName $check.ClaimSetName -Action $check.Action

                $resolved.Found | Should -BeTrue -Because "$($check.ResourceClaim) must exist in the embedded Claims.json"
                $resolved.IsLeaf | Should -BeTrue -Because "$($check.ResourceClaim) must be a leaf resource claim (the gate asserts leaves directly)"
                $resolved.GrantReaches | Should -BeTrue -Because "$($check.ClaimSetName)/$($check.Action) must reach $($check.ResourceClaim) via claimSets lineage"
            }
        }

        It "reuses an identical core + TPDM Embedded claims workspace on re-run" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("TPDM"))
            Invoke-PrepareClaim
            { Invoke-PrepareClaim } | Should -Not -Throw
            (Get-RootManifest).claims.mode | Should -Be "Embedded"
        }

        It "stages caller fragments and records expected verification checks with the fragment's raw resource name" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            $check.PSObject.Properties.Name | Should -Not -Contain "stagedOnly"
        }

        It "records explicit parent claimSets readiness checks structurally" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            $check.PSObject.Properties.Name | Should -Not -Contain "stagedOnly"
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))

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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
            $claimsDir = Join-Path $script:repo.RepoRoot "nested-claims"
            $nestedDir = Join-Path $claimsDir "subdir"
            New-ExplicitClaimsetFragment -Path (Join-Path $nestedDir "010-tpdm-claimset.json")

            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir

            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims/010-tpdm-claimset.json") |
                Should -BeTrue
        }

        It "detects nested filename collisions across subdirectories" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
            $claimsDir = Join-Path $script:repo.RepoRoot "nested-collision"
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "a/010-tpdm-claimset.json")
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "b/010-tpdm-claimset.json")

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*filename collision*"
        }

        It "rejects a non-boolean isParent value because CMS deserializes IsParent as a strict bool" {
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
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
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet -Extensions @("Acme"))
            $claimsDir = Join-Path $script:repo.RepoRoot "stale-claims-input"
            New-ExplicitClaimsetFragment -Path (Join-Path $claimsDir "010-tpdm-claimset.json")
            Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir

            $stagedClaims = Join-Path $script:repo.BootstrapRoot "claims"
            Remove-Item -LiteralPath $stagedClaims -Recurse -Force

            { Invoke-PrepareClaim -ClaimsDirectoryPath $claimsDir } |
                Should -Throw -ExpectedMessage "*stale claims/seed sections*"

            Test-Path -LiteralPath $stagedClaims | Should -BeFalse
        }

        It "Standard-mode core-only schema writes Embedded claims mode with no namespace prefixes" {
            # prepare-dms-claims.ps1 is mode-agnostic: it reads bootstrap-api-schema-manifest.json
            # regardless of selectionMode. Standard mode with no extensions produces no extension
            # projects in the manifest, so no fragments are staged and mode is Embedded.
            $feedFolder = New-StandardModeFeed
            try {
                Invoke-PrepareSchemaStandard -FeedFolder $feedFolder
                Invoke-PrepareClaim
                $manifest = Get-RootManifest

                $manifest.claims.mode | Should -Be "Embedded"
                @(Get-ChildItem -LiteralPath (Join-Path $script:repo.BootstrapRoot "claims") -File).Count |
                    Should -Be 0
                $manifest.seed.extensionNamespacePrefixes | Should -BeNullOrEmpty
            } finally {
                if (Test-Path -LiteralPath $feedFolder) {
                    Remove-Item -LiteralPath $feedFolder -Recurse -Force
                }
            }
        }

        It "reads the ApiSchema manifest from the recorded schema.apiSchemaManifestPath, not a hardcoded path" {
            # claims staging must consume the ApiSchema manifest the schema handoff recorded
            # (schema.apiSchemaManifestPath) - the same field Resolve-BootstrapSchemaWorkspace and
            # Set-BootstrapStartupEnvironment use - rather than a hardcoded
            # ApiSchema/bootstrap-api-schema-manifest.json.
            Invoke-PrepareSchema -ApiSchemaPath (New-ApiSchemaSet)

            $apiSchemaDir = Join-Path $script:repo.BootstrapRoot "ApiSchema"
            $defaultManifest = Join-Path $apiSchemaDir "bootstrap-api-schema-manifest.json"
            $relocatedRelative = "ApiSchema/relocated-api-schema-manifest.json"
            Move-Item -LiteralPath $defaultManifest -Destination (Join-Path $script:repo.BootstrapRoot $relocatedRelative)

            # Point the recorded schema handoff at the relocated manifest.
            $manifestPath = Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json"
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $manifest.schema.apiSchemaManifestPath = $relocatedRelative
            $manifest | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $manifestPath -Encoding utf8

            # The hardcoded default manifest no longer exists; claims staging must follow the recorded
            # path and succeed. With the old hardcoded path this threw "Staged ApiSchema manifest was not found".
            { Invoke-PrepareClaim } | Should -Not -Throw -Because "claims staging must honor schema.apiSchemaManifestPath"
            (Get-RootManifest).claims.mode | Should -Be "Embedded"
        }

        It "throws when the root manifest schema section lacks apiSchemaManifestPath" {
            # A schema section without apiSchemaManifestPath is an incomplete/legacy handoff; claims staging
            # must require the recorded field rather than silently falling back to a hardcoded manifest path.
            New-Item -ItemType Directory -Path $script:repo.BootstrapRoot -Force | Out-Null
            $manifestPath = Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json"
            '{"version":1,"schema":{"selectionMode":"Standard"}}' |
                Set-Content -LiteralPath $manifestPath -Encoding utf8

            { Invoke-PrepareClaim } |
                Should -Throw -ExpectedMessage "*schema.apiSchemaManifestPath*"
        }

    }

    Context "startup handoff" {
        It "activates staged DMS schema and CMS claims env vars when a valid bootstrap manifest is present" {
            # With a valid bootstrap manifest, Set-BootstrapStartupEnvironment returns $true and
            # activates both the staged schema workspace (USE_API_SCHEMA_PATH, API_SCHEMA_PATH,
            # DMS_API_SCHEMA_MOUNT_SOURCE, SCHEMA_PACKAGES=[]) and staged claims per
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
            # is an empty JSON array so run.sh performs no second download; DMS_API_SCHEMA_MOUNT_SOURCE
            # is the host-side source for bootstrap-dms.yml.
            $env:USE_API_SCHEMA_PATH | Should -Be "true"
            $env:API_SCHEMA_PATH | Should -Be "/app/ApiSchema"
            $env:SCHEMA_PACKAGES | Should -Be "[]"
            $env:DMS_API_SCHEMA_MOUNT_SOURCE | Should -Not -BeNullOrEmpty

            # Claims activation: manifest claims.mode=Hybrid (Sample extension) overrides the
            # stale Embedded process-env values.
            $env:DMS_CONFIG_CLAIMS_SOURCE | Should -Be "Hybrid"
            $env:DMS_CONFIG_CLAIMS_DIRECTORY | Should -Be "/app/additional-claims"
            $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE | Should -Not -BeNullOrEmpty
        }

        It "validates a package-backed standard-mode bootstrap manifest at startup (DMS-1156 wrapper path)" {
            # Regression: prepare-dms-schema.ps1 standard mode (core-only fallback when invoked directly,
            # effective package set when auto-staged by the wrapper) records selectionMode "Standard". Standard and ApiSchemaPath modes
            # stage the same normalized .bootstrap/ApiSchema workspace, so startup validation must accept
            # "Standard"; rejecting everything but "ApiSchemaPath" broke the standard-mode/wrapper production
            # path before infrastructure could start.
            $feedFolder = New-StandardModeFeed
            try {
                Invoke-PrepareSchemaStandard -FeedFolder $feedFolder
                Invoke-PrepareClaim
                (Get-RootManifest).schema.selectionMode | Should -Be "Standard"

                Remove-Module bootstrap-manifest -Force -ErrorAction SilentlyContinue
                Import-Module $script:repo.ManifestModule -Force

                # Story 04 (DMS-1154) activates staged-workspace runtime loading, so a valid
                # standard-mode manifest must be accepted and the staged-schema env vars activated.
                Set-BootstrapStartupEnvironment | Should -BeTrue
                $env:USE_API_SCHEMA_PATH | Should -Be "true"
                $env:API_SCHEMA_PATH | Should -Be "/app/ApiSchema"
            } finally {
                if (Test-Path -LiteralPath $feedFolder) {
                    Remove-Item -LiteralPath $feedFolder -Recurse -Force
                }
            }
        }

        It "activates staged schema env vars in bootstrap mode and sets DMS_API_SCHEMA_MOUNT_SOURCE to the staged workspace" {
            # Bootstrap mode sets USE_API_SCHEMA_PATH=true, API_SCHEMA_PATH=/app/ApiSchema, and
            # DMS_API_SCHEMA_MOUNT_SOURCE to the absolute .bootstrap/ApiSchema path. SCHEMA_PACKAGES
            # is set to an empty JSON array so run.sh performs no second package download into the
            # mounted workspace.
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
            $env:SCHEMA_PACKAGES | Should -Be "[]"
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
            $publishedConfig = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "published-config.yml") -Raw

            $localDms | Should -Not -Match "/app/additional-claims"
            $publishedDms | Should -Not -Match "/app/additional-claims"
            $localDms | Should -Not -Match "/app/ApiSchema:ro"
            $localConfig | Should -Match "DMS_CONFIG_CLAIMS_MOUNT_SOURCE"
            $publishedConfig | Should -Match "DMS_CONFIG_CLAIMS_MOUNT_SOURCE"

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

            # Config Service participation folds $bootstrapMode into the single $configServiceIncluded
            # authority, and published-config.yml is gated on it - so bootstrap mode always mounts the config
            # service (and its staged claims).
            $publishedStartScript = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1") -Raw
            $publishedStartScript | Should -Match '\$configServiceIncluded\s*=\s*\$EnableConfig -or \$InfraOnly -or \(\$IdentityProvider -eq "self-contained"\) -or \$bootstrapMode' -Because "the participation authority must fold in bootstrap mode"
            $publishedStartScript | Should -Match 'if \(\$configServiceIncluded\)\s*\{[^}]*?published-config\.yml' -Because "published bootstrap mode must include the Config Service compose file that mounts staged claims"
        }

        It "retains AddExtensionSecurityMetadata as a transitional non-bootstrap hybrid claims path" {
            # The -AddExtensionSecurityMetadata switch is kept in the startup wrappers and build script
            # as a transitional helper for non-bootstrap extension E2E setups. It must be present
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

        It "run.sh materializes a root ApiSchema manifest from current SCHEMA_PACKAGES package manifests" {
            $content = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/run.sh") -Raw

            $content | Should -Match "bootstrap-api-schema-manifest\.json"
            $content | Should -Match "package-manifest\.json"
            $content | Should -Match '\$\{AppSettings__ApiSchemaPath\}/Packages/\$\{name\}/package-manifest\.json'
            $content | Should -Match "jq -n --slurpfile projects"
            $content | Should -Match 'schemaPath: \(\$packageDir \+ "/" \+ \.schemaPath\)'
        }

        It "run.sh clears stale generated package output before materializing the current SCHEMA_PACKAGES manifest" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name jq -ErrorAction SilentlyContinue)) {
            $workspace = Join-Path $script:repo.RepoRoot "run-sh"
            $stubDirectory = Join-Path $workspace "bin"
            $apiSchemaPath = Join-Path $workspace "ApiSchema"
            $dotnetInvocationsPath = Join-Path $workspace "dotnet-invocations.log"
            New-Item -ItemType Directory -Path $stubDirectory -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $apiSchemaPath "Packages/RemovedPackage/xsd") -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $apiSchemaPath "DownloadedPackages/RemovedPackage") -Force | Out-Null

            "preserve" | Set-Content -LiteralPath (Join-Path $apiSchemaPath "JsonSchemaForApiSchema.json") -Encoding utf8
            "{}" | Set-Content -LiteralPath (Join-Path $apiSchemaPath "Packages/RemovedPackage/ApiSchema.json") -Encoding utf8
            @"
{
  "version": "1",
  "projectName": "RemovedPackage",
  "projectEndpointName": "removed",
  "isExtensionProject": true,
  "schemaPath": "ApiSchema.json",
  "discoverySpecPath": null,
  "xsdDirectory": "xsd"
}
"@ | Set-Content -LiteralPath (Join-Path $apiSchemaPath "Packages/RemovedPackage/package-manifest.json") -Encoding utf8

            @'
#!/bin/sh
exit 0
'@ | Set-Content -LiteralPath (Join-Path $stubDirectory "pg_isready") -Encoding utf8

            @'
#!/bin/sh
echo "$*" >> "$DOTNET_INVOCATIONS_PATH"

if [ "$1" = "/app/ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.dll" ]; then
    package_id=""
    output_dir=""

    while [ "$#" -gt 0 ]; do
        case "$1" in
            -p)
                package_id="$2"
                shift 2
                ;;
            -d)
                output_dir="$2"
                shift 2
                ;;
            *)
                shift
                ;;
        esac
    done

    package_dir="$output_dir/Packages/$package_id"
    mkdir -p "$package_dir/xsd"
    printf "{}" > "$package_dir/ApiSchema.json"
    printf "{}" > "$package_dir/discovery-spec.json"
    printf "<schema/>" > "$package_dir/xsd/$package_id.xsd"
    cat > "$package_dir/package-manifest.json" <<MANIFEST
{
  "version": "1",
  "projectName": "$package_id",
  "projectEndpointName": "$package_id",
  "isExtensionProject": true,
  "schemaPath": "ApiSchema.json",
  "discoverySpecPath": "discovery-spec.json",
  "xsdDirectory": "xsd"
}
MANIFEST
fi

exit 0
'@ | Set-Content -LiteralPath (Join-Path $stubDirectory "dotnet") -Encoding utf8

            & chmod +x (Join-Path $stubDirectory "pg_isready")
            & chmod +x (Join-Path $stubDirectory "dotnet")

            $envNames = @(
                "PATH",
                "DATABASE_CONNECTION_STRING_ADMIN",
                "AppSettings__UseApiSchemaPath",
                "AppSettings__ApiSchemaPath",
                "SCHEMA_PACKAGES",
                "DOTNET_INVOCATIONS_PATH"
            )
            $envSnapshot = @{}
            foreach ($name in $envNames) {
                $envSnapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
            }

            try {
                $env:PATH = "$stubDirectory$([System.IO.Path]::PathSeparator)$($env:PATH)"
                $env:DATABASE_CONNECTION_STRING_ADMIN = "host=localhost;port=5432;username=postgres"
                $env:AppSettings__UseApiSchemaPath = "true"
                $env:AppSettings__ApiSchemaPath = $apiSchemaPath
                $env:SCHEMA_PACKAGES = '[{"name":"KeptPackage","version":"1.0.0","feedUrl":"https://example.test/feed/index.json"}]'
                $env:DOTNET_INVOCATIONS_PATH = $dotnetInvocationsPath

                $runOutput = & bash (Join-Path $script:sourceRepoRoot "src/dms/run.sh") 2>&1

                $LASTEXITCODE | Should -Be 0 -Because ($runOutput -join [Environment]::NewLine)
            }
            finally {
                foreach ($name in $envNames) {
                    [System.Environment]::SetEnvironmentVariable($name, $envSnapshot[$name])
                }
            }

            Test-Path -LiteralPath (Join-Path $apiSchemaPath "JsonSchemaForApiSchema.json") |
                Should -BeTrue -Because "workspace files outside generated package output must be preserved"
            Test-Path -LiteralPath (Join-Path $apiSchemaPath "Packages/RemovedPackage") |
                Should -BeFalse -Because "stale package extraction output must be removed before manifest materialization"
            Test-Path -LiteralPath (Join-Path $apiSchemaPath "DownloadedPackages/RemovedPackage") |
                Should -BeFalse -Because "stale downloaded package output must be removed with extraction output"
            Test-Path -LiteralPath (Join-Path $apiSchemaPath "Packages/KeptPackage/package-manifest.json") |
                Should -BeTrue

            $manifest = Get-Content -LiteralPath (Join-Path $apiSchemaPath "bootstrap-api-schema-manifest.json") -Raw |
                ConvertFrom-Json
            @($manifest.projects).Count | Should -Be 1
            @($manifest.projects)[0].projectName | Should -Be "KeptPackage"
            @($manifest.projects)[0].schemaPath | Should -Be "Packages/KeptPackage/ApiSchema.json"
            @($manifest.projects).projectName | Should -Not -Contain "RemovedPackage"
        }

        It "run.sh skips PostgreSQL readiness checks when the datastore is MSSQL" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $workspace = Join-Path $script:repo.RepoRoot "run-sh-mssql"
            $stubDirectory = Join-Path $workspace "bin"
            $pgIsReadyInvocationsPath = Join-Path $workspace "pg-isready-invocations.log"
            $dotnetInvocationsPath = Join-Path $workspace "dotnet-invocations.log"
            Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Path $stubDirectory -Force | Out-Null

            @'
#!/bin/sh
echo "$*" >> "$PG_ISREADY_INVOCATIONS_PATH"
exit 1
'@ | Set-Content -LiteralPath (Join-Path $stubDirectory "pg_isready") -Encoding utf8

            @'
#!/bin/sh
echo "$*" >> "$DOTNET_INVOCATIONS_PATH"
exit 0
'@ | Set-Content -LiteralPath (Join-Path $stubDirectory "dotnet") -Encoding utf8

            & chmod +x (Join-Path $stubDirectory "pg_isready")
            & chmod +x (Join-Path $stubDirectory "dotnet")

            $envNames = @(
                "PATH",
                "DATABASE_CONNECTION_STRING_ADMIN",
                "AppSettings__Datastore",
                "AppSettings__UseApiSchemaPath",
                "PG_ISREADY_INVOCATIONS_PATH",
                "DOTNET_INVOCATIONS_PATH"
            )
            $envSnapshot = @{}
            foreach ($name in $envNames) {
                $envSnapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
            }

            try {
                $env:PATH = "$stubDirectory$([System.IO.Path]::PathSeparator)$($env:PATH)"
                $env:DATABASE_CONNECTION_STRING_ADMIN = "Server=localhost,1433;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true"
                $env:AppSettings__Datastore = "mssql"
                $env:AppSettings__UseApiSchemaPath = "false"
                $env:PG_ISREADY_INVOCATIONS_PATH = $pgIsReadyInvocationsPath
                $env:DOTNET_INVOCATIONS_PATH = $dotnetInvocationsPath

                $runOutput = & bash (Join-Path $script:sourceRepoRoot "src/dms/run.sh") 2>&1

                $LASTEXITCODE | Should -Be 0 -Because ($runOutput -join [Environment]::NewLine)
                ($runOutput -join [Environment]::NewLine) |
                    Should -Match "Skipping PostgreSQL readiness check for datastore 'mssql'\."
            }
            finally {
                foreach ($name in $envNames) {
                    [System.Environment]::SetEnvironmentVariable($name, $envSnapshot[$name])
                }
            }

            Test-Path -LiteralPath $pgIsReadyInvocationsPath |
                Should -BeFalse -Because "MSSQL startup must not run PostgreSQL readiness checks"
            Get-Content -LiteralPath $dotnetInvocationsPath |
                Should -Contain "EdFi.DataManagementService.Frontend.AspNetCore.dll"
        }

        It "start-local-dms.ps1 does not register the legacy default connector" {
            $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") -Raw
            $legacyConnectorCommandPattern = [regex]::Escape(("setup" + "-connectors.ps1") + " `$EnvironmentFile")
            $legacyPublicationNamePattern = "to" + "_debezium"
            $legacyDocumentTablePattern = "dms" + "\.document"

            $content | Should -Not -Match $legacyConnectorCommandPattern
            $content | Should -Not -Match 'SkipConnectorSetup'
            $content | Should -Not -Match $legacyPublicationNamePattern
            $content | Should -Not -Match $legacyDocumentTablePattern
        }

        It "start-published-dms.ps1 does not register the legacy default connector" {
            $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1") -Raw
            $legacyConnectorCommandPattern = [regex]::Escape(("setup" + "-connectors.ps1") + " `$EnvironmentFile")
            $legacyPublicationNamePattern = "to" + "_debezium"
            $legacyDocumentTablePattern = "dms" + "\.document"

            $content | Should -Not -Match $legacyConnectorCommandPattern
            $content | Should -Not -Match 'SkipConnectorSetup'
            $content | Should -Not -Match $legacyPublicationNamePattern
            $content | Should -Not -Match $legacyDocumentTablePattern
        }
    }

    Context "E2E wrappers" {
        It "keeps DataManagementService E2E setup on file-based schema packages (non-bootstrap compatibility)" {
            $content = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1") -Raw

            $content | Should -Not -Match "UseBootstrapWorkspace"
            $content | Should -Not -Match "prepare-dms-schema"
            $content | Should -Not -Match "prepare-dms-claims"
            $content | Should -Match "file-based schema packages"
            $content | Should -Match "SCHEMA_PACKAGES"
        }

        It "keeps InstanceManagement E2E setup on route-context file-based schema packages" {
            $content = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1") -Raw

            $content | Should -Not -Match "UseBootstrapWorkspace"
            $content | Should -Not -Match "prepare-dms-schema"
            $content | Should -Not -Match "prepare-dms-claims"
            $content | Should -Match "file-based schema packages"
            $content | Should -Match "USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES"
            $content | Should -Not -Match '\$env:USE_API_SCHEMA_PATH\s*=\s*"false"'
            $content | Should -Not -Match '\$env:API_SCHEMA_PATH\s*=\s*""'
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

        It "DataManagementService E2E setup uses the infra/configure/provision/DMS phase flow" {
            # start-local-dms.ps1 no longer creates a data store automatically after DMS-1153.
            # The DataManagementService E2E setup must call configure-local-data-store.ps1 explicitly
            # so a default route-unqualified data store points at the provisioned E2E database.
            $dmsSetup = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1") -Raw
            $phaseFlowPattern = '(?ms)^\s*\./start-local-dms\.ps1[^\r\n]*-InfraOnly[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile.*^\s*\./configure-local-data-store\.ps1[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile[^\r\n]*-DataStoreDatabaseName\s+\$e2eDatabaseName.*^\s*\./provision-e2e-database\.ps1[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile[^\r\n]*-DatabaseName\s+\$e2eDatabaseName.*^\s*\./start-local-dms\.ps1[^\r\n]*-DmsOnly[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile'
            $dmsSetup | Should -Match $phaseFlowPattern
            $dmsSetup | Should -Match "start-local-dms\.ps1[^\r\n]*-InfraOnly"
            $dmsSetup | Should -Match "configure-local-data-store\.ps1"
            $dmsSetup | Should -Match "E2E_DATABASE_NAME"
            $dmsSetup | Should -Match '-DataStoreDatabaseName\s+\$e2eDatabaseName'
            $dmsSetup | Should -Match "provision-e2e-database\.ps1"
            $dmsSetup | Should -Match '-DatabaseName\s+\$e2eDatabaseName'
            $dmsSetup | Should -Match "start-local-dms\.ps1[^\r\n]*-DmsOnly"
            $dmsSetup | Should -Not -Match "docker restart ed-fi-api"
        }

        It "DataManagementService E2E setup composes the Data Standard env file before start, configure, and provision" {
            $dmsSetup = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1") -Raw
            $composeBeforeReadPattern = '(?ms)\$baseEnvironmentFile\s*=\s*Resolve-LocalSettingsEnvironmentFile.*\$resolvedEnvironmentFile\s*=\s*Resolve-DataStandardEnvironmentFile.*-DataStandardVersion\s+\$DataStandardVersion.*-BaseEnvironmentFile\s+\$baseEnvironmentFile.*\$envValues\s*=\s*ReadValuesFromEnvFile\s+\$resolvedEnvironmentFile'

            $dmsSetup | Should -Match $composeBeforeReadPattern
            $dmsSetup | Should -Match 'configure-local-data-store\.ps1[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile'
            $dmsSetup | Should -Match 'provision-e2e-database\.ps1[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile'
            $dmsSetup | Should -Not -Match 'start-local-dms\.ps1[^\r\n]*-DataStandardVersion'
        }

        It "InstanceManagement E2E setup composes the Data Standard env file before start and route-context provisioning" {
            $imSetup = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1") -Raw
            $composePattern = '(?ms)\$baseEnvironmentFile\s*=\s*Resolve-LocalSettingsEnvironmentFile[^\r\n]*\.env\.routeContext\.e2e.*\$resolvedEnvironmentFile\s*=\s*Resolve-DataStandardEnvironmentFile.*-DataStandardVersion\s+\$DataStandardVersion.*-BaseEnvironmentFile\s+\$baseEnvironmentFile'

            $imSetup | Should -Match $composePattern
            $imSetup | Should -Match 'start-local-dms\.ps1[^\r\n]*-EnvironmentFile\s+\$resolvedEnvironmentFile'
            $imSetup | Should -Match 'provision-e2e-database\.ps1'
            $imSetup | Should -Match '-EnvironmentFile\s+\$resolvedEnvironmentFile'
            $imSetup | Should -Not -Match 'start-local-dms\.ps1[^\r\n]*-DataStandardVersion'
        }

        It "start-published-dms.ps1 can create E2E data stores against an explicit database name" {
            $publishedStartScript = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1") -Raw

            $publishedStartScript | Should -Match '\$DataStoreDatabaseName = ""'
            $publishedStartScript | Should -Match '\$postgresDbName ='
            $publishedStartScript | Should -Match '-PostgresDbName\s+\$postgresDbName'
            $publishedStartScript | Should -Not -Match '-PostgresDbName\s+\$envValues\.POSTGRES_DB_NAME'
        }

        It "E2E setup scripts do not enable Kafka infrastructure by default" {
            foreach ($setupScript in @(
                "src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1",
                "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"
            )) {
                $setup = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot $setupScript) -Raw

                $setup | Should -Match "start-local-dms\.ps1"
                $setup | Should -Not -Match "start-local-dms\.ps1[^\r\n]*-EnableKafka"
                $setup | Should -Not -Match "start-local-dms\.ps1[^\r\n]*-EnableKafkaUI"
            }
        }

        It "active Docker Compose env files do not retain legacy DMS backend switches" {
            $legacyDmsEnvironmentVariablePattern = '(?m)^(NEED_DATABASE_SETUP|USE_RELATIONAL_BACKEND|DMS_DEPLOY_DATABASE_ON_STARTUP|DMS_QUERYHANDLER|AppSettings__UseRelationalBackend)\s*='

            Get-ChildItem -LiteralPath $script:sourceDockerComposeRoot -Force -File -Filter ".env*" |
                ForEach-Object {
                    $content = Get-Content -LiteralPath $_.FullName -Raw
                    $content | Should -Not -Match $legacyDmsEnvironmentVariablePattern -Because "$($_.Name) must not reintroduce the legacy DMS backend selection or startup provisioning surface"
                }
        }

        It "tracked env files and the schema catalog agree on a single ApiSchema package version" {
            # The ApiSchema package version pin is duplicated across every env file's
            # SCHEMA_PACKAGES value and the catalog's core fallback pin. A missed file during a
            # version bump silently runs a mixed package set (or, on the catalog fallback path,
            # provisions a schema whose effective hash mismatches the runtime). Tracked files
            # only: local untracked .env copies are developer state, not repo contract.
            $trackedEnvFiles = @(git -C $script:sourceDockerComposeRoot ls-files ".env*" 2>$null)
            if ($LASTEXITCODE -ne 0 -or $trackedEnvFiles.Count -eq 0) {
                Set-ItResult -Skipped -Because "git is unavailable or returned no tracked env files"
                return
            }

            $versions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($fileName in $trackedEnvFiles) {
                $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot $fileName) -Raw
                foreach ($match in [regex]::Matches($content, '"version"\s*:\s*"([0-9.]+)"')) {
                    $null = $versions.Add($match.Groups[1].Value)
                }
            }

            $versions.Count | Should -Be 1 -Because "every env file's SCHEMA_PACKAGES entries must pin the same ApiSchema package version (found: $($versions -join ', '))"

            Import-Module (Join-Path $script:sourceDockerComposeRoot "bootstrap-schema-catalog.psm1") -Force
            (Get-StandardCorePackage).Version | Should -Be @($versions)[0] -Because "the catalog core fallback pin must match the env files' SCHEMA_PACKAGES version"
        }

        It "build-dms.ps1 teardown invocations include -RemoveBootstrap to wipe stale bootstrap workspace" {
            # Confirm that both teardown invocations in Start-DockerEnvironment pass -RemoveBootstrap so
            # a manually-staged .bootstrap/ from a developer session cannot hijack the subsequent E2E start.
            $buildScript = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "build-dms.ps1") -Raw
            $buildScript | Should -Match "start-local-dms\.ps1.*-d.*-v.*-RemoveBootstrap"
            $buildScript | Should -Match "start-published-dms\.ps1.*-d.*-v.*-RemoveBootstrap"
        }

        It "build-dms.ps1 relational E2E startup clears schema process env overrides around compose calls" {
            # Docker Compose gives process env vars precedence over --env-file values. Relational E2E
            # startup must let .env.e2e provide the schema package settings, even when an
            # earlier compose helper left empty schema env vars in the process.
            $buildScript = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "build-dms.ps1") -Raw

            $buildScript | Should -Match "function Invoke-WithEnvironmentFileSchemaSettings"
            $buildScript | Should -Match '"USE_API_SCHEMA_PATH"'
            $buildScript | Should -Match '"API_SCHEMA_PATH"'
            $buildScript | Should -Match '"SCHEMA_PACKAGES"'
            $buildScript | Should -Match 'Remove-Item "Env:\$name"'
            $buildScript | Should -Match '\[System\.Environment\]::SetEnvironmentVariable\(\$name, \$previousValues\[\$name\]\)'
            ([regex]::Matches($buildScript, 'Invoke-WithEnvironmentFileSchemaSettings -Enabled:\$UseEnvironmentFileSchemaSettings -Action')).Count | Should -Be 4
            ([regex]::Matches($buildScript, '\./start-(local|published)-dms\.ps1')).Count | Should -Be 4
            $buildScript | Should -Match '(?s)Invoke-WithEnvironmentFileSchemaSettings[^{]+-Action\s+\{[^}]+start-local-dms\.ps1[^\n]+-d[^\n]+-v[^\n]+-RemoveBootstrap'
            $buildScript | Should -Match '(?s)Invoke-WithEnvironmentFileSchemaSettings[^{]+-Action\s+\{[^}]+start-published-dms\.ps1[^\n]+-d[^\n]+-v[^\n]+-RemoveBootstrap'
            $buildScript | Should -Match '-UseEnvironmentFileSchemaSettings:\$e2eTestSettings\.ShouldProvisionE2EDatabase'
        }

        It "build-dms.ps1 StartEnvironment uses the bootstrap phase contract" {
            # StartEnvironment is a developer stack command, not the E2E generated-database path.
            # It must use the bootstrap wrapper so configure-local-data-store.ps1 and
            # provision-dms-schema.ps1 complete before the DMS-only startup phase.
            $buildScript = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "build-dms.ps1") -Raw

            $buildScript | Should -Match "function Start-BootstrapDockerEnvironment"
            $buildScript | Should -Match "bootstrap-local-dms\.ps1 @bootstrapArgs"
            $buildScript | Should -Match "bootstrap-published-dms\.ps1 @bootstrapArgs"
            $buildScript | Should -Match "StartEnvironment \{ Invoke-Step \{ Start-BootstrapDockerEnvironment"
        }

        It "build-dms.ps1 fails fast when restarted DMS never becomes healthy" {
            $buildScript = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "build-dms.ps1") -Raw

            $buildScript | Should -Match "DMS container '\`$ContainerName' did not become ready within the timeout period"
            $buildScript | Should -Not -Match "DMS did not become ready, but continuing anyway"
        }

        It "E2E setup wrappers contain defensive .bootstrap removal step before non-bootstrap startup" {
            # Confirm that both E2E setup wrappers defensively remove .bootstrap/ before invoking
            # start-local-dms.ps1 so a stale bootstrap workspace cannot hijack the non-bootstrap run
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
