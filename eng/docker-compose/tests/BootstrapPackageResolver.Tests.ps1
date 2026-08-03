# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester fixture parameters are unused by design.')]
param()

Describe "DMS-1156 bootstrap package resolver" {
    BeforeAll {
        $script:sourceDockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:resolverModule = Join-Path $script:sourceDockerComposeRoot "bootstrap-package-resolver.psm1"

        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop

        function script:New-TestDirectory
        {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-resolver-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function script:New-ExtractedCorePackage
        {
            <#
            .SYNOPSIS
            Creates a minimal valid extracted asset-only core package tree on disk (no zip).
            Returns a PSCustomObject with ApiSchemaDirectory and PackageRoot.
            #>
            param(
                [string] $PackageId = "EdFi.DataStandard52.ApiSchema",
                [string] $Version   = "1.0.329",
                [switch] $IncludeDiscoverySpec,
                [switch] $IncludeXsd
            )

            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

            '{"apiSchemaVersion":"1.0.0"}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = $PackageId
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }

            if ($IncludeDiscoverySpec)
            {
                '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "discovery-spec.json") -Encoding utf8
                $manifest.discoverySpecPath = "discovery-spec.json"
            }

            if ($IncludeXsd)
            {
                $xsdDir = Join-Path $apiSchemaDir "xsd"
                New-Item -ItemType Directory -Path $xsdDir -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "Ed-Fi-Core.xsd") -Encoding utf8
                $manifest.xsdDirectory = "xsd"
            }

            $manifest | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            return [pscustomobject]@{
                PackageRoot        = $packageRoot
                ApiSchemaDirectory = $apiSchemaDir
            }
        }

        function script:New-ExtractedExtensionPackage
        {
            <#
            .SYNOPSIS
            Creates a minimal valid extracted asset-only extension package tree on disk (no zip).
            Returns a PSCustomObject with ApiSchemaDirectory and PackageRoot.
            #>
            param(
                [string] $PackageId = "EdFi.DataStandard52.Sample.ApiSchema",
                [string] $Version   = "1.0.329",
                [switch] $IncludeXsd
            )

            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

            '{"apiSchemaVersion":"1.0.0"}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = $PackageId
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = $true
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }

            if ($IncludeXsd)
            {
                $xsdDir = Join-Path $apiSchemaDir "xsd"
                New-Item -ItemType Directory -Path $xsdDir -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "EXTENSION-Core.xsd") -Encoding utf8
                $manifest.xsdDirectory = "xsd"
            }

            $manifest | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            return [pscustomobject]@{
                PackageRoot        = $packageRoot
                ApiSchemaDirectory = $apiSchemaDir
            }
        }

        function script:New-FixtureNupkg
        {
            <#
            .SYNOPSIS
            Creates a minimal asset-only ApiSchema .nupkg fixture in a local feed folder.
            The .nupkg contains contentFiles/any/any/ApiSchema/package-manifest.json and
            contentFiles/any/any/ApiSchema/ApiSchema.json (and optionally an xsd/ subfolder).
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

                [switch]
                $IncludeXsd
            )

            $nupkgName = "$($PackageId.ToLowerInvariant()).$($Version.ToLowerInvariant()).nupkg"
            $nupkgPath = Join-Path $FeedFolder $nupkgName

            # Build the zip (nupkg) contents in a temp staging dir, then zip it.
            $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "dms-nupkg-stage-$([Guid]::NewGuid().ToString('N'))"
            $apiSchemaDir = Join-Path $stagingDir "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

            # Minimal package-manifest.json
            $manifest = [ordered]@{
                packageId = $PackageId
                version   = $Version
            }
            $manifest | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            # Minimal ApiSchema.json
            $apiSchema = [ordered]@{
                apiSchemaVersion = "1.0.0"
                projectSchema    = [ordered]@{
                    projectName         = $PackageId
                    projectEndpointName = $PackageId.ToLowerInvariant()
                    isExtensionProject  = $false
                    resourceSchemas     = [ordered]@{}
                }
            }
            $apiSchema | ConvertTo-Json -Depth 10 |
                Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            if ($IncludeXsd)
            {
                $xsdDir = Join-Path $apiSchemaDir "xsd"
                New-Item -ItemType Directory -Path $xsdDir -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "Ed-Fi-Core.xsd") -Encoding utf8
            }

            # Create .nupkg as a zip from the staging dir.
            [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $nupkgPath)

            # Clean up staging dir.
            Remove-Item -LiteralPath $stagingDir -Recurse -Force

            return $nupkgPath
        }
    }

    BeforeEach {
        Remove-Module bootstrap-package-resolver -Force -ErrorAction SilentlyContinue
        Import-Module $script:resolverModule -Force

        $script:feedFolder = script:New-TestDirectory
        $script:destRoot = script:New-TestDirectory
    }

    AfterEach {
        Remove-Module bootstrap-package-resolver -Force -ErrorAction SilentlyContinue

        if ($null -ne $script:feedFolder -and (Test-Path -LiteralPath $script:feedFolder))
        {
            Remove-Item -LiteralPath $script:feedFolder -Recurse -Force
        }

        if ($null -ne $script:destRoot -and (Test-Path -LiteralPath $script:destRoot))
        {
            Remove-Item -LiteralPath $script:destRoot -Recurse -Force
        }
    }

    Context "Given_LocalFolderFeed" {
        It "It_resolves_and_extracts_core_package_and_exposes_ApiSchema_contract_path" {
            script:New-FixtureNupkg `
                -FeedFolder $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" | Out-Null

            $result = Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot

            $result.PackageId | Should -Be "EdFi.DataStandard52.ApiSchema"
            $result.Version | Should -Be "1.0.329"
            $result.ExtractionDirectory | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $result.ExtractionDirectory -PathType Container | Should -BeTrue
            $result.ApiSchemaDirectory | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $result.ApiSchemaDirectory -PathType Container | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $result.ApiSchemaDirectory "ApiSchema.json") -PathType Leaf | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $result.ApiSchemaDirectory "package-manifest.json") -PathType Leaf | Should -BeTrue
        }

        It "It_resolves_core_package_with_xsd_from_local_feed" {
            # Core-only standard mode: the resolver fetches the pinned core package and
            # exposes its optional XSD payload. Extension package resolution is out of scope — extension
            # and custom schema sets use the expert -ApiSchemaPath filesystem path, not package resolution.
            script:New-FixtureNupkg `
                -FeedFolder $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -IncludeXsd | Out-Null

            $result = Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot

            $result.PackageId | Should -Be "EdFi.DataStandard52.ApiSchema"
            Test-Path -LiteralPath (Join-Path $result.ApiSchemaDirectory "xsd/Ed-Fi-Core.xsd") -PathType Leaf | Should -BeTrue
        }

        It "It_resolves_via_file_URL_feed" {
            script:New-FixtureNupkg `
                -FeedFolder $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" | Out-Null

            $fileUrl = [System.Uri]::new($script:feedFolder).AbsoluteUri

            $result = Resolve-StandardSchemaPackage `
                -FeedUrl $fileUrl `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot

            Test-Path -LiteralPath $result.ApiSchemaDirectory -PathType Container | Should -BeTrue
        }

        It "It_throws_clear_error_when_package_id_not_in_local_feed" {
            # Feed is empty - no packages at all.
            { Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandard52.Nonexistent.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*not found in local feed folder*"
        }

        It "It_throws_clear_error_when_requested_version_not_in_local_feed" {
            # Package exists but only at a different version.
            script:New-FixtureNupkg `
                -FeedFolder $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.1" | Out-Null

            { Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*version*1.0.329*not found*"
        }

        It "It_never_falls_back_to_a_different_version_on_missing_pinned_version" {
            # Confirm error message explicitly states no fallback.
            script:New-FixtureNupkg `
                -FeedFolder $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.1" | Out-Null

            { Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*never falls back to latest*"
        }

        It "It_result_PackageRoot_matches_ExtractionDirectory" {
            script:New-FixtureNupkg `
                -FeedFolder $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" | Out-Null

            $result = Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot

            $result.PackageRoot | Should -Be $result.ExtractionDirectory
        }

        It "It_throws_when_extracted_package_lacks_ApiSchema_contract_directory" {
            # Build a .nupkg that does NOT contain contentFiles/any/any/ApiSchema/.
            $nupkgName = "edfi.datastandardnoapischema.1.0.0.nupkg"
            $nupkgPath = Join-Path $script:feedFolder $nupkgName

            $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "dms-noapischema-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
            "placeholder" | Set-Content -LiteralPath (Join-Path $stagingDir "dummy.txt") -Encoding utf8
            [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $nupkgPath)
            Remove-Item -LiteralPath $stagingDir -Recurse -Force

            { Resolve-StandardSchemaPackage `
                -FeedUrl $script:feedFolder `
                -PackageId "EdFi.DataStandardNoApiSchema" `
                -Version "1.0.0" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*contentFiles/any/any/ApiSchema/*"
        }
    }

    Context "Given_Assert-AssetOnlyPackageContract" {

        # SCOPE NOTE: these are package-CONTRACT VALIDATION tests, not extension package
        # RESOLUTION tests. Assert-AssetOnlyPackageContract is the general asset-only validator that
        # runs over whatever package the resolver extracted; its isExtensionProject / payload / manifest
        # rules must be proven for both core- and extension-shaped manifests regardless of the core-only
        # selection scope. The contract (06-package-backed-standard-schema-selection.md:115-118) removes
        # extension package RESOLUTION coverage (done in Given_LocalFolderFeed) while keeping core payload
        # and contract-validation coverage; an extension-shaped fixture here exercises a validator branch,
        # it does not stage or resolve an extension into standard mode.

        # ---- Happy path ----

        It "It_returns_validated_identity_for_valid_core_package" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            $result = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false

            $result.PackageId           | Should -Be "EdFi.DataStandard52.ApiSchema"
            $result.ProjectName         | Should -Be "Ed-Fi"
            $result.ProjectEndpointName | Should -Be "ed-fi"
            $result.IsExtensionProject  | Should -BeFalse
            $result.SchemaPath          | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $result.SchemaPath -PathType Leaf | Should -BeTrue
            $result.DiscoverySpecPath   | Should -BeNullOrEmpty
            $result.XsdDirectory        | Should -BeNullOrEmpty

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_returns_validated_identity_for_valid_core_package_with_optional_assets" {
            $pkg = script:New-ExtractedCorePackage `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -IncludeDiscoverySpec `
                -IncludeXsd

            $result = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false

            $result.DiscoverySpecPath | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $result.DiscoverySpecPath -PathType Leaf | Should -BeTrue
            $result.XsdDirectory | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $result.XsdDirectory -PathType Container | Should -BeTrue

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_returns_validated_identity_for_valid_extension_package" {
            # Validator-branch coverage (see SCOPE NOTE above): proves Assert-AssetOnlyPackageContract
            # accepts a well-formed extension-shaped manifest (isExtensionProject=true). This validates
            # the contract checker, not extension package resolution — nothing here resolves or stages the
            # extension through an env-driven standard-mode invocation.
            $pkg = script:New-ExtractedExtensionPackage `
                -PackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -IncludeXsd

            $result = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true

            $result.PackageId           | Should -Be "EdFi.DataStandard52.Sample.ApiSchema"
            $result.ProjectName         | Should -Be "Sample"
            $result.ProjectEndpointName | Should -Be "sample"
            $result.IsExtensionProject  | Should -BeTrue
            Test-Path -LiteralPath $result.SchemaPath -PathType Leaf | Should -BeTrue
            $result.XsdDirectory | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $result.XsdDirectory -PathType Container | Should -BeTrue

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        # ---- Missing asset-only payload ----

        It "It_throws_when_ApiSchema_directory_is_absent" {
            $packageRoot = script:New-TestDirectory

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory (Join-Path $packageRoot "contentFiles/any/any/ApiSchema") `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*contentFiles/any/any/ApiSchema/*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_no_schema_JSON_file_exists_in_ApiSchema_directory" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

            # Only manifest, no ApiSchema.json
            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*no schema JSON file found*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_multiple_schema_JSON_files_exist_in_ApiSchema_directory" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema2.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*multiple schema JSON files found*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_extra_root_JSON_shares_a_basename_with_a_nested_discoverySpecPath" {
            # Regression: the discoverySpecPath exclusion must match the actual declared file, not
            # just its basename. A nested discoverySpecPath (nested/Other.json) must NOT cause an
            # unrelated root Other.json to be excluded from the single-schema count.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            $nestedDir = Join-Path $apiSchemaDir "nested"
            New-Item -ItemType Directory -Path $nestedDir -Force | Out-Null

            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "Other.json") -Encoding utf8
            '{}' | Set-Content -LiteralPath (Join-Path $nestedDir "Other.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = "nested/Other.json"
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*multiple schema JSON files found*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        # ---- Missing or malformed package-manifest.json ----

        It "It_throws_when_manifest_version_is_a_non_integer_string" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # version is a non-empty string, so presence/non-empty checks pass; type check must fail.
            $manifest = [ordered]@{
                version              = "bad"
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'version' must be an integer manifest schema version*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_version_is_a_fractional_number" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A non-integer JSON number must be rejected (ConvertFrom-Json parses 1.5 as [double]).
            $manifestJson = '{ "version": 1.5, "packageId": "EdFi.DataStandard52.ApiSchema", "projectName": "Ed-Fi", "projectEndpointName": "ed-fi", "isExtensionProject": false, "schemaPath": "ApiSchema.json", "discoverySpecPath": null, "xsdDirectory": null }'
            $manifestJson | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'version' must be an integer manifest schema version*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_version_is_an_unsupported_integer" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 2
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*only manifest schema version 1 is supported*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_discoverySpecPath_is_not_a_string" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A non-string discoverySpecPath (JSON number) must be rejected as malformed, even though a
            # file whose name matches the coerced value exists. A raw [string] cast would let it pass.
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "123") -Encoding utf8
            $manifestJson = '{ "version": 1, "packageId": "EdFi.DataStandard52.ApiSchema", "projectName": "Ed-Fi", "projectEndpointName": "ed-fi", "isExtensionProject": false, "schemaPath": "ApiSchema.json", "discoverySpecPath": 123, "xsdDirectory": null }'
            $manifestJson | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'discoverySpecPath' must be a JSON string*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_xsdDirectory_is_not_a_string" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A non-string xsdDirectory (JSON boolean) must be rejected as malformed, even though a
            # directory whose name matches the coerced value ([string]$true -> "True") exists with XSD content.
            $coercedDir = Join-Path $apiSchemaDir "True"
            New-Item -ItemType Directory -Path $coercedDir -Force | Out-Null
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $coercedDir "Ed-Fi-Core.xsd") -Encoding utf8
            $manifestJson = '{ "version": 1, "packageId": "EdFi.DataStandard52.ApiSchema", "projectName": "Ed-Fi", "projectEndpointName": "ed-fi", "isExtensionProject": false, "schemaPath": "ApiSchema.json", "discoverySpecPath": null, "xsdDirectory": true }'
            $manifestJson | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'xsdDirectory' must be a JSON string*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_required_string_field_<Field>_is_not_a_string" -ForEach @(
            @{ Field = "packageId" }
            @{ Field = "projectName" }
            @{ Field = "projectEndpointName" }
            @{ Field = "schemaPath" }
        ) {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A required identity/path field given as a non-string (JSON number) must be rejected as a
            # contract violation here, not coerced or deferred to a later non-contract failure (e.g.
            # Int32.Equals(string,...) on packageId, or [string]/Join-Path on schemaPath downstream).
            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest[$Field] = 123
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'$Field' must be a JSON string*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_package_manifest_json_is_absent" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8
            # No package-manifest.json

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*package-manifest.json*missing*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_package_manifest_json_is_not_valid_JSON" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8
            "{ not valid json !!!" | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*package-manifest.json*not valid JSON*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_package_manifest_json_is_missing_required_field_projectName" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # Missing projectName
            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*missing required field*projectName*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_a_clear_error_when_required_packageId_is_JSON_null" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A JSON null required field must produce the clear "missing required field" message, not a raw
            # "cannot call a method on a null-valued expression" error from the later identity check.
            $manifestJson = '{ "version": 1, "packageId": null, "projectName": "Ed-Fi", "projectEndpointName": "ed-fi", "isExtensionProject": false, "schemaPath": "ApiSchema.json", "discoverySpecPath": null, "xsdDirectory": null }'
            $manifestJson | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*missing required field*packageId*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_a_clear_error_when_required_schemaPath_is_JSON_null" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A JSON null schemaPath must be rejected by the required-field guard, not reach Join-Path.
            $manifestJson = '{ "version": 1, "packageId": "EdFi.DataStandard52.ApiSchema", "projectName": "Ed-Fi", "projectEndpointName": "ed-fi", "isExtensionProject": false, "schemaPath": null, "discoverySpecPath": null, "xsdDirectory": null }'
            $manifestJson | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*missing required field*schemaPath*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        # ---- Forbidden DLL/assembly shape ----

        It "It_throws_when_package_contains_a_dll_file" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            # Plant a DLL in a lib/ directory
            $libDir = Join-Path $pkg.PackageRoot "lib/net8.0"
            New-Item -ItemType Directory -Path $libDir -Force | Out-Null
            [System.IO.File]::WriteAllBytes((Join-Path $libDir "EdFi.ApiSchema.dll"), [byte[]]@(0, 0, 0, 0))

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*forbidden*"

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_throws_when_package_contains_a_cs_file" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            "// Marker.cs" | Set-Content -LiteralPath (Join-Path $pkg.PackageRoot "Marker.cs") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*forbidden*"

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_throws_when_package_contains_a_lib_directory" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            New-Item -ItemType Directory -Path (Join-Path $pkg.PackageRoot "lib") -Force | Out-Null

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*forbidden*lib/*"

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_throws_when_package_contains_a_ref_directory" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            New-Item -ItemType Directory -Path (Join-Path $pkg.PackageRoot "ref") -Force | Out-Null

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*forbidden*ref/*"

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_throws_when_package_contains_a_nested_forbidden_directory" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            # A forbidden directory (bin/) nested below the contract path, carrying only an
            # innocuous file, must still be rejected even though it contains no *.dll or *.cs.
            $nestedBin = Join-Path $pkg.ApiSchemaDirectory "bin"
            New-Item -ItemType Directory -Path $nestedBin -Force | Out-Null
            "harmless" | Set-Content -LiteralPath (Join-Path $nestedBin "foo.txt") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*forbidden*bin/*"

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        # ---- Identity mismatch ----

        It "It_throws_when_manifest_packageId_does_not_match_expected" {
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.WRONG.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*identity mismatch*"

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_throws_when_extension_manifest_declares_isExtensionProject_false" {
            # Extension package whose manifest incorrectly says isExtensionProject: false
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.Sample.ApiSchema"
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = $false   # <-- wrong: says false but caller expects extension
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true
            } | Should -Throw -ExpectedMessage "*isExtensionProject*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_core_manifest_declares_isExtensionProject_true" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $true    # <-- wrong: says true but caller expects core
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*isExtensionProject*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_extension_manifest_declares_isExtensionProject_as_string_false" {
            # PowerShell coerces any non-empty string (including "false") to $true under a raw
            # [bool] cast, so a string-typed flag must be rejected as malformed, not coerced.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.Sample.ApiSchema"
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = "false"   # <-- malformed: JSON string, not boolean
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true
            } | Should -Throw -ExpectedMessage "*isExtensionProject*must be a JSON boolean*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_declares_isExtensionProject_as_non_boolean_string" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.Sample.ApiSchema"
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = "not-a-bool"   # <-- malformed: arbitrary string
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true
            } | Should -Throw -ExpectedMessage "*isExtensionProject*must be a JSON boolean*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_core_manifest_declares_isExtensionProject_as_null" {
            # A JSON null required field is rejected by the required-field guard (null is treated as a
            # missing field), before the boolean-type check below, so the message is "missing required field".
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $null   # <-- malformed: JSON null
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*missing required field*isExtensionProject*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        # ---- Manifest-declared static assets missing on disk ----

        It "It_throws_when_manifest_discoverySpecPath_does_not_exist_on_disk" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = "discovery-spec.json"   # declared but missing on disk
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*discoverySpecPath*does not exist*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_discoverySpecPath_is_a_zero_byte_file" {
            # A declared discoverySpecPath that exists but is empty would otherwise pass the
            # file-exists check yet finalize a workspace advertising a discovery spec it cannot serve.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # The declared discovery spec exists on disk but is zero bytes.
            $discoverySpecFile = Join-Path $apiSchemaDir "discovery-spec.json"
            New-Item -ItemType File -Path $discoverySpecFile -Force | Out-Null

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = "discovery-spec.json"   # declared but empty on disk
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*discoverySpecPath*is empty*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_xsdDirectory_does_not_exist_on_disk" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.Sample.ApiSchema"
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = $true
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = "xsd"   # declared but missing on disk
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true
            } | Should -Throw -ExpectedMessage "*xsdDirectory*does not exist*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_xsdDirectory_exists_but_contains_no_xsd_files" {
            # A declared xsdDirectory that exists but holds no *.xsd content would otherwise pass the
            # directory-exists check yet stage nothing (prepare-dms-schema.ps1 records XSD only when
            # *.xsd files exist), silently finalizing a workspace missing the advertised XSD content.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # The declared directory exists but contains a non-XSD file only (no *.xsd).
            $xsdDir = Join-Path $apiSchemaDir "xsd"
            New-Item -ItemType Directory -Path $xsdDir -Force | Out-Null
            "not an xsd" | Set-Content -LiteralPath (Join-Path $xsdDir "README.txt") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.Sample.ApiSchema"
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = $true
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = "xsd"   # declared but holds no *.xsd content
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true
            } | Should -Throw -ExpectedMessage "*xsdDirectory*no .xsd files were found*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_allows_null_discoverySpecPath_without_error" {
            # Core package with discoverySpecPath explicitly null - should not throw.
            $pkg = script:New-ExtractedCorePackage -PackageId "EdFi.DataStandard52.ApiSchema"

            $result = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false

            $result.DiscoverySpecPath | Should -BeNullOrEmpty

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_allows_null_xsdDirectory_without_error" {
            # Extension package with xsdDirectory null - should not throw.
            $pkg = script:New-ExtractedExtensionPackage -PackageId "EdFi.DataStandard52.Sample.ApiSchema"

            $result = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $pkg.ApiSchemaDirectory `
                -PackageRoot $pkg.PackageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true

            $result.XsdDirectory | Should -BeNullOrEmpty

            Remove-Item -LiteralPath $pkg.PackageRoot -Recurse -Force
        }

        It "It_allows_manifest_that_omits_optional_fields_without_StrictMode_error" {
            # A conforming manifest may omit discoverySpecPath/xsdDirectory entirely (not merely set
            # them to null). Under Set-StrictMode, accessing an absent property throws; the validator
            # must treat omitted optional fields the same as null.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # discoverySpecPath and xsdDirectory keys are intentionally absent.
            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            $result = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false

            $result.DiscoverySpecPath | Should -BeNullOrEmpty
            $result.XsdDirectory | Should -BeNullOrEmpty

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_discoverySpecPath_is_empty_string" {
            # Absence is expressed only by omitting the key or JSON null. A present-but-empty string is
            # a malformed declared path and must fail fast, not be silently treated as absent.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = ""
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'discoverySpecPath' is present but empty*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_xsdDirectory_is_empty_string" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = ""
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*'xsdDirectory' is present but empty*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        # ---- Manifest path traversal ----

        It "It_throws_when_manifest_schemaPath_contains_parent_traversal" {
            # A manifest schemaPath with '..' could point outside contentFiles/any/any/ApiSchema/.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "../../escape.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*must not contain '..'*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_xsdDirectory_is_rooted" {
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.Sample.ApiSchema"
                projectName          = "Sample"
                projectEndpointName  = "sample"
                isExtensionProject   = $true
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = "/etc"
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.Sample.ApiSchema" `
                -ExpectedIsExtension $true
            } | Should -Throw -ExpectedMessage "*is rooted*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        # ---- Duplicate normalized paths ----

        It "It_throws_when_duplicate_normalized_paths_exist_in_package_payload" {
            # Simulate two files that normalize to the same path (case difference on a
            # case-sensitive filesystem, or a direct duplicate on case-insensitive).
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "ApiSchema.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            # Create a second directory that after normalization (case-insensitive) collides with the first.
            # We do this by creating a sibling directory at the package root level that mirrors the content,
            # creating a file at a path whose normalized form duplicates an existing one.
            # Use a sub-dir that has the same normalized path as an existing file.
            # Easiest: add the same file content under a different casing sub-path (works on macOS/Linux).
            # On case-insensitive FS (Windows/macOS default) this would fail mkdir; use same-name
            # files in different subdirs that normalize identically.
            $docsDir = Join-Path $packageRoot "docs"
            New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
            "readme" | Set-Content -LiteralPath (Join-Path $docsDir "README.md") -Encoding utf8
            # Add a second file that normalizes to the same path as an existing one.
            # We'll create an 'extra' dir alongside docs with the same normalized structure.
            $extraDir = Join-Path $packageRoot "extra"
            New-Item -ItemType Directory -Path $extraDir -Force | Out-Null
            "readme2" | Set-Content -LiteralPath (Join-Path $extraDir "README.md") -Encoding utf8
            # Now manually call the function with a patched package root that has duplicate paths.
            # The simplest cross-platform approach: build the package root so two files share the same
            # normalized relative path by copying the file into the same logical location.
            # Because we can't create identical file paths on any FS, we instead inject a collision
            # by writing the same normalized path twice into an in-memory list - but that's not
            # exercising the real code. Instead, test the detection through the real code by creating
            # a directory structure where two separate subdirectories each contain a file named identically
            # such that the relative paths differ only by parent directory letter casing, which on
            # macOS HFS+ (case-insensitive) fails to create. On case-sensitive FS (Linux/ext4) it works.
            #
            # Cross-platform strategy: use the SAME file in a symlink or hard link - not available here.
            # Fallback: test duplicate detection via a single flat directory with the same file included
            # twice - not possible on any real FS. Instead, verify that the duplicate-path detection
            # code executes without error on a clean package (no duplicates), and separately use a unit
            # approach by verifying the error message is reachable from code inspection.
            #
            # Actual cross-platform approach: put two files with names that differ only in extension
            # casing - e.g. "file.JSON" and "file.json" - which on Linux creates two distinct files
            # that normalize to the same path.
            $dupeDir = Join-Path $packageRoot "dupedir"
            New-Item -ItemType Directory -Path $dupeDir -Force | Out-Null
            "a" | Set-Content -LiteralPath (Join-Path $dupeDir "data.json") -Encoding utf8

            # Check whether FS is case-sensitive for this directory.
            $fileB = Join-Path $dupeDir "DATA.JSON"
            $isCaseSensitive = -not (Test-Path -LiteralPath $fileB)

            if ($isCaseSensitive)
            {
                # Linux/case-sensitive: create DATA.JSON as a distinct file that normalizes to data.json
                "b" | Set-Content -LiteralPath $fileB -Encoding utf8

                { Assert-AssetOnlyPackageContract `
                    -ApiSchemaDirectory $apiSchemaDir `
                    -PackageRoot $packageRoot `
                    -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                    -ExpectedIsExtension $false
                } | Should -Throw -ExpectedMessage "*duplicate normalized relative path*"
            }
            else
            {
                # macOS/Windows: case-insensitive FS - can't create distinct files with same normalized
                # path. Skip the throw assertion and just verify the happy path runs cleanly.
                { Assert-AssetOnlyPackageContract `
                    -ApiSchemaDirectory $apiSchemaDir `
                    -PackageRoot $packageRoot `
                    -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                    -ExpectedIsExtension $false
                } | Should -Not -Throw
            }

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        # ---- schemaPath / root schema mismatch ----

        It "It_throws_when_manifest_schemaPath_references_nested_JSON_instead_of_root_schema" {
            # A package that has a valid root ApiSchema.json, but whose manifest schemaPath
            # points at a different nested file (nested/Other.json) - must throw the mismatch diagnostic.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            $nestedDir    = Join-Path $apiSchemaDir "nested"
            New-Item -ItemType Directory -Path $nestedDir -Force | Out-Null

            # The one root-level schema JSON (counted in step 6).
            '{"apiSchemaVersion":"1.0.0"}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # A nested file that the manifest mistakenly references as schemaPath.
            '{"apiSchemaVersion":"1.0.0"}' | Set-Content -LiteralPath (Join-Path $nestedDir "Other.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "nested/Other.json"
                discoverySpecPath    = $null
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*manifest 'schemaPath' must reference the single schema JSON at the asset-only contract root*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }

        It "It_throws_when_manifest_schemaPath_references_the_discovery_spec_file" {
            # A package whose manifest has schemaPath and discoverySpecPath both pointing at
            # the same discovery-spec.json file - the root-schema count sees ApiSchema.json as
            # the single schema, but schemaPath references discovery-spec.json, so it must throw.
            $packageRoot = script:New-TestDirectory
            $apiSchemaDir = Join-Path $packageRoot "contentFiles/any/any/ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

            # The actual root schema JSON (counted in step 6 after excluding discoverySpecPath).
            '{"apiSchemaVersion":"1.0.0"}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "ApiSchema.json") -Encoding utf8

            # The discovery-spec file also exists on disk.
            '{}' | Set-Content -LiteralPath (Join-Path $apiSchemaDir "discovery-spec.json") -Encoding utf8

            $manifest = [ordered]@{
                version              = 1
                packageId            = "EdFi.DataStandard52.ApiSchema"
                projectName          = "Ed-Fi"
                projectEndpointName  = "ed-fi"
                isExtensionProject   = $false
                schemaPath           = "discovery-spec.json"
                discoverySpecPath    = "discovery-spec.json"
                xsdDirectory         = $null
            }
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $apiSchemaDir "package-manifest.json") -Encoding utf8

            { Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $apiSchemaDir `
                -PackageRoot $packageRoot `
                -ExpectedPackageId "EdFi.DataStandard52.ApiSchema" `
                -ExpectedIsExtension $false
            } | Should -Throw -ExpectedMessage "*manifest 'schemaPath' must reference the single schema JSON at the asset-only contract root*"

            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }
    }

    Context "Given_HttpV3Feed_StrictModeHardening" {
        It "It_throws_clear_diagnostic_when_service_index_lacks_resources" {
            # A service index that parses as valid JSON but omits 'resources' must surface the
            # advertise diagnostic, not a raw StrictMode property-not-found error.
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                [pscustomobject]@{ Content = '{ "version": "3.0.0" }' }
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*does not advertise a PackageBaseAddress*"
        }

        It "It_throws_version_diagnostic_when_version_index_lacks_versions" {
            # Valid service index, but the version index omits 'versions'. Must surface the
            # pinned-version-not-found diagnostic rather than a StrictMode crash on .versions.
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                if ($Uri -eq "https://example.test/v3/index.json") {
                    return [pscustomobject]@{
                        Content = '{"resources":[{"@id":"https://example.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}'
                    }
                }
                return [pscustomobject]@{ Content = '{}' }
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*was not found*"
        }

        It "It_throws_clear_diagnostic_when_the_service_index_is_unreachable" {
            # A network/HTTP failure fetching the service index must surface the unreachable-feed
            # diagnostic rather than a raw web exception.
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                throw "The remote name could not be resolved."
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*service index is unreachable*"
        }

        It "It_throws_clear_diagnostic_when_the_service_index_returns_malformed_JSON" {
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                [pscustomobject]@{ Content = '{ this is not valid json' }
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*service index*returned malformed JSON*"
        }

        It "It_throws_clear_diagnostic_when_the_version_index_request_fails" {
            # Service index resolves, but the flat-container version-index request fails (e.g. the
            # package id path does not exist). Must surface the package-not-found-on-feed diagnostic.
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                if ($Uri -eq "https://example.test/v3/index.json") {
                    return [pscustomobject]@{
                        Content = '{"resources":[{"@id":"https://example.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}'
                    }
                }
                throw "404 (Not Found)."
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*version index request failed*"
        }

        It "It_throws_clear_diagnostic_when_the_version_index_returns_malformed_JSON" {
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                if ($Uri -eq "https://example.test/v3/index.json") {
                    return [pscustomobject]@{
                        Content = '{"resources":[{"@id":"https://example.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}'
                    }
                }
                return [pscustomobject]@{ Content = '{ broken version index' }
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*version index*returned malformed JSON*"
        }

        It "It_throws_clear_diagnostic_when_the_nupkg_download_fails" {
            # Service index and version index resolve and list the pinned version, but the flat-container
            # .nupkg download fails. Must surface the download-failure diagnostic.
            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                if ($OutFile) {
                    throw "503 (Service Unavailable)."
                }
                if ($Uri -eq "https://example.test/v3/index.json") {
                    return [pscustomobject]@{
                        Content = '{"resources":[{"@id":"https://example.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}'
                    }
                }
                return [pscustomobject]@{ Content = '{"versions":["1.0.329"]}' }
            }

            { Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot
            } | Should -Throw -ExpectedMessage "*Failed to download package*"
        }
    }

    Context "Given_HttpV3Feed_SuccessPath" {
        It "It_resolves_and_extracts_a_package_from_a_configured_HTTP_NuGet_v3_feed" {
            # Positive coverage for the production default: an Azure-style HTTP NuGet v3 feed. The mock
            # serves the three real request stages — service index (advertises PackageBaseAddress/3.0.0),
            # flat-container version index (lists the pinned version), and the .nupkg flat-container
            # download (writes a real asset-only package to -OutFile) — so the full configured-feed
            # success path is exercised end to end, not just the local-folder override used elsewhere.
            $sourceFeed = script:New-TestDirectory
            $script:httpFixtureNupkg = script:New-FixtureNupkg `
                -FeedFolder $sourceFeed `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329"

            Mock -ModuleName bootstrap-package-resolver Invoke-WebRequest {
                if ($OutFile) {
                    # Flat-container .nupkg download: materialize the pinned package at the requested path.
                    Copy-Item -LiteralPath $script:httpFixtureNupkg -Destination $OutFile -Force
                    return
                }
                if ($Uri -eq "https://example.test/v3/index.json") {
                    return [pscustomobject]@{
                        Content = '{"resources":[{"@id":"https://example.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}'
                    }
                }
                if ($Uri -eq "https://example.test/flat/edfi.datastandard52.apischema/index.json") {
                    return [pscustomobject]@{ Content = '{"versions":["1.0.329"]}' }
                }
                throw "Unexpected URI requested by the resolver: $Uri"
            }

            $result = Resolve-StandardSchemaPackage `
                -FeedUrl "https://example.test/v3/index.json" `
                -PackageId "EdFi.DataStandard52.ApiSchema" `
                -Version "1.0.329" `
                -DestinationRoot $script:destRoot

            $result.PackageId | Should -Be "EdFi.DataStandard52.ApiSchema"
            $result.Version | Should -Be "1.0.329"
            Test-Path -LiteralPath $result.ApiSchemaDirectory -PathType Container | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $result.ApiSchemaDirectory "ApiSchema.json") -PathType Leaf |
                Should -BeTrue
            Test-Path -LiteralPath (Join-Path $result.ApiSchemaDirectory "package-manifest.json") -PathType Leaf |
                Should -BeTrue

            # The downloaded .nupkg artifact is removed after extraction (only the HTTP path downloads it).
            Test-Path -LiteralPath (Join-Path $result.ExtractionDirectory "edfi.datastandard52.apischema.1.0.329.nupkg") |
                Should -BeFalse

            # Pin the full HTTP flow: service index, version index, and flat-container download.
            Should -Invoke -ModuleName bootstrap-package-resolver Invoke-WebRequest -Times 3 -Exactly

            Remove-Item -LiteralPath $sourceFeed -Recurse -Force
        }
    }
}
