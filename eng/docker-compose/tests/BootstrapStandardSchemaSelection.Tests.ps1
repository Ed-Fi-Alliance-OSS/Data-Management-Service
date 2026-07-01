# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester fixture parameters are unused by design.')]
param()

Describe "DMS-1156 standard-mode schema selection" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:hashA = "0000000000000000000000000000000000000000000000000000000000000001"

        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop

        function script:New-TempDirectory {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-std-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function script:New-IsolatedRepo {
            <#
            .SYNOPSIS
            Copies the bootstrap scripts into a temp repo tree and returns a helper object.
            #>
            $repoRoot = script:New-TempDirectory
            $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

            foreach ($fileName in @(
                "bootstrap-manifest.psm1",
                "bootstrap-schema-catalog.psm1",
                "bootstrap-schema-tool.psm1",
                "bootstrap-package-resolver.psm1",
                "prepare-dms-schema.ps1"
            )) {
                Copy-Item `
                    -LiteralPath (Join-Path $script:sourceDockerComposeRoot $fileName) `
                    -Destination $dockerComposeRoot
            }

            # schema-package-utility.psm1 lives at eng/ (a sibling of eng/docker-compose/), not inside
            # eng/docker-compose/; -EnvironmentFile-driven staging imports it via a relative "../" path.
            $engRoot = Join-Path $repoRoot "eng"
            Copy-Item `
                -LiteralPath (Join-Path $script:sourceRepoRoot "eng/schema-package-utility.psm1") `
                -Destination $engRoot

            # Copy JsonSchemaForApiSchema.json so prepare-dms-schema.ps1 can stage it into the
            # workspace (required since DMS-1154 activates staged-workspace runtime loading).
            $jsonSchemaSourceDir = Join-Path $script:sourceRepoRoot "src/dms/core/EdFi.DataManagementService.Core/ApiSchema"
            $jsonSchemaTargetDir = Join-Path $repoRoot "src/dms/core/EdFi.DataManagementService.Core/ApiSchema"
            New-Item -ItemType Directory -Path $jsonSchemaTargetDir -Force | Out-Null
            Copy-Item `
                -LiteralPath (Join-Path $jsonSchemaSourceDir "JsonSchemaForApiSchema.json") `
                -Destination $jsonSchemaTargetDir

            return [pscustomobject]@{
                RepoRoot            = $repoRoot
                DockerComposeRoot   = $dockerComposeRoot
                BootstrapRoot       = Join-Path $dockerComposeRoot ".bootstrap"
                PrepareSchemaScript = Join-Path $dockerComposeRoot "prepare-dms-schema.ps1"
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

            $path = Join-Path $Directory "fake-schema-tool.ps1"
            @"
param([Parameter(ValueFromRemainingArguments = `$true)][string[]] `$Arguments)
Write-Output "Effective schema hash: $Hash"
exit $ExitCode
"@ | Set-Content -LiteralPath $path -Encoding utf8
            return $path
        }

        function script:New-FixtureNupkg {
            <#
            .SYNOPSIS
            Creates an asset-only .nupkg fixture in a local feed folder.
            The nupkg contains:
              contentFiles/any/any/ApiSchema/package-manifest.json
              contentFiles/any/any/ApiSchema/ApiSchema.json
            Returns the .nupkg path.
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
                $IsExtensionProject = $false,

                # Manifest-relative path to a discovery spec file; when supplied, the file is
                # created inside the package and declared in package-manifest.json.
                [string]
                $DiscoverySpecPath,

                # Manifest-relative path to an XSD directory; when supplied, the directory is
                # created with one .xsd file and declared in package-manifest.json.
                [string]
                $XsdDirectory,

                # Plants a conventional sibling xsd/ directory in the payload WITHOUT declaring it
                # in the manifest, to exercise manifest-authoritative (no rediscovery) staging.
                [switch]
                $PlantUndeclaredXsdDirectory
            )

            $nupkgName = "$($PackageId.ToLowerInvariant()).$($Version.ToLowerInvariant()).nupkg"
            $nupkgPath = Join-Path $FeedFolder $nupkgName

            $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "dms-std-nupkg-$([Guid]::NewGuid().ToString('N'))"
            $apiSchemaDir = Join-Path $stagingDir "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

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

            if (-not [string]::IsNullOrWhiteSpace($DiscoverySpecPath)) {
                $manifest.discoverySpecPath = $DiscoverySpecPath
                $discoverySpecFullPath = Join-Path $apiSchemaDir $DiscoverySpecPath
                New-Item -ItemType Directory -Path (Split-Path -Parent $discoverySpecFullPath) -Force | Out-Null
                '{"urls":{}}' | Set-Content -LiteralPath $discoverySpecFullPath -Encoding utf8
            }

            if (-not [string]::IsNullOrWhiteSpace($XsdDirectory)) {
                $manifest.xsdDirectory = $XsdDirectory
                $xsdFullPath = Join-Path $apiSchemaDir $XsdDirectory
                New-Item -ItemType Directory -Path $xsdFullPath -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdFullPath "Ed-Fi-Core.xsd") -Encoding utf8
            }

            if ($PlantUndeclaredXsdDirectory) {
                $undeclaredXsdPath = Join-Path $apiSchemaDir "xsd"
                New-Item -ItemType Directory -Path $undeclaredXsdPath -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $undeclaredXsdPath "Undeclared.xsd") -Encoding utf8
            }

            $manifest | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

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

        function script:New-FixtureFeed {
            <#
            .SYNOPSIS
            Creates a local fixture feed folder containing the package-backed core package. Standard mode is
            core-only, so the feed stages only the core package; extension/custom schema sets use
            the expert -ApiSchemaPath filesystem path, not package resolution.
            Returns the path to the feed folder.
            #>
            param()

            $feedFolder = script:New-TempDirectory

            script:New-FixtureNupkg `
                -FeedFolder $feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -ProjectName "Ed-Fi" `
                -ProjectEndpointName "ed-fi" `
                -IsExtensionProject $false | Out-Null

            return $feedFolder
        }

        function script:New-SchemaPackagesEnvironmentFile {
            <#
            .SYNOPSIS
            Writes a docker-compose-style env file whose SCHEMA_PACKAGES value lists the supplied
            package entries (name/version/feedUrl), matching the shape schema-package-utility.psm1's
            Get-SchemaPackagesFromEnvironmentFile parses. feedUrl is a local folder path in these
            fixtures (Resolve-StandardSchemaPackage treats a plain directory path as a local feed).
            Returns the path to the written env file.
            #>
            param(
                [Parameter(Mandatory)]
                [string]
                $Directory,

                [Parameter(Mandatory)]
                [object[]]
                $Packages
            )

            $envFilePath = Join-Path $Directory ".env.fixture"
            $packagesJson = $Packages | ConvertTo-Json -Depth 5 -AsArray
            $content = "SCHEMA_PACKAGES='$packagesJson'`n"
            Set-Content -LiteralPath $envFilePath -Value $content -Encoding utf8 -NoNewline
            return $envFilePath
        }

        function script:Invoke-PrepareStandard {
            <#
            .SYNOPSIS
            Invokes prepare-dms-schema.ps1 in standard mode (no -ApiSchemaPath). Standard mode is
            package-backed core-only; there is no -Extensions parameter.
            #>
            param(
                [Parameter(Mandatory)]
                [string]
                $FeedFolder,

                [string]
                $Hash = $script:hashA,

                [int]
                $ToolExitCode = 0
            )

            $tool = script:New-FakeSchemaTool `
                -Directory $script:repo.RepoRoot `
                -Hash $Hash `
                -ExitCode $ToolExitCode

            & $script:repo.PrepareSchemaScript `
                -PackageFeedUrl $FeedFolder `
                -SchemaToolPath $tool | Out-Null
        }

        function script:Get-RootManifest {
            return Get-Content `
                -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") `
                -Raw |
                ConvertFrom-Json
        }

        function script:Get-ApiSchemaManifest {
            return Get-Content `
                -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json") `
                -Raw |
                ConvertFrom-Json
        }

        function script:Get-DeclaredParameterBlock {
            <#
            .SYNOPSIS
            Returns the ParamBlockAst for a script file's top-level param block, or for a named function
            defined within the file. Returns $null when there is no param block. Parses; never executes.
            #>
            param(
                [Parameter(Mandatory)]
                [string]
                $FilePath,

                [string]
                $FunctionName
            )

            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($FilePath, [ref]$null, [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) {
                throw "Parse errors in '$FilePath': $($parseErrors[0].Message)"
            }

            if ([string]::IsNullOrEmpty($FunctionName)) {
                return $ast.ParamBlock
            }

            $function = $ast.Find(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $FunctionName
                },
                $true)
            if ($null -eq $function) {
                throw "Function '$FunctionName' not found in '$FilePath'."
            }
            return $function.Body.ParamBlock
        }

        function script:Test-ExposesExtensionsParameter {
            <#
            .SYNOPSIS
            True if the param block declares a parameter named 'Extensions' (typed or untyped) or any
            parameter carrying an [Alias('Extensions')] alias. Case-insensitive, matching how PowerShell
            binds parameter names and aliases - so it catches forms a single regex would miss.
            #>
            param(
                $ParamBlock
            )

            if ($null -eq $ParamBlock) {
                return $false
            }

            foreach ($parameter in $ParamBlock.Parameters) {
                if ($parameter.Name.VariablePath.UserPath -ieq 'Extensions') {
                    return $true
                }

                foreach ($attribute in $parameter.Attributes) {
                    if ($attribute -is [System.Management.Automation.Language.AttributeAst] -and
                        ($attribute.TypeName.Name -ieq 'Alias' -or $attribute.TypeName.Name -ieq 'AliasAttribute')) {
                        foreach ($argument in $attribute.PositionalArguments) {
                            if (($argument -is [System.Management.Automation.Language.StringConstantExpressionAst]) -and
                                ($argument.Value -ieq 'Extensions')) {
                                return $true
                            }
                        }
                    }
                }
            }

            return $false
        }
    }

    BeforeEach {
        $script:repo = script:New-IsolatedRepo
        $script:feedFolder = $null
    }

    AfterEach {
        if ($null -ne $script:repo -and (Test-Path -LiteralPath $script:repo.RepoRoot)) {
            Remove-Item -LiteralPath $script:repo.RepoRoot -Recurse -Force
        }

        if ($null -ne $script:feedFolder -and (Test-Path -LiteralPath $script:feedFolder)) {
            Remove-Item -LiteralPath $script:feedFolder -Recurse -Force
        }
    }

    Context "Given_StandardMode_CoreOnly_NoExtensionsParam" {
        It "It_stages_core_schema_and_records_Standard_selectionMode_when_ApiSchemaPath_omitted" {
            $script:feedFolder = script:New-FixtureFeed

            script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

            $manifest = script:Get-RootManifest

            $manifest.schema.selectionMode | Should -Be "Standard"
            $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
            $manifest.schema.apiSchemaManifestPath | Should -Be "ApiSchema/bootstrap-api-schema-manifest.json"
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                Should -BeTrue
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json") |
                Should -BeTrue
        }
    }

    Context "Given_EnvironmentFile_DrivenStandardMode_DMS1238" {
        It "It_stages_core_and_extension_from_SCHEMA_PACKAGES_when_EnvironmentFile_is_supplied" {
            # DMS-1238: standard mode staged from -EnvironmentFile must resolve and stage the FULL
            # SCHEMA_PACKAGES set (core plus any extensions), not the catalog core-only default, so the
            # staged workspace's effective schema hash matches what the DMS container entrypoint
            # resolves from the same SCHEMA_PACKAGES value at startup.
            $packagesFeedFolder = script:New-TempDirectory
            try {
                script:New-FixtureNupkg `
                    -FeedFolder $packagesFeedFolder `
                    -PackageId "EdFi.DataStandard52.ApiSchema" `
                    -Version "1.0.332" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false | Out-Null

                script:New-FixtureNupkg `
                    -FeedFolder $packagesFeedFolder `
                    -PackageId "EdFi.DataStandard52.TPDM.ApiSchema" `
                    -Version "1.0.332" `
                    -ProjectName "TPDM" `
                    -ProjectEndpointName "tpdm" `
                    -IsExtensionProject $true | Out-Null

                $environmentFilePath = script:New-SchemaPackagesEnvironmentFile `
                    -Directory $script:repo.RepoRoot `
                    -Packages @(
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard52.ApiSchema"
                            version = "1.0.332"
                            feedUrl = $packagesFeedFolder
                        },
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard52.TPDM.ApiSchema"
                            version = "1.0.332"
                            feedUrl = $packagesFeedFolder
                        }
                    )

                $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA
                & $script:repo.PrepareSchemaScript `
                    -EnvironmentFile $environmentFilePath `
                    -SchemaToolPath $tool | Out-Null

                $manifest = script:Get-RootManifest
                $manifest.schema.selectionMode | Should -Be "Standard"
                @($manifest.schema.selectedExtensions) | Should -Contain "tpdm"

                $apiSchemaManifest = script:Get-ApiSchemaManifest
                $projectEndpoints = @($apiSchemaManifest.projects | ForEach-Object { $_.projectEndpointName })
                $projectEndpoints | Should -Contain "ed-fi"
                $projectEndpoints | Should -Contain "tpdm"

                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                    Should -BeTrue
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/TPDM/ApiSchema.json") |
                    Should -BeTrue
            }
            finally {
                if (Test-Path -LiteralPath $packagesFeedFolder) {
                    Remove-Item -LiteralPath $packagesFeedFolder -Recurse -Force
                }
            }
        }

        It "It_fails_fast_when_SCHEMA_PACKAGES_lists_no_core_package" {
            # Guard rail: SCHEMA_PACKAGES must carry exactly one core package entry so the staged
            # workspace always has a core project. A set containing only extensions must fail fast
            # rather than silently staging an incomplete workspace.
            $extensionOnlyFeedFolder = script:New-TempDirectory
            try {
                script:New-FixtureNupkg `
                    -FeedFolder $extensionOnlyFeedFolder `
                    -PackageId "EdFi.DataStandard52.TPDM.ApiSchema" `
                    -Version "1.0.332" `
                    -ProjectName "TPDM" `
                    -ProjectEndpointName "tpdm" `
                    -IsExtensionProject $true | Out-Null

                $environmentFilePath = script:New-SchemaPackagesEnvironmentFile `
                    -Directory $script:repo.RepoRoot `
                    -Packages @(
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard52.TPDM.ApiSchema"
                            version = "1.0.332"
                            feedUrl = $extensionOnlyFeedFolder
                        }
                    )

                $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA

                {
                    & $script:repo.PrepareSchemaScript `
                        -EnvironmentFile $environmentFilePath `
                        -SchemaToolPath $tool | Out-Null
                } | Should -Throw -ExpectedMessage "*must list exactly one core package*"

                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                    Should -BeFalse
            }
            finally {
                if (Test-Path -LiteralPath $extensionOnlyFeedFolder) {
                    Remove-Item -LiteralPath $extensionOnlyFeedFolder -Recurse -Force
                }
            }
        }

        It "It_falls_back_to_catalog_core_only_staging_when_EnvironmentFile_is_omitted_BackwardCompat" {
            # Backward compatibility: direct invocation with no -EnvironmentFile (the pre-DMS-1238
            # contract) must keep resolving the catalog-pinned core-only default, unaffected by this
            # change.
            $script:feedFolder = script:New-FixtureFeed

            script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

            $manifest = script:Get-RootManifest
            $manifest.schema.selectionMode | Should -Be "Standard"
            $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                Should -BeTrue
        }

        It "It_falls_back_to_catalog_core_only_staging_when_EnvironmentFile_has_no_SCHEMA_PACKAGES_key" {
            # An env file supplied via -EnvironmentFile but without a SCHEMA_PACKAGES key at all (e.g. a
            # minimal or non-schema env file) must not throw; it degrades to the catalog core-only
            # default rather than treating absence as an error.
            $script:feedFolder = script:New-FixtureFeed

            $environmentFilePath = Join-Path $script:repo.RepoRoot ".env.no-schema-packages"
            "DMS_HTTP_PORTS=8080`n" | Set-Content -LiteralPath $environmentFilePath -Encoding utf8

            $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA
            & $script:repo.PrepareSchemaScript `
                -EnvironmentFile $environmentFilePath `
                -PackageFeedUrl $script:feedFolder `
                -SchemaToolPath $tool | Out-Null

            $manifest = script:Get-RootManifest
            $manifest.schema.selectionMode | Should -Be "Standard"
            $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                Should -BeTrue
        }
    }

    Context "Given_DefaultCatalogPath" {
        It "It_catalog_exposes_the_pinned_default_feed_and_core_package" {
            # The production standard path uses the pinned catalog defaults (no caller-supplied feed/id/
            # version). Lock those defaults so an accidental change to the configured feed or pinned core
            # package identity is caught here.
            Import-Module (Join-Path $script:sourceDockerComposeRoot "bootstrap-schema-catalog.psm1") -Force

            Get-StandardSchemaFeed |
                Should -Match '^https://pkgs\.dev\.azure\.com/ed-fi-alliance/.*/EdFi/nuget/v3/index\.json$'

            $core = Get-StandardCorePackage
            $core.Id | Should -Be "EdFi.DataStandard52.ApiSchema"
            $core.ProjectToken | Should -Be "Ed-Fi"
            $core.EndpointToken | Should -Be "ed-fi"
            $core.Version | Should -Match '^\d+\.\d+\.\d+'
        }

        It "It_standard_mode_resolves_the_default_catalog_feed_and_core_package_when_PackageFeedUrl_is_omitted" {
            # Production standard path: developers omit -PackageFeedUrl, so prepare-dms-schema.ps1 falls
            # back to Get-StandardSchemaFeed / Get-StandardCorePackage. Exercise that fallback end to end
            # WITHOUT a network call by redirecting the isolated copy's pinned default feed to the local
            # fixture; the core package id/version still come from the catalog (Get-StandardCorePackage)
            # unchanged. prepare-dms-schema.ps1 re-imports the copied catalog with -Force on each run, so
            # the redirect is what the default path resolves.
            $script:feedFolder = script:New-FixtureFeed

            $catalogPath = Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-catalog.psm1"
            $redirected = $false
            $newLines = foreach ($line in (Get-Content -LiteralPath $catalogPath)) {
                if ($line -match '^\s*\$script:StandardFeedUrl\s*=') {
                    $redirected = $true
                    '$script:StandardFeedUrl = ' + "'$($script:feedFolder)'"
                } else {
                    $line
                }
            }
            $redirected |
                Should -BeTrue -Because "test setup must redirect the catalog default feed to the fixture; otherwise the default path would reach the live feed"
            Set-Content -LiteralPath $catalogPath -Value $newLines -Encoding utf8

            $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA -ExitCode 0

            # No -PackageFeedUrl: exercises the default feed / catalog fallback.
            & $script:repo.PrepareSchemaScript -SchemaToolPath $tool | Out-Null

            $manifest = script:Get-RootManifest
            $manifest.schema.selectionMode | Should -Be "Standard"
            $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                Should -BeTrue
        }
    }

    Context "Given_ExtensionsParameterRemoved" {
        It "It_does_not_declare_an_Extensions_parameter_on_prepare-dms-schema.ps1" {
            # scope decision: standard mode is package-backed core-only; there is no
            # -Extensions parameter. Invoking with -Extensions must fail with PowerShell's native
            # "parameter cannot be found" error.
            $prepareContent = Get-Content -LiteralPath $script:repo.PrepareSchemaScript -Raw
            # Case-sensitive param-declaration check (avoids matching $extension* locals).
            $prepareContent | Should -Not -Match '(?-i)\[string\[\]\]\s*\$Extensions\b'

            {
                & $script:repo.PrepareSchemaScript -Extensions "sample"
            } | Should -Throw -ExpectedMessage "*parameter cannot be found*Extensions*"
        }
    }

    Context "Given_BlankApiSchemaPath_BoundButEmpty" {
        It "It_throws_invalid_input_instead_of_falling_into_standard_mode_when_ApiSchemaPath_is_blank" {
            # blocker: standard mode is selected by OMITTING -ApiSchemaPath. An explicitly bound
            # but blank value is invalid expert-mode input and must fail fast, not silently route to
            # package-backed core-only staging.
            {
                & $script:repo.PrepareSchemaScript -ApiSchemaPath ""
            } | Should -Throw -ExpectedMessage "*-ApiSchemaPath was supplied but is blank*"

            # The failure must occur before any workspace is staged.
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") |
                Should -BeFalse
        }
    }

    Context "Given_StandardMode_ManifestCorrectness" {
        It "It_does_not_record_package_IDs_or_feed_URLs_in_root_manifest_or_ApiSchema_manifest" {
            $script:feedFolder = script:New-FixtureFeed

            script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

            $rootManifest = script:Get-RootManifest
            $apiSchemaManifest = script:Get-ApiSchemaManifest

            # Root manifest schema section must not expose package IDs, versions, or feed URLs.
            $rootManifestJson = $rootManifest | ConvertTo-Json -Depth 20
            $rootManifestJson | Should -Not -Match "EdFi\.DataStandard"
            $rootManifestJson | Should -Not -Match "1\.0\.329"
            $rootManifestJson | Should -Not -Match "pkgs\.dev\.azure\.com"
            $rootManifestJson | Should -Not -Match $script:feedFolder.Replace("\", "\\")

            # ApiSchema manifest projects must not expose package IDs, versions, or feed URLs.
            $apiSchemaManifestJson = $apiSchemaManifest | ConvertTo-Json -Depth 20
            $apiSchemaManifestJson | Should -Not -Match "EdFi\.DataStandard"
            $apiSchemaManifestJson | Should -Not -Match "1\.0\.329"
            $apiSchemaManifestJson | Should -Not -Match "pkgs\.dev\.azure\.com"
        }
    }

    Context "Given_InvalidPayload_LibDll_SurfacedThroughStandardMode" {
        It "It_fails_fast_with_clear_message_and_leaves_no_staged_workspace_when_package_contains_lib_dll" {
            # Build a malformed CORE .nupkg that contains a forbidden lib/ directory with a .dll file.
            # The resolver finds it by the pinned core ID; the contract validator must reject it before
            # workspace finalization.
            $malformedFeedFolder = script:New-TempDirectory

            try {
                $corePackageId = "EdFi.DataStandard52.ApiSchema"
                $coreNupkgName = "$($corePackageId.ToLowerInvariant()).1.0.329.nupkg"
                $coreNupkgPath = Join-Path $malformedFeedFolder $coreNupkgName
                $libStaging = Join-Path ([System.IO.Path]::GetTempPath()) "dms-std-libcore-$([Guid]::NewGuid().ToString('N'))"
                $libApiSchemaDir = Join-Path $libStaging "contentFiles/any/any/ApiSchema"
                New-Item -ItemType Directory -Path $libApiSchemaDir -Force | Out-Null

                $libManifest = [ordered]@{
                    version             = 1
                    packageId           = $corePackageId
                    projectName         = "Ed-Fi"
                    projectEndpointName = "ed-fi"
                    isExtensionProject  = $false
                    schemaPath          = "ApiSchema.json"
                    discoverySpecPath   = $null
                    xsdDirectory        = $null
                }
                $libManifest | ConvertTo-Json -Depth 5 |
                    Set-Content -LiteralPath (Join-Path $libApiSchemaDir "package-manifest.json") -Encoding utf8

                $libApiSchema = [ordered]@{
                    apiSchemaVersion = "1.0.0"
                    projectSchema    = [ordered]@{
                        projectName         = "Ed-Fi"
                        projectEndpointName = "ed-fi"
                        isExtensionProject  = $false
                        resourceSchemas     = [ordered]@{}
                        openApiBaseDocuments = [ordered]@{
                            resources = [ordered]@{ openapi = "3.0.1" }
                        }
                    }
                }
                $libApiSchema | ConvertTo-Json -Depth 10 |
                    Set-Content -LiteralPath (Join-Path $libApiSchemaDir "ApiSchema.json") -Encoding utf8

                # Plant the forbidden lib/ directory to trigger the contract violation.
                $libNetDir = Join-Path $libStaging "lib/net8.0"
                New-Item -ItemType Directory -Path $libNetDir -Force | Out-Null
                [System.IO.File]::WriteAllBytes(
                    (Join-Path $libNetDir "EdFi.DataStandard52.ApiSchema.dll"),
                    [byte[]]@(0, 0, 0, 0)
                )

                [System.IO.Compression.ZipFile]::CreateFromDirectory($libStaging, $coreNupkgPath)
                Remove-Item -LiteralPath $libStaging -Recurse -Force

                {
                    script:Invoke-PrepareStandard -FeedFolder $malformedFeedFolder
                } | Should -Throw -ExpectedMessage "*forbidden*"

                # The staged ApiSchema workspace must not have been created.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                    Should -BeFalse
            }
            finally {
                if (Test-Path -LiteralPath $malformedFeedFolder) {
                    Remove-Item -LiteralPath $malformedFeedFolder -Recurse -Force
                }
            }
        }
    }

    Context "Given_SchemaAssetIdentityDivergesFromManifest_SurfacedThroughStandardMode" {
        It "It_fails_fast_with_identity_mismatch_and_leaves_no_staged_workspace_when_ApiSchema_json_declares_a_different_project" {
            # The core package can pass package-manifest identity validation (packageId/projectName
            # say Ed-Fi) yet ship an ApiSchema.json whose internal project identity differs. The
            # stage-time identity lock must reject that divergence rather than silently staging the
            # wrong project.
            $divergentFeedFolder = script:New-TempDirectory

            try {
                $corePackageId = "EdFi.DataStandard52.ApiSchema"
                $coreNupkgPath = Join-Path $divergentFeedFolder "$($corePackageId.ToLowerInvariant()).1.0.329.nupkg"
                $coreStaging = Join-Path ([System.IO.Path]::GetTempPath()) "dms-std-divergent-$([Guid]::NewGuid().ToString('N'))"
                $coreApiSchemaDir = Join-Path $coreStaging "contentFiles/any/any/ApiSchema"
                New-Item -ItemType Directory -Path $coreApiSchemaDir -Force | Out-Null

                # Manifest declares the expected core identity (Ed-Fi/ed-fi).
                $coreManifest = [ordered]@{
                    version             = 1
                    packageId           = $corePackageId
                    projectName         = "Ed-Fi"
                    projectEndpointName = "ed-fi"
                    isExtensionProject  = $false
                    schemaPath          = "ApiSchema.json"
                    discoverySpecPath   = $null
                    xsdDirectory        = $null
                }
                $coreManifest | ConvertTo-Json -Depth 5 |
                    Set-Content -LiteralPath (Join-Path $coreApiSchemaDir "package-manifest.json") -Encoding utf8

                # ApiSchema.json deliberately declares a DIFFERENT project identity than the manifest.
                $coreApiSchema = [ordered]@{
                    apiSchemaVersion = "1.0.0"
                    projectSchema    = [ordered]@{
                        projectName         = "Other"
                        projectEndpointName = "other"
                        isExtensionProject  = $false
                        resourceSchemas     = [ordered]@{}
                        openApiBaseDocuments = [ordered]@{
                            resources = [ordered]@{ openapi = "3.0.1" }
                        }
                    }
                }
                $coreApiSchema | ConvertTo-Json -Depth 10 |
                    Set-Content -LiteralPath (Join-Path $coreApiSchemaDir "ApiSchema.json") -Encoding utf8

                [System.IO.Compression.ZipFile]::CreateFromDirectory($coreStaging, $coreNupkgPath)
                Remove-Item -LiteralPath $coreStaging -Recurse -Force

                {
                    script:Invoke-PrepareStandard -FeedFolder $divergentFeedFolder
                } | Should -Throw -ExpectedMessage "*identity mismatch*"

                # The staged ApiSchema workspace must not have been created.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                    Should -BeFalse
            }
            finally {
                if (Test-Path -LiteralPath $divergentFeedFolder) {
                    Remove-Item -LiteralPath $divergentFeedFolder -Recurse -Force
                }
            }
        }
    }

    Context "Given_StandardMode_ManifestDeclaredAssets_NonConventionalPaths" {
        It "It_stages_discovery_spec_and_xsd_from_manifest_declared_paths_at_canonical_workspace_locations" {
            # The package contract allows discoverySpecPath/xsdDirectory to be any safe relative
            # path. Staging must source content from the validated manifest paths - not rediscover
            # hard-coded siblings - while keeping the canonical workspace layout.
            $assetsFeedFolder = script:New-TempDirectory

            try {
                script:New-FixtureNupkg `
                    -FeedFolder $assetsFeedFolder `
                    -PackageId "EdFi.DataStandard52.ApiSchema" `
                    -Version "1.0.329" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false `
                    -DiscoverySpecPath "assets/discovery-spec.json" `
                    -XsdDirectory "static/xsd" | Out-Null

                $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA
                & $script:repo.PrepareSchemaScript `
                    -PackageFeedUrl $assetsFeedFolder `
                    -SchemaToolPath $tool | Out-Null

                # Content staged at canonical workspace paths regardless of source layout.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/content/Ed-Fi/discovery-spec.json") |
                    Should -BeTrue
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/content/Ed-Fi/xsd/Ed-Fi-Core.xsd") |
                    Should -BeTrue

                # ApiSchema manifest records the canonical workspace-relative paths.
                $apiSchemaManifest = script:Get-ApiSchemaManifest
                $coreProject = $apiSchemaManifest.projects | Where-Object { -not $_.isExtensionProject }
                $coreProject.discoverySpecPath | Should -Be "content/Ed-Fi/discovery-spec.json"
                $coreProject.xsdDirectory | Should -Be "content/Ed-Fi/xsd"
            }
            finally {
                if (Test-Path -LiteralPath $assetsFeedFolder) {
                    Remove-Item -LiteralPath $assetsFeedFolder -Recurse -Force
                }
            }
        }
    }

    Context "Given_StandardMode_ManifestNullAssets_WithUndeclaredSiblingContent" {
        It "It_does_not_stage_an_xsd_directory_the_manifest_records_as_null" {
            # Manifest-authoritative semantics: the package manifest is the contract for
            # schema-adjacent content. An xsd/ directory present in the payload but recorded as
            # null in the manifest must NOT be staged via sibling rediscovery.
            $undeclaredFeedFolder = script:New-TempDirectory

            try {
                script:New-FixtureNupkg `
                    -FeedFolder $undeclaredFeedFolder `
                    -PackageId "EdFi.DataStandard52.ApiSchema" `
                    -Version "1.0.329" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false `
                    -PlantUndeclaredXsdDirectory | Out-Null

                $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA
                & $script:repo.PrepareSchemaScript `
                    -PackageFeedUrl $undeclaredFeedFolder `
                    -SchemaToolPath $tool | Out-Null

                # The undeclared xsd directory must not have been staged.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/content/Ed-Fi/xsd") |
                    Should -BeFalse

                # The ApiSchema manifest core project must not record an xsdDirectory.
                $apiSchemaManifest = script:Get-ApiSchemaManifest
                $coreProject = $apiSchemaManifest.projects | Where-Object { -not $_.isExtensionProject }
                ($coreProject | Get-Member -Name "xsdDirectory" -MemberType NoteProperty) |
                    Should -BeNullOrEmpty
            }
            finally {
                if (Test-Path -LiteralPath $undeclaredFeedFolder) {
                    Remove-Item -LiteralPath $undeclaredFeedFolder -Recurse -Force
                }
            }
        }
    }

    Context "Given_MislabeledCorePackage_ConsistentIdentityForDifferentProject" {
        It "It_fails_fast_with_identity_mismatch_when_core_package_consistently_identifies_a_non_core_project" {
            # Core variant of the mislabeled-package scenario: the core package ID carries a
            # manifest+schema that consistently identify tpdm (with isExtensionProject false so the
            # extension-flag check passes). The core endpoint assertion (ed-fi) must reject it.
            $mislabeledCoreFeedFolder = script:New-TempDirectory

            try {
                script:New-FixtureNupkg `
                    -FeedFolder $mislabeledCoreFeedFolder `
                    -PackageId "EdFi.DataStandard52.ApiSchema" `
                    -Version "1.0.329" `
                    -ProjectName "TPDM" `
                    -ProjectEndpointName "tpdm" `
                    -IsExtensionProject $false | Out-Null

                $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA

                {
                    & $script:repo.PrepareSchemaScript `
                        -PackageFeedUrl $mislabeledCoreFeedFolder `
                        -SchemaToolPath $tool | Out-Null
                } | Should -Throw -ExpectedMessage "*identity mismatch*"

                # The staged ApiSchema workspace must not have been created.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                    Should -BeFalse
            }
            finally {
                if (Test-Path -LiteralPath $mislabeledCoreFeedFolder) {
                    Remove-Item -LiteralPath $mislabeledCoreFeedFolder -Recurse -Force
                }
            }
        }
    }

    Context "Given_InvalidPayload_ManifestIdMismatch_SurfacedThroughStandardMode" {
        It "It_fails_fast_with_identity_mismatch_and_leaves_no_staged_workspace_when_manifest_packageId_is_wrong" {
            # Build a malformed CORE .nupkg: the filename matches the pinned core ID so the resolver
            # finds it, but package-manifest.json declares a different packageId to trigger an identity
            # mismatch before workspace finalization.
            $mismatchFeedFolder = script:New-TempDirectory

            try {
                $corePackageId = "EdFi.DataStandard52.ApiSchema"
                $mismatchNupkgName = "$($corePackageId.ToLowerInvariant()).1.0.329.nupkg"
                $mismatchNupkgPath = Join-Path $mismatchFeedFolder $mismatchNupkgName
                $mismatchStaging = Join-Path ([System.IO.Path]::GetTempPath()) "dms-std-mismatch-$([Guid]::NewGuid().ToString('N'))"
                $mismatchApiSchemaDir = Join-Path $mismatchStaging "contentFiles/any/any/ApiSchema"
                New-Item -ItemType Directory -Path $mismatchApiSchemaDir -Force | Out-Null

                # Manifest declares a packageId that differs from the expected pinned core ID.
                $mismatchManifest = [ordered]@{
                    version             = 1
                    packageId           = "EdFi.DataStandard52.WRONG.ApiSchema"
                    projectName         = "Ed-Fi"
                    projectEndpointName = "ed-fi"
                    isExtensionProject  = $false
                    schemaPath          = "ApiSchema.json"
                    discoverySpecPath   = $null
                    xsdDirectory        = $null
                }
                $mismatchManifest | ConvertTo-Json -Depth 5 |
                    Set-Content -LiteralPath (Join-Path $mismatchApiSchemaDir "package-manifest.json") -Encoding utf8

                $mismatchApiSchema = [ordered]@{
                    apiSchemaVersion = "1.0.0"
                    projectSchema    = [ordered]@{
                        projectName         = "Ed-Fi"
                        projectEndpointName = "ed-fi"
                        isExtensionProject  = $false
                        resourceSchemas     = [ordered]@{}
                        openApiBaseDocuments = [ordered]@{
                            resources = [ordered]@{ openapi = "3.0.1" }
                        }
                    }
                }
                $mismatchApiSchema | ConvertTo-Json -Depth 10 |
                    Set-Content -LiteralPath (Join-Path $mismatchApiSchemaDir "ApiSchema.json") -Encoding utf8

                [System.IO.Compression.ZipFile]::CreateFromDirectory($mismatchStaging, $mismatchNupkgPath)
                Remove-Item -LiteralPath $mismatchStaging -Recurse -Force

                {
                    script:Invoke-PrepareStandard -FeedFolder $mismatchFeedFolder
                } | Should -Throw -ExpectedMessage "*identity mismatch*"

                # The staged ApiSchema workspace must not have been created.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                    Should -BeFalse
            }
            finally {
                if (Test-Path -LiteralPath $mismatchFeedFolder) {
                    Remove-Item -LiteralPath $mismatchFeedFolder -Recurse -Force
                }
            }
        }
    }

    Context "Given_MissingPackage_SurfacedThroughStandardMode" {
        It "It_fails_with_clear_resolution_diagnostic_and_no_partial_workspace_when_core_is_absent_from_feed" {
            # Feed is empty: the pinned core package cannot be resolved.
            $emptyFeed = script:New-TempDirectory

            {
                script:Invoke-PrepareStandard -FeedFolder $emptyFeed
            } | Should -Throw

            # No partial workspace should exist.
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                Should -BeFalse

            Remove-Item -LiteralPath $emptyFeed -Recurse -Force -ErrorAction SilentlyContinue
        }

        It "It_fails_with_resolution_diagnostic_and_no_partial_workspace_when_pinned_version_is_absent_from_feed" {
            # The catalog pins the core package at version 1.0.329. Supply the core package at version
            # 1.0.1 only so the resolver cannot find the pinned version 1.0.329.
            $wrongVersionFeed = script:New-TempDirectory
            try {
                script:New-FixtureNupkg `
                    -FeedFolder $wrongVersionFeed `
                    -PackageId "EdFi.DataStandard52.ApiSchema" `
                    -Version "1.0.1" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false | Out-Null

                {
                    script:Invoke-PrepareStandard -FeedFolder $wrongVersionFeed
                } | Should -Throw -ExpectedMessage "*version*1.0.329*not found*"

                # No partial workspace should exist.
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema") |
                    Should -BeFalse
            }
            finally {
                if (Test-Path -LiteralPath $wrongVersionFeed) {
                    Remove-Item -LiteralPath $wrongVersionFeed -Recurse -Force
                }
            }
        }
    }

    Context "Given_RerunReuse_SameSelection" {
        It "It_is_a_noop_on_second_run_with_identical_core_workspace_and_hash_are_unchanged" {
            $script:feedFolder = script:New-FixtureFeed

            # First run - stages the core workspace.
            script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

            $manifestBefore = script:Get-RootManifest
            $hashBefore = $manifestBefore.schema.effectiveSchemaHash
            $fingerprintBefore = $manifestBefore.schema.workspaceFingerprint

            # Second run - must not throw.
            { script:Invoke-PrepareStandard -FeedFolder $script:feedFolder } |
                Should -Not -Throw

            $manifestAfter = script:Get-RootManifest
            $manifestAfter.schema.effectiveSchemaHash | Should -Be $hashBefore
            $manifestAfter.schema.workspaceFingerprint | Should -Be $fingerprintBefore
        }
    }

    Context "Given_RerunMismatch_ChangedPackageContent" {
        It "It_fails_fast_with_workspace_fingerprint_mismatch_when_core_package_content_changes_under_a_stable_schema_hash" {
            # Same selection (core-only) and the SAME stable fake EffectiveSchemaHash across both runs,
            # but the republished core package ships different staged content on the second run. Reuse
            # must be gated on the workspace fingerprint, not just the schema hash: identical hash plus
            # drifted package payload must still fail fast with teardown guidance.

            # Run 1: core package with no discovery spec.
            $feedV1 = script:New-TempDirectory
            try {
                script:New-FixtureNupkg `
                    -FeedFolder $feedV1 `
                    -PackageId "EdFi.DataStandard52.ApiSchema" `
                    -Version "1.0.329" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false | Out-Null

                script:Invoke-PrepareStandard -FeedFolder $feedV1 -Hash $script:hashA

                # Run 2: same selection and stable hash, but the core package now ships an extra declared
                # discovery spec -> different staged content -> different workspace fingerprint.
                $feedV2 = script:New-TempDirectory
                try {
                    script:New-FixtureNupkg `
                        -FeedFolder $feedV2 `
                        -PackageId "EdFi.DataStandard52.ApiSchema" `
                        -Version "1.0.329" `
                        -ProjectName "Ed-Fi" `
                        -ProjectEndpointName "ed-fi" `
                        -IsExtensionProject $false `
                        -DiscoverySpecPath "discovery-spec.json" | Out-Null

                    {
                        script:Invoke-PrepareStandard -FeedFolder $feedV2 -Hash $script:hashA
                    } | Should -Throw -ExpectedMessage "*workspace fingerprint mismatch*Stop the local stack*"
                } finally {
                    if (Test-Path -LiteralPath $feedV2) {
                        Remove-Item -LiteralPath $feedV2 -Recurse -Force
                    }
                }
            } finally {
                if (Test-Path -LiteralPath $feedV1) {
                    Remove-Item -LiteralPath $feedV1 -Recurse -Force
                }
            }
        }
    }

    Context "Given_SchemaPackagesEnvVar_DoesNotInfluenceStandardMode" {
        It "It_produces_the_same_core_workspace_regardless_of_SCHEMA_PACKAGES_env_var_value" {
            # SCHEMA_PACKAGES is a non-bootstrap env var owned by the DLL-backed schema-loader path.
            # Standard mode must ignore it completely: the result must be a clean core-only workspace.
            $script:feedFolder = script:New-FixtureFeed

            $savedSchemaPackages = [System.Environment]::GetEnvironmentVariable("SCHEMA_PACKAGES")
            try {
                # Run with SCHEMA_PACKAGES pointing to something unrelated to the fixture feed.
                [System.Environment]::SetEnvironmentVariable(
                    "SCHEMA_PACKAGES",
                    "EdFi.DataStandard52.Homograph.ApiSchema:1.0.329"
                )

                script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

                $manifest = script:Get-RootManifest

                # Standard mode stages core only; SCHEMA_PACKAGES must not introduce any extension.
                $manifest.schema.selectionMode | Should -Be "Standard"
                $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                    Should -BeTrue
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Homograph/ApiSchema.json") |
                    Should -BeFalse
            }
            finally {
                [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", $savedSchemaPackages)
            }
        }
    }

    Context "Given_ExtensionsParameterRemoved_StaticAssertion" {
        # The package-backed core-only contract requires that no schema/wrapper surface exposes
        # -Extensions. Assert this from the parameter AST of every required surface (not a single regex
        # shape), so untyped or aliased parameters - and any forwarding shape - are also caught.
        It "It_<Name>_does_not_expose_an_Extensions_parameter_or_alias" -ForEach @(
            @{ Name = "prepare-dms-schema.ps1";      File = "prepare-dms-schema.ps1";      Function = "" }
            @{ Name = "bootstrap-local-dms.ps1";     File = "bootstrap-local-dms.ps1";     Function = "" }
            @{ Name = "bootstrap-published-dms.ps1"; File = "bootstrap-published-dms.ps1"; Function = "" }
            @{ Name = "Invoke-BootstrapWrapper";     File = "bootstrap-wrapper.psm1";      Function = "Invoke-BootstrapWrapper" }
        ) {
            $filePath = Join-Path $script:sourceDockerComposeRoot $File
            $paramBlock = script:Get-DeclaredParameterBlock -FilePath $filePath -FunctionName $Function
            script:Test-ExposesExtensionsParameter -ParamBlock $paramBlock |
                Should -BeFalse -Because "$Name must not expose -Extensions (parameter or alias) under the package-backed core-only contract"
        }

        It "It_wrapper_module_never_forwards_an_Extensions_argument_to_any_command" {
            # Robust (AST) forwarding check: no command invocation anywhere in bootstrap-wrapper.psm1 binds
            # an -Extensions parameter, in any forwarding shape. Combined with the surface assertions above
            # (the wrappers expose no -Extensions to splat), the wrapper cannot forward -Extensions.
            $filePath = Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$null, [ref]$parseErrors)
            $parseErrors.Count | Should -Be 0

            # Sanity: the wrapper still drives prepare-dms-schema.ps1, so the assertion is meaningful.
            (Get-Content -LiteralPath $filePath -Raw) | Should -Match "prepare-dms-schema\.ps1"

            $extensionsArguments = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $node.ParameterName -ieq 'Extensions'
                },
                $true)
            @($extensionsArguments).Count |
                Should -Be 0 -Because "the wrapper must never forward -Extensions to any command"
        }
    }

    Context "Given_PublishedToolLocation_AutoDiscovery" {
        It "It_Resolve-DmsSchemaTool_discovers_the_published_tool_under_.bootstrap_tools_dms-schema" {
            # The README publishes dms-schema to .bootstrap/tools/dms-schema, and the wrapper shorthand
            # (bootstrap-local-dms.ps1) cannot forward -SchemaToolPath. The resolver must auto-discover
            # that documented location so the documented happy path works after publishing, without
            # requiring -SchemaToolPath or DMS_SCHEMA_TOOL_PATH.
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-tool.psm1") -Force

            $toolDirectory = Join-Path $script:repo.BootstrapRoot "tools/dms-schema"
            New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
            $toolName = if ($IsWindows) { "dms-schema.exe" } else { "dms-schema" }
            $toolPath = Join-Path $toolDirectory $toolName
            "stub" | Set-Content -LiteralPath $toolPath -Encoding utf8

            $resolved = Resolve-DmsSchemaTool

            [System.IO.Path]::GetFullPath($resolved) |
                Should -Be ([System.IO.Path]::GetFullPath($toolPath))
        }
    }
}
