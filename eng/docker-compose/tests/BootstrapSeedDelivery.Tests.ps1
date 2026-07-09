# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester callback scriptblocks keep delegate-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
param()

Describe "DMS-1152 API seed delivery bootstrap" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"
        $script:seedCatalogPath = Join-Path $script:sourceDockerComposeRoot "seed-catalog.json"

        function script:New-TestDirectory {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-seed-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function script:Copy-WrapperCompositionPrerequisites {
            param([string]$DockerComposeRoot)

            # Invoke-BootstrapWrapper composes the default Data Standard bootstrap overlay onto the
            # effective env file for bootstrap-local-dms.ps1 always, and for
            # bootstrap-published-dms.ps1 only when -DataStandardVersion is explicitly supplied.
            # Every isolated fixture that executes either wrapper still needs the composition
            # utility module, the tracked bootstrap overlay files, and a base env file for the
            # composition to read (mirroring the tracked .env.example) so the paths that do compose
            # succeed.
            foreach ($fileName in @("env-utility.psm1", ".env.bootstrap.ds52", ".env.bootstrap.ds61")) {
                Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $fileName) -Destination $DockerComposeRoot -Force
            }

            $exampleEnvFile = Join-Path $DockerComposeRoot ".env.example"
            if (-not (Test-Path -LiteralPath $exampleEnvFile)) {
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $exampleEnvFile -Encoding utf8
            }
        }

        function script:New-IsolatedSeedRepo {
            $repoRoot = New-TestDirectory
            $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

            foreach ($fileName in @("bootstrap-manifest.psm1", "env-utility.psm1")) {
                Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $fileName) -Destination $dockerComposeRoot
            }

            # Minimal seed-catalog.json (v1 empty extensions)
            '{"version":1,"extensions":{}}' | Set-Content -LiteralPath (Join-Path $dockerComposeRoot "seed-catalog.json") -Encoding utf8

            $bootstrapRoot = Join-Path $dockerComposeRoot ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null

            return [pscustomobject]@{
                RepoRoot          = $repoRoot
                DockerComposeRoot = $dockerComposeRoot
                BootstrapRoot     = $bootstrapRoot
                SeedCatalogPath   = Join-Path $dockerComposeRoot "seed-catalog.json"
            }
        }

        function script:New-FakeManifest {
            param(
                [string]$BootstrapRoot,
                [string]$SelectionMode = "Standard",
                [string[]]$SelectedExtensions = @(),
                [string]$EffectiveSchemaHash = "abc123",
                [string]$ClaimsFingerprint = "def456"
            )

            $manifest = [ordered]@{
                version = 1
                schema  = [ordered]@{
                    selectionMode         = $SelectionMode
                    selectedExtensions    = @($SelectedExtensions)
                    effectiveSchemaHash   = $EffectiveSchemaHash
                    apiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                }
                claims  = [ordered]@{
                    directory                  = "claims"
                    fingerprint                = $ClaimsFingerprint
                    expectedVerificationChecks = @()
                }
                seed    = [ordered]@{
                    extensionNamespacePrefixes = @()
                }
            }

            $manifestPath = Join-Path $BootstrapRoot "bootstrap-manifest.json"
            $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            return $manifestPath
        }

        # Test fixture that builds an isolated Ed-Fi-Data-Standard repo root matching the v5.2.0 tag layout:
        #   <root>/Descriptors/{ADescriptor.xml, BDescriptor.xml, ...}
        #   <root>/Samples/Sample XML/{EducationOrganization.xml, Student.xml, StudentEnrollment.xml,
        #                              [sample-only descriptors like AncestryEthnicOriginDescriptor.xml]}
        #   <root>/Schemas/Bulk/{Interchange-EducationOrganization.xsd, ...} (when -IncludeBulkXsds)
        function script:New-FakeDataStandardRoot {
            param(
                [string]$Root,
                [bool]$IncludeDescriptors = $true,
                [bool]$IncludeSampleXml = $true,
                [bool]$IncludeBulkXsds = $false,
                [string[]]$DescriptorNames = @("ADescriptor", "BDescriptor", "CDescriptor"),
                [string[]]$SampleResourceNames = @("EducationOrganization.xml", "Student.xml", "StudentEnrollment.xml"),
                [string[]]$SampleDescriptorNames = @(),
                [string[]]$BulkXsdNames = @()
            )

            if ($IncludeDescriptors) {
                $descriptorsDir = Join-Path $Root "Descriptors"
                New-Item -ItemType Directory -Path $descriptorsDir -Force | Out-Null
                foreach ($name in $DescriptorNames) {
                    "<root/>" | Set-Content -LiteralPath (Join-Path $descriptorsDir "$name.xml") -Encoding utf8
                }
            }

            if ($IncludeSampleXml) {
                $sampleXmlDir = Join-Path $Root "Samples/Sample XML"
                New-Item -ItemType Directory -Path $sampleXmlDir -Force | Out-Null
                foreach ($name in $SampleResourceNames) {
                    "<root/>" | Set-Content -LiteralPath (Join-Path $sampleXmlDir $name) -Encoding utf8
                }
                foreach ($name in $SampleDescriptorNames) {
                    "<root/>" | Set-Content -LiteralPath (Join-Path $sampleXmlDir "$name.xml") -Encoding utf8
                }
            }

            if ($IncludeBulkXsds) {
                $xsdDir = Join-Path $Root "Schemas/Bulk"
                New-Item -ItemType Directory -Path $xsdDir -Force | Out-Null
                foreach ($name in $BulkXsdNames) {
                    "<xs:schema xmlns:xs=`"http://www.w3.org/2001/XMLSchema`"/>" | Set-Content -LiteralPath (Join-Path $xsdDir "$name.xsd") -Encoding utf8
                }
            }
        }

        # Dot-source the production seed script to expose its helpers (Resolve-SeedSource,
        # Initialize-CoreSeedSource, New-SeedWorkspace, Get-SeedFileTargetName, Resolve-SeedTargetDataStores,
        # Get-SeedXsdDirectory, Invoke-BulkLoadClient, etc.). The orchestration block is guarded
        # against dot-sourcing via the InvocationName check at the bottom of the script.
        Import-Module "$script:sourceDockerComposeRoot/bootstrap-manifest.psm1" -Force
        Import-Module "$script:sourceDockerComposeRoot/../Package-Management.psm1" -Force
        . "$script:sourceDockerComposeRoot/load-dms-seed-data.ps1"
        $script:seedWorkspaceInterchangeNames = @(
            "AssessmentMetadata",
            "Course",
            "Descriptor",
            "Descriptors",
            "EducationOrganization",
            "Student",
            "StudentAttendance",
            "StudentAssessment",
            "StudentEnrollment"
        )
    }

    BeforeEach {
        $script:repo = New-IsolatedSeedRepo
    }

    AfterEach {
        if ($null -ne $script:repo -and (Test-Path -LiteralPath $script:repo.RepoRoot)) {
            Remove-Item -LiteralPath $script:repo.RepoRoot -Recurse -Force
        }
    }

    Context "seed source selection" {
        It "defaults to Minimal when no seed source flag is supplied in Standard mode" {
            $fakeSourceRoot = New-TestDirectory
            $minimalDescriptorsDir = Join-Path $fakeSourceRoot "minimal/descriptors"
            New-Item -ItemType Directory -Path $minimalDescriptorsDir -Force | Out-Null
            "<root/>" | Set-Content -LiteralPath (Join-Path $minimalDescriptorsDir "ADescriptor.xml") -Encoding utf8

            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }
            $result = Resolve-SeedSource `
                -Manifest $manifest `
                -BuiltInSourceRoot $fakeSourceRoot `
                -CatalogPath $script:repo.SeedCatalogPath

            $result.Kind | Should -Be "BuiltIn"
            $result.Template | Should -Be "Minimal"
            $result.SourceDirectory | Should -Match "minimal"
            $result.DescriptorsDirectory | Should -Match "descriptors"
            $result.ResourcesDirectory | Should -BeNullOrEmpty

            Remove-Item -LiteralPath $fakeSourceRoot -Recurse -Force
        }

        It "rejects both SeedTemplate and SeedDataPath when supplied together" {
            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }
            {
                Resolve-SeedSource `
                    -Manifest $manifest `
                    -SeedTemplate "Minimal" `
                    -SeedDataPath "/some/custom/path" `
                    -BuiltInSourceRoot (New-TestDirectory) `
                    -CatalogPath $script:repo.SeedCatalogPath
            } | Should -Throw -ExpectedMessage "*mutually exclusive*"
        }

        It "rejects both DataStoreId and SchoolYear when supplied together" {
            # Regression: passing -DataStoreId @(7) -SchoolYear @(2024) would short-circuit instance
            # selection to id 7 but the orchestrator loop still iterated $SchoolYear, building
            # /{year} URLs that routed to whichever instance had the matching route context - not
            # instance 7. The credentials and the URL silently disagreed.
            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }

            {
                Assert-SeedSelectionInputs `
                    -Manifest $manifest `
                    -DataStoreId @(7) `
                    -SchoolYear @(2024)
            } | Should -Throw -ExpectedMessage "*-DataStoreId and -SchoolYear are mutually exclusive*"

            # Each in isolation is still accepted
            { Assert-SeedSelectionInputs -Manifest $manifest -DataStoreId @(7) } | Should -Not -Throw
            { Assert-SeedSelectionInputs -Manifest $manifest -SchoolYear @(2024) } | Should -Not -Throw
        }

        It "rejects multiple -DataStoreId values because the unqualified path cannot disambiguate" {
            # Regression: -DataStoreId @(1,2) flowed through Resolve-SeedTargetDataStores as a 2-id array,
            # New-SeedLoaderCredentials minted one credential authorized for both, and the orchestrator's
            # non-SchoolYear branch issued a single bulk-load pass against the unqualified base URL.
            # DMS cannot pick between two authorized instances without a route qualifier in the URL.
            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }

            $thrown = $null
            try {
                Assert-SeedSelectionInputs -Manifest $manifest -DataStoreId @(1, 2)
            }
            catch {
                $thrown = $_.Exception.Message
            }
            $thrown | Should -Not -BeNullOrEmpty
            $thrown | Should -Match "Multiple -DataStoreId values"
            $thrown | Should -Match "1, 2"
        }

        It "requires SeedDataPath in ApiSchemaPath mode and rejects SeedTemplate" {
            $manifest = @{
                schema = @{ selectionMode = "ApiSchemaPath"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }

            # Rejects -SeedTemplate in ApiSchemaPath mode
            {
                Resolve-SeedSource `
                    -Manifest $manifest `
                    -SeedTemplate "Minimal" `
                    -BuiltInSourceRoot (New-TestDirectory) `
                    -CatalogPath $script:repo.SeedCatalogPath
            } | Should -Throw -ExpectedMessage "*does not support -SeedTemplate*"

            # Requires -SeedDataPath in ApiSchemaPath mode
            {
                Resolve-SeedSource `
                    -Manifest $manifest `
                    -BuiltInSourceRoot (New-TestDirectory) `
                    -CatalogPath $script:repo.SeedCatalogPath
            } | Should -Throw -ExpectedMessage "*requires -SeedDataPath*"

            # Succeeds with -SeedDataPath in ApiSchemaPath mode
            $dir = New-TestDirectory
            "<root/>" | Set-Content -LiteralPath (Join-Path $dir "Sample.xml") -Encoding utf8
            $result = Resolve-SeedSource `
                -Manifest $manifest `
                -SeedDataPath $dir `
                -BuiltInSourceRoot (New-TestDirectory) `
                -CatalogPath $script:repo.SeedCatalogPath

            $result.Kind | Should -Be "CustomPath"
            $result.SourceDirectory | Should -Be $dir
            Remove-Item -LiteralPath $dir -Recurse -Force
        }

        It "emits warning and uses custom path when SeedDataPath is supplied in Standard mode" {
            $dir = New-TestDirectory
            "<root/>" | Set-Content -LiteralPath (Join-Path $dir "Sample.xml") -Encoding utf8

            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }
            $result = Resolve-SeedSource `
                -Manifest $manifest `
                -SeedDataPath $dir `
                -BuiltInSourceRoot (New-TestDirectory) `
                -CatalogPath $script:repo.SeedCatalogPath `
                -WarningAction SilentlyContinue

            $result.Kind | Should -Be "CustomPath"
            $result.SourceDirectory | Should -Be $dir
            Remove-Item -LiteralPath $dir -Recurse -Force
        }

        It "extension not in catalog produces informational warning from Resolve-ExtensionSeedSources" {
            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @("SomeUnknownExtension") }
                seed   = @{ extensionNamespacePrefixes = @() }
            }
            $extResult = Resolve-ExtensionSeedSources `
                -Manifest $manifest `
                -CatalogPath $script:repo.SeedCatalogPath

            $extResult.Warnings.Count | Should -BeGreaterThan 0
            $extResult.Warnings[0] | Should -Match "No built-in seed package"
        }

        Context "Resolve-SeedSource -SeedDataPath validation" {
            It "throws when -SeedDataPath does not exist (Standard CustomPath mode)" {
                $missing = Join-Path ([System.IO.Path]::GetTempPath()) "dms-seed-missing-$([Guid]::NewGuid().ToString('N'))"
                $manifest = @{
                    schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                    seed   = @{ extensionNamespacePrefixes = @() }
                }
                {
                    Resolve-SeedSource `
                        -Manifest $manifest `
                        -SeedDataPath $missing `
                        -BuiltInSourceRoot (New-TestDirectory) `
                        -CatalogPath $script:repo.SeedCatalogPath
                } | Should -Throw -ExpectedMessage "*does not exist or is not a directory*"
            }

            It "throws when -SeedDataPath contains no *.xml files (Standard CustomPath mode)" {
                $emptyDir = New-TestDirectory
                "not xml" | Set-Content -LiteralPath (Join-Path $emptyDir "readme.txt") -Encoding utf8
                $manifest = @{
                    schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                    seed   = @{ extensionNamespacePrefixes = @() }
                }
                {
                    Resolve-SeedSource `
                        -Manifest $manifest `
                        -SeedDataPath $emptyDir `
                        -BuiltInSourceRoot (New-TestDirectory) `
                        -CatalogPath $script:repo.SeedCatalogPath
                } | Should -Throw -ExpectedMessage "*contains no *.xml files*"
                Remove-Item -LiteralPath $emptyDir -Recurse -Force
            }

            It "throws when -SeedDataPath is missing in ApiSchemaPath mode" {
                $missing = Join-Path ([System.IO.Path]::GetTempPath()) "dms-seed-missing-$([Guid]::NewGuid().ToString('N'))"
                $manifest = @{
                    schema = @{ selectionMode = "ApiSchemaPath"; selectedExtensions = @() }
                    seed   = @{ extensionNamespacePrefixes = @() }
                }
                {
                    Resolve-SeedSource `
                        -Manifest $manifest `
                        -SeedDataPath $missing `
                        -BuiltInSourceRoot (New-TestDirectory) `
                        -CatalogPath $script:repo.SeedCatalogPath
                } | Should -Throw -ExpectedMessage "*does not exist or is not a directory*"
            }

            It "accepts -SeedDataPath that exists and contains at least one *.xml (recursive)" {
                $dir = New-TestDirectory
                $sub = Join-Path $dir "nested"
                New-Item -ItemType Directory -Path $sub -Force | Out-Null
                "<root/>" | Set-Content -LiteralPath (Join-Path $sub "Sample.xml") -Encoding utf8
                $manifest = @{
                    schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                    seed   = @{ extensionNamespacePrefixes = @() }
                }
                $result = Resolve-SeedSource `
                    -Manifest $manifest `
                    -SeedDataPath $dir `
                    -BuiltInSourceRoot (New-TestDirectory) `
                    -CatalogPath $script:repo.SeedCatalogPath `
                    -WarningAction SilentlyContinue

                $result.Kind | Should -Be "CustomPath"
                $result.SourceDirectory | Should -Be $dir
                Remove-Item -LiteralPath $dir -Recurse -Force
            }
        }
    }

    Context "built-in seed asset presence" {
        It "Initialize-CoreSeedSource Minimal stages descriptors into a descriptors/ subdir and no resources/ subdir" {
            $fakeRepo = New-TestDirectory
            New-FakeDataStandardRoot -Root $fakeRepo -DescriptorNames @("ADescriptor", "BDescriptor", "CDescriptor") -IncludeSampleXml $false

            $destRoot = New-TestDirectory
            $result = Initialize-CoreSeedSource -Template "Minimal" -DataStandardRoot $fakeRepo -DestinationRoot $destRoot

            Test-Path -LiteralPath $result.TemplateDirectory -PathType Container | Should -BeTrue
            Test-Path -LiteralPath $result.DescriptorsDirectory -PathType Container | Should -BeTrue
            $result.ResourcesDirectory | Should -BeNullOrEmpty

            foreach ($name in @("ADescriptor.xml", "BDescriptor.xml", "CDescriptor.xml")) {
                Test-Path -LiteralPath (Join-Path $result.DescriptorsDirectory $name) | Should -BeTrue -Because "Minimal must stage $name into descriptors/"
            }

            # SchoolYearTypes are loaded via REST precondition, not as a staged XML file.
            Test-Path -LiteralPath (Join-Path $result.DescriptorsDirectory "SchoolYearTypes.xml") | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $result.TemplateDirectory "SchoolYearTypes.xml") | Should -BeFalse

            # Minimal must not produce a resources/ subdir.
            Test-Path -LiteralPath (Join-Path $result.TemplateDirectory "resources") | Should -BeFalse

            Remove-Item -LiteralPath $fakeRepo -Recurse -Force
            Remove-Item -LiteralPath $destRoot -Recurse -Force
        }

        It "Initialize-CoreSeedSource Populated stages standard descriptors plus sample-side descriptors into descriptors/, and only non-descriptor sample XMLs into resources/" {
            $fakeRepo = New-TestDirectory
            # Two standard descriptors (mirror v5.2.0's Descriptors/) plus two sample-only descriptors
            # (mirror the 8 *Descriptor.xml files that ship inside Samples/Sample XML/ in v5.2.0).
            New-FakeDataStandardRoot `
                -Root $fakeRepo `
                -DescriptorNames @("ADescriptor", "BDescriptor") `
                -SampleResourceNames @("EducationOrganization.xml", "Student.xml", "StudentEnrollment.xml") `
                -SampleDescriptorNames @("AncestryEthnicOriginDescriptor", "BusRouteDescriptor")

            $destRoot = New-TestDirectory
            $result = Initialize-CoreSeedSource -Template "Populated" -DataStandardRoot $fakeRepo -DestinationRoot $destRoot

            Test-Path -LiteralPath $result.DescriptorsDirectory -PathType Container | Should -BeTrue
            Test-Path -LiteralPath $result.ResourcesDirectory -PathType Container | Should -BeTrue

            # Standard descriptors land in descriptors/
            foreach ($name in @("ADescriptor.xml", "BDescriptor.xml")) {
                Test-Path -LiteralPath (Join-Path $result.DescriptorsDirectory $name) | Should -BeTrue -Because "Populated must include $name in descriptors/ from Descriptors/"
            }

            # Sample-side descriptors are MOVED to descriptors/ (must not stay in resources/)
            foreach ($name in @("AncestryEthnicOriginDescriptor.xml", "BusRouteDescriptor.xml")) {
                Test-Path -LiteralPath (Join-Path $result.DescriptorsDirectory $name) | Should -BeTrue -Because "Populated must route sample-side $name into descriptors/"
                Test-Path -LiteralPath (Join-Path $result.ResourcesDirectory $name) | Should -BeFalse -Because "Populated must NOT duplicate sample-side $name in resources/"
            }

            # Non-descriptor sample XMLs land in resources/
            foreach ($name in @("EducationOrganization.xml", "Student.xml", "StudentEnrollment.xml")) {
                Test-Path -LiteralPath (Join-Path $result.ResourcesDirectory $name) | Should -BeTrue -Because "Populated must include $name in resources/ from Samples/Sample XML/"
            }

            # SchoolYearTypes is REST-only; never appears as an XML file in any tier.
            Test-Path -LiteralPath (Join-Path $result.DescriptorsDirectory "SchoolYearTypes.xml") | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $result.ResourcesDirectory "SchoolYearTypes.xml") | Should -BeFalse

            Remove-Item -LiteralPath $fakeRepo -Recurse -Force
            Remove-Item -LiteralPath $destRoot -Recurse -Force
        }

        It "preflights representative built-in Minimal and Populated inventories before runtime" {
            $fakeRepo = New-TestDirectory
            $destRoot = New-TestDirectory
            try {
                New-FakeDataStandardRoot `
                    -Root $fakeRepo `
                    -IncludeBulkXsds $true `
                    -DescriptorNames @("AcademicHonorCategoryDescriptor", "BusRouteDescriptor") `
                    -SampleResourceNames @("EducationOrganization.xml", "Student.xml", "StudentEnrollment.xml") `
                    -SampleDescriptorNames @("AncestryEthnicOriginDescriptor") `
                    -BulkXsdNames @(
                        "Interchange-Descriptors",
                        "Interchange-EducationOrganization",
                        "Interchange-Student",
                        "Interchange-StudentEnrollment"
                    )

                $interchangeNames = Get-BulkLoadClientInterchangeNames -XsdDirectory (Join-Path $fakeRepo "Schemas/Bulk")

                $minimal = Initialize-CoreSeedSource -Template "Minimal" -DataStandardRoot $fakeRepo -DestinationRoot $destRoot
                {
                    Assert-SeedWorkspacePathsAreDiscoverable `
                        -SourceDirectories @($minimal.DescriptorsDirectory) `
                        -InterchangeNames $interchangeNames
                } | Should -Not -Throw

                $populated = Initialize-CoreSeedSource -Template "Populated" -DataStandardRoot $fakeRepo -DestinationRoot $destRoot
                {
                    Assert-SeedWorkspacePathsAreDiscoverable `
                        -SourceDirectories @($populated.DescriptorsDirectory) `
                        -InterchangeNames $interchangeNames
                } | Should -Not -Throw
                {
                    Assert-SeedWorkspacePathsAreDiscoverable `
                        -SourceDirectories @($populated.ResourcesDirectory) `
                        -InterchangeNames $interchangeNames
                } | Should -Not -Throw
            }
            finally {
                Remove-Item -LiteralPath $fakeRepo -Recurse -Force
                Remove-Item -LiteralPath $destRoot -Recurse -Force
            }
        }

        It "Initialize-CoreSeedSource Populated lets a sample-side descriptor overwrite a same-named file from Descriptors/" {
            # Mirrors the v5.2.0 collision case (DiagnosisDescriptor.xml exists in both Descriptors/
            # and Samples/Sample XML/). The sample-side payload must win because it carries the richer
            # Populated-tier descriptor values.
            $fakeRepo = New-TestDirectory
            New-FakeDataStandardRoot `
                -Root $fakeRepo `
                -DescriptorNames @("DiagnosisDescriptor", "ADescriptor") `
                -SampleResourceNames @("Resource.xml") `
                -SampleDescriptorNames @("DiagnosisDescriptor")

            # Tag the two source files so we can prove which one was kept.
            Set-Content -LiteralPath (Join-Path $fakeRepo "Descriptors/DiagnosisDescriptor.xml") `
                -Value "<root>from-Descriptors</root>" -Encoding utf8
            Set-Content -LiteralPath (Join-Path $fakeRepo "Samples/Sample XML/DiagnosisDescriptor.xml") `
                -Value "<root>from-SampleXML</root>" -Encoding utf8

            $destRoot = New-TestDirectory
            $result = Initialize-CoreSeedSource -Template "Populated" -DataStandardRoot $fakeRepo -DestinationRoot $destRoot

            $stagedFile = Join-Path $result.DescriptorsDirectory "DiagnosisDescriptor.xml"
            $stagedFile | Should -Exist
            (Get-Content -LiteralPath $stagedFile -Raw).Trim() | Should -Be "<root>from-SampleXML</root>" `
                -Because "sample-side descriptor must win on name collision"

            # And the file must not also live in resources/
            (Join-Path $result.ResourcesDirectory "DiagnosisDescriptor.xml") | Should -Not -Exist

            # Descriptors-tier file count is the union minus the duplicate: 2 unique (DiagnosisDescriptor,
            # ADescriptor) - the sample-side DiagnosisDescriptor overwrote, not appended.
            $stagedDescriptors = @(Get-ChildItem -LiteralPath $result.DescriptorsDirectory -File -Filter "*.xml")
            $stagedDescriptors.Count | Should -Be 2

            Remove-Item -LiteralPath $fakeRepo -Recurse -Force
            Remove-Item -LiteralPath $destRoot -Recurse -Force
        }

        It "Initialize-CoreSeedSource throws when Data Standard Descriptors directory is missing or empty" {
            # Missing Descriptors/
            $fakeRepoNoDescriptors = New-TestDirectory
            $destRoot = New-TestDirectory

            {
                Initialize-CoreSeedSource -Template "Minimal" -DataStandardRoot $fakeRepoNoDescriptors -DestinationRoot $destRoot
            } | Should -Throw -ExpectedMessage "*Data Standard 'Descriptors' directory not found*"

            # Empty Descriptors/
            $fakeRepoEmpty = New-TestDirectory
            New-Item -ItemType Directory -Path (Join-Path $fakeRepoEmpty "Descriptors") -Force | Out-Null

            {
                Initialize-CoreSeedSource -Template "Minimal" -DataStandardRoot $fakeRepoEmpty -DestinationRoot $destRoot
            } | Should -Throw -ExpectedMessage "*no XML files*"

            Remove-Item -LiteralPath $fakeRepoNoDescriptors -Recurse -Force
            Remove-Item -LiteralPath $fakeRepoEmpty -Recurse -Force
            Remove-Item -LiteralPath $destRoot -Recurse -Force
        }

        It "Resolve-SeedSource throws when BuiltInSourceRoot template directory is missing after materialization" {
            $fakeRepo = New-TestDirectory
            New-FakeDataStandardRoot -Root $fakeRepo -DescriptorNames @("ADescriptor") -IncludeSampleXml $false

            $destRoot = New-TestDirectory

            Initialize-CoreSeedSource -Template "Minimal" -DataStandardRoot $fakeRepo -DestinationRoot $destRoot | Out-Null

            # Remove the materialized directory to simulate a missing source after the fact
            $materializedDir = Join-Path $destRoot "minimal"
            Remove-Item -LiteralPath $materializedDir -Recurse -Force

            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }
            {
                Resolve-SeedSource `
                    -Manifest $manifest `
                    -SeedTemplate "Minimal" `
                    -BuiltInSourceRoot $destRoot `
                    -CatalogPath $script:repo.SeedCatalogPath
            } | Should -Throw -ExpectedMessage "*Built-in seed source directory*missing*"

            Remove-Item -LiteralPath $fakeRepo -Recurse -Force
            Remove-Item -LiteralPath $destRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Ed-Fi Data Standard repo fetch" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/data-standard.psm1" -Force
        }

        # Builds a synthetic GitHub-style source tarball at $TargetZipPath, with a single top-level
        # directory matching the GitHub naming convention ("Ed-Fi-Data-Standard-<ref-without-v>").
        function script:New-FakeDataStandardZip {
            param(
                [Parameter(Mandatory)] [string]$TargetZipPath,
                [string]$InnerDirName = "Ed-Fi-Data-Standard-5.2.0",
                [hashtable]$Contents = @{ "README.md" = "# Ed-Fi Data Standard" }
            )

            $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "fake-ds-stage-$([Guid]::NewGuid().ToString('N'))"
            $innerDir = Join-Path $stagingRoot $InnerDirName
            New-Item -ItemType Directory -Path $innerDir -Force | Out-Null

            foreach ($relPath in $Contents.Keys) {
                $abs = Join-Path $innerDir $relPath
                $absParent = Split-Path -Parent $abs
                if (-not (Test-Path -LiteralPath $absParent)) {
                    New-Item -ItemType Directory -Path $absParent -Force | Out-Null
                }
                Set-Content -LiteralPath $abs -Value $Contents[$relPath] -Encoding utf8
            }

            Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $TargetZipPath -Force
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }

        It "downloads, extracts, and renames the inner GitHub directory to the stable cache path" {
            $cacheRoot = New-TestDirectory
            $invoker = { param($url, $dest) New-FakeDataStandardZip -TargetZipPath $dest }

            $result = Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker

            $result | Should -Be (Join-Path $cacheRoot "v5.2.0")
            (Join-Path $result ".fetched-ok") | Should -Exist
            (Join-Path $result "README.md")   | Should -Exist
            # The inner GitHub directory name must NOT appear under the cache (it was renamed).
            (Join-Path $cacheRoot "Ed-Fi-Data-Standard-5.2.0") | Should -Not -Exist

            Remove-Item -LiteralPath $cacheRoot -Recurse -Force
        }

        It "short-circuits on subsequent calls when the marker file is present (no refetch)" {
            $cacheRoot = New-TestDirectory
            $invocationCount = 0
            $invoker = {
                param($url, $dest)
                $script:invocationCount++
                New-FakeDataStandardZip -TargetZipPath $dest
            }

            Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker | Out-Null
            Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker | Out-Null
            Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker | Out-Null

            $script:invocationCount | Should -Be 1 -Because "marker file must short-circuit subsequent fetches"

            Remove-Item -LiteralPath $cacheRoot -Recurse -Force
        }

        It "wipes a partial target dir (marker missing) and re-fetches" {
            $cacheRoot = New-TestDirectory
            $targetDir = Join-Path $cacheRoot "v5.2.0"
            # Pre-create a partial extract: dir exists, marker missing, with stale content that must be wiped.
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $targetDir "STALE.txt") -Value "should-be-wiped" -Encoding utf8

            $invoker = { param($url, $dest) New-FakeDataStandardZip -TargetZipPath $dest }
            $result = Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker

            (Join-Path $result "STALE.txt")    | Should -Not -Exist
            (Join-Path $result ".fetched-ok")  | Should -Exist
            (Join-Path $result "README.md")    | Should -Exist

            Remove-Item -LiteralPath $cacheRoot -Recurse -Force
        }

        It "throws when the tarball does not contain exactly one inner directory" {
            $cacheRoot = New-TestDirectory
            # Invoker that produces a zip with two top-level dirs instead of the canonical one.
            # Each dir gets a placeholder file so Compress-Archive actually materializes the zip.
            $invoker = {
                param($url, $dest)
                $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "bad-zip-$([Guid]::NewGuid().ToString('N'))"
                $firstDir  = Join-Path $stagingRoot "FirstDir"
                $secondDir = Join-Path $stagingRoot "SecondDir"
                New-Item -ItemType Directory -Path $firstDir -Force | Out-Null
                New-Item -ItemType Directory -Path $secondDir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $firstDir "placeholder.txt") -Value "a" -Encoding utf8
                Set-Content -LiteralPath (Join-Path $secondDir "placeholder.txt") -Value "b" -Encoding utf8
                Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $dest -Force
                Remove-Item -LiteralPath $stagingRoot -Recurse -Force
            }

            { Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker } |
                Should -Throw -ExpectedMessage "*expected exactly one inner directory*found 2*"

            Remove-Item -LiteralPath $cacheRoot -Recurse -Force
        }

        It "forwards the GitHub tag URL to the FetchInvoker" {
            $cacheRoot = New-TestDirectory
            $script:capturedUrl = $null
            $invoker = {
                param($url, $dest)
                $script:capturedUrl = $url
                New-FakeDataStandardZip -TargetZipPath $dest
            }

            Get-DataStandardRepo -RefTag "v5.2.0" -CacheRoot $cacheRoot -FetchInvoker $invoker | Out-Null

            $script:capturedUrl | Should -Be "https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Data-Standard/archive/refs/tags/v5.2.0.zip"

            Remove-Item -LiteralPath $cacheRoot -Recurse -Force
        }

        It "Resolve-BootstrapDataStandard wraps fetch failures with the pinned tag" {
            $bootstrapRoot = New-TestDirectory
            function script:Get-BootstrapRoot { $bootstrapRoot }
            function script:Get-DataStandardRepo { throw "simulated fetch failure" }

            try {
                { Resolve-BootstrapDataStandard } |
                    Should -Throw -ExpectedMessage "*Data Standard repo resolution failed for tag v5.2.0*simulated fetch failure*"
            }
            finally {
                Remove-Item function:script:Get-BootstrapRoot -ErrorAction SilentlyContinue
                Remove-Item function:script:Get-DataStandardRepo -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $bootstrapRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "Resolve-BootstrapDataStandard rejects a missing extracted directory with the resolved path" {
            $bootstrapRoot = New-TestDirectory
            $missingRepoRoot = Join-Path $bootstrapRoot "data-standard/v5.2.0"
            function script:Get-BootstrapRoot { $bootstrapRoot }
            function script:Get-DataStandardRepo { $missingRepoRoot }

            try {
                { Resolve-BootstrapDataStandard } |
                    Should -Throw -ExpectedMessage "*Data Standard repo directory not found after fetch*$missingRepoRoot*"
            }
            finally {
                Remove-Item function:script:Get-BootstrapRoot -ErrorAction SilentlyContinue
                Remove-Item function:script:Get-DataStandardRepo -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $bootstrapRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "Data Standard ref tag resolution" {
        It "maps Data Standard 5.2 to the v5.2.0 seed ref tag" {
            $envValues = @{ DMS_CONFIG_DATA_STANDARD_VERSION = "5.2" }

            Resolve-SeedDataStandardRefTag -EnvValues $envValues | Should -Be "v5.2.0"
        }

        It "maps Data Standard 6.1 to the v6.1.0 seed ref tag" {
            $envValues = @{ DMS_CONFIG_DATA_STANDARD_VERSION = "6.1" }

            Resolve-SeedDataStandardRefTag -EnvValues $envValues | Should -Be "v6.1.0"
        }

        It "falls back to v5.2.0 when the Data Standard version is absent or unrecognized" {
            Resolve-SeedDataStandardRefTag -EnvValues @{} | Should -Be "v5.2.0"
            Resolve-SeedDataStandardRefTag -EnvValues @{ DMS_CONFIG_DATA_STANDARD_VERSION = "9.9" } | Should -Be "v5.2.0"
        }
    }

    Context "seed workspace materialization" {
        It "derives known interchange names from Bulk XSD filenames" {
            $xsdDir = New-TestDirectory
            "<schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "Interchange-EducationOrganization.xsd") -Encoding utf8
            "<schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "Interchange-StudentAssessment.xsd") -Encoding utf8
            "<schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "TPDM-EXTENSION-Interchange-Candidate-Extension.xsd") -Encoding utf8
            "<schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "EXTENSION-Interchange-Example.xsd") -Encoding utf8
            "<schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "EducationOrganization.xsd") -Encoding utf8

            $names = Get-BulkLoadClientInterchangeNames -XsdDirectory $xsdDir

            $names | Should -Contain "EducationOrganization"
            $names | Should -Contain "StudentAssessment"
            $names | Should -Contain "Candidate"
            $names | Should -Contain "Example"
            $names | Should -Not -Contain "Interchange-EducationOrganization"
            $names | Should -Not -Contain "TPDM-EXTENSION-Interchange-Candidate-Extension"

            Remove-Item -LiteralPath $xsdDir -Recurse -Force
        }

        It "uses extension XSD interchange names when staging extension seed XML" {
            $xsdDir = New-TestDirectory
            $sourceDir = New-TestDirectory
            "<schema />" | Set-Content -LiteralPath (Join-Path $xsdDir "TPDM-EXTENSION-Interchange-Candidate-Extension.xsd") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "Candidate.xml") -Encoding utf8

            $names = Get-BulkLoadClientInterchangeNames -XsdDirectory $xsdDir
            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $names

            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "Candidate.xml") | Should -BeTrue

            Remove-Item -LiteralPath $xsdDir -Recurse -Force
            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "preserves BulkLoadClient-compatible interchange file names and folders" {
            $sourceDir = New-TestDirectory
            $interchangeDir = Join-Path $sourceDir "EducationOrganization"
            New-Item -ItemType Directory -Path $interchangeDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "EducationOrganization.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "StudentAssessment-Benchmarks.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $interchangeDir "part1.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "EducationOrganization.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "StudentAssessment-Benchmarks.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "EducationOrganization/part1.xml") | Should -BeTrue
            @($workspace.StagedFiles | Where-Object { (Split-Path -Leaf $_) -eq "EducationOrganization.xml" }).Count | Should -Be 1
            @($workspace.StagedFiles | Where-Object { (Split-Path -Leaf $_) -match ".+__[0-9a-f]{8}__.+" }).Count | Should -Be 0

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "normalizes wrapper directories to BulkLoadClient-discoverable target paths" {
            $sourceDir = New-TestDirectory
            $resourcesDir = Join-Path $sourceDir "resources"
            $interchangeDir = Join-Path $resourcesDir "EducationOrganization"
            New-Item -ItemType Directory -Path $interchangeDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $resourcesDir "EducationOrganization.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $resourcesDir "StudentAssessment-Benchmarks.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $interchangeDir "part1.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "EducationOrganization.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "StudentAssessment-Benchmarks.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "EducationOrganization/part1.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "resources/EducationOrganization.xml") | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "resources/EducationOrganization/part1.xml") | Should -BeFalse

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "infers target interchange from XML declarations when sample filenames are not interchange-prefixed" {
            $sourceDir = New-TestDirectory
            '<?xml version="1.0" encoding="UTF-8"?><InterchangeAssessmentMetadata xmlns="http://ed-fi.org/5.2.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://ed-fi.org/5.2.0 ../../Schemas/Bulk/Interchange-AssessmentMetadata.xsd" />' |
                Set-Content -LiteralPath (Join-Path $sourceDir "AssessmentSample.xml") -Encoding utf8
            '<?xml version="1.0" encoding="UTF-8"?><InterchangeStudentAttendance xmlns="http://ed-fi.org/5.2.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://ed-fi.org/5.2.0 ../../Schemas/Bulk/Interchange-StudentAttendance.xsd" />' |
                Set-Content -LiteralPath (Join-Path $sourceDir "StudentSchoolAttendance.xml") -Encoding utf8
            '<?xml version="1.0" encoding="UTF-8"?><InterchangeStudentEnrollment xmlns="http://ed-fi.org/5.2.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://ed-fi.org/5.2.0 ../../Schemas/Bulk/Interchange-StudentEnrollment.xsd" />' |
                Set-Content -LiteralPath (Join-Path $sourceDir "StudentTransportation.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "AssessmentMetadata/AssessmentSample.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "StudentAttendance/StudentSchoolAttendance.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "StudentEnrollment/StudentTransportation.xml") | Should -BeTrue

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "infers target interchange from schemaLocation when the XML root is not an interchange element" {
            $sourceDir = New-TestDirectory
            '<?xml version="1.0" encoding="UTF-8"?><Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://ed-fi.org/5.2.0 ../../Schemas/Bulk/Interchange-StudentAttendance.xsd" />' |
                Set-Content -LiteralPath (Join-Path $sourceDir "AttendanceSeed.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "StudentAttendance/AttendanceSeed.xml") | Should -BeTrue

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "stages files from an interchange-named source directory under that interchange folder" {
            $rootDir = New-TestDirectory
            $sourceDir = Join-Path $rootDir "descriptors"
            New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "AcademicHonorCategoryDescriptor.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "BusRouteDescriptor.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "Descriptors/AcademicHonorCategoryDescriptor.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "Descriptors/BusRouteDescriptor.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "AcademicHonorCategoryDescriptor.xml") | Should -BeFalse

            Remove-Item -LiteralPath $rootDir -Recurse -Force
        }

        It "rejects nested XML paths that cannot be mapped to a known interchange" {
            $sourceDir = New-TestDirectory
            $resourcesDir = Join-Path $sourceDir "resources"
            New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $resourcesDir "part1.xml") -Encoding utf8

            {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($sourceDir) `
                    -InterchangeNames $script:seedWorkspaceInterchangeNames
            } | Should -Throw -ExpectedMessage "*cannot discover*resources/part1.xml*"

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "preflights undiscoverable paths without touching the seed workspace" {
            $sourceDir = New-TestDirectory
            $resourcesDir = Join-Path $sourceDir "resources"
            New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $resourcesDir "part1.xml") -Encoding utf8

            {
                Assert-SeedWorkspacePathsAreDiscoverable `
                    -SourceDirectories @($sourceDir) `
                    -InterchangeNames $script:seedWorkspaceInterchangeNames
            } | Should -Throw -ExpectedMessage "*cannot discover*resources/part1.xml*"

            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "seed") | Should -BeFalse

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "ignores directories whose names end with .xml during custom seed preflight and staging" {
            $sourceDir = New-TestDirectory
            New-Item -ItemType Directory -Path (Join-Path $sourceDir "Fake.xml") -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "Student.xml") -Encoding utf8

            { Assert-SeedDataPathHasXml -SeedDataPath $sourceDir } | Should -Not -Throw

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            $workspace.StagedFiles.Count | Should -Be 1
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "Student.xml") | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $workspace.DataDirectory "Fake.xml") | Should -BeFalse

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "rejects a custom seed path that contains only directories named like XML files" {
            $sourceDir = New-TestDirectory
            New-Item -ItemType Directory -Path (Join-Path $sourceDir "Fake.xml") -Force | Out-Null

            { Assert-SeedDataPathHasXml -SeedDataPath $sourceDir } | Should -Throw -ExpectedMessage "*contains no loadable *.xml files*"

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "excludes only ODS _rels path segments from custom seed staging" {
            $sourceDir = New-TestDirectory
            $relsDir = Join-Path $sourceDir "_rels"
            $districtRelsDir = Join-Path $sourceDir "district_rels"
            New-Item -ItemType Directory -Path $relsDir -Force | Out-Null
            New-Item -ItemType Directory -Path $districtRelsDir -Force | Out-Null

            "<root />" | Set-Content -LiteralPath (Join-Path $relsDir "metadata.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $districtRelsDir "Student.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "Course.xml") -Encoding utf8

            { Assert-SeedDataPathHasXml -SeedDataPath $sourceDir } | Should -Not -Throw

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            $stagedNames = @($workspace.StagedFiles | ForEach-Object { Split-Path -Leaf $_ })
            $stagedNames.Count | Should -Be 2
            @($stagedNames | Where-Object { $_ -match "Course\.xml" }).Count | Should -Be 1
            @($stagedNames | Where-Object { $_ -match "Student\.xml" }).Count | Should -Be 1
            @($stagedNames | Where-Object { $_ -match "metadata\.xml" }).Count | Should -Be 0

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "keeps package-backed seed source outside the disposable BulkLoadClient workspace" {
            $tmpRoot = New-TestDirectory
            $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
            $sourceDir = Join-Path $bootstrapRoot "seed-source/minimal"
            New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "Student.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $bootstrapRoot `
                -SourceDirectories @($sourceDir) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames

            $workspace.StagedFiles.Count | Should -Be 1
            Test-Path -LiteralPath $sourceDir -PathType Container | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $sourceDir "Student.xml") | Should -BeTrue

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "fails on real target name collisions instead of prefixing every file" {
            $srcA = New-TestDirectory
            $srcB = New-TestDirectory
            "<root />" | Set-Content -LiteralPath (Join-Path $srcA "EducationOrganization.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $srcB "EducationOrganization.xml") -Encoding utf8

            {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($srcA, $srcB) `
                    -InterchangeNames $script:seedWorkspaceInterchangeNames
            } | Should -Throw -ExpectedMessage "*collision*EducationOrganization.xml*"

            Remove-Item -LiteralPath $srcA -Recurse -Force
            Remove-Item -LiteralPath $srcB -Recurse -Force
        }

        It "fails before BulkLoadClient when XML declaration-inferred target paths collide" {
            $srcA = New-TestDirectory
            $srcB = New-TestDirectory
            '<?xml version="1.0" encoding="UTF-8"?><InterchangeAssessmentMetadata xmlns="http://ed-fi.org/5.2.0" />' |
                Set-Content -LiteralPath (Join-Path $srcA "AssessmentSample.xml") -Encoding utf8
            '<?xml version="1.0" encoding="UTF-8"?><InterchangeAssessmentMetadata xmlns="http://ed-fi.org/5.2.0" />' |
                Set-Content -LiteralPath (Join-Path $srcB "AssessmentSample.xml") -Encoding utf8

            {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($srcA, $srcB) `
                    -InterchangeNames $script:seedWorkspaceInterchangeNames
            } | Should -Throw -ExpectedMessage "*collision*AssessmentMetadata*AssessmentSample.xml*"

            Remove-Item -LiteralPath $srcA -Recurse -Force
            Remove-Item -LiteralPath $srcB -Recurse -Force
        }

        It "fails before BulkLoadClient when relative target paths would collide" {
            $srcA = New-TestDirectory
            "<root />" | Set-Content -LiteralPath (Join-Path $srcA "Descriptor.xml") -Encoding utf8

            {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($srcA, $srcA) `
                    -InterchangeNames $script:seedWorkspaceInterchangeNames
            } | Should -Throw -ExpectedMessage "*collision*"

            $seedDataDir = Join-Path $script:repo.BootstrapRoot "seed/data"
            if (Test-Path -LiteralPath $seedDataDir) {
                $stagedFiles = @(Get-ChildItem -LiteralPath $seedDataDir -File)
                $stagedFiles.Count | Should -Be 0
            }

            Remove-Item -LiteralPath $srcA -Recurse -Force
        }

        It "removes seed workspace on success and retains on failure" {
            $srcA = New-TestDirectory
            "<root />" | Set-Content -LiteralPath (Join-Path $srcA "Student.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($srcA) `
                -InterchangeNames $script:seedWorkspaceInterchangeNames
            Test-Path -LiteralPath $workspace.DataDirectory | Should -BeTrue

            $seedRoot = Join-Path $script:repo.BootstrapRoot "seed"
            Remove-Item -LiteralPath $seedRoot -Recurse -Force
            Test-Path -LiteralPath $workspace.DataDirectory | Should -BeFalse

            try {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($srcA, $srcA) `
                    -InterchangeNames $script:seedWorkspaceInterchangeNames
            }
            catch {
                # Expected; collision was detected.
                $null = $_
            }

            $failedSeedRoot = Join-Path $script:repo.BootstrapRoot "seed"
            if (Test-Path -LiteralPath $failedSeedRoot) {
                $dataDir = Join-Path $failedSeedRoot "data"
                if (Test-Path -LiteralPath $dataDir) {
                    @(Get-ChildItem -LiteralPath $dataDir -File).Count | Should -Be 0
                }
            }

            Remove-Item -LiteralPath $srcA -Recurse -Force
        }
    }

    Context "Invoke-Api / Get-HttpErrorResponse HTTP error surfacing" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/../Dms-Management.psm1" -Force
        }

        It "Get-HttpErrorResponse returns ErrorDetails.Message when populated" {
            # Regression: when Invoke-Api captures the HTTP body before the response stream
            # is disposed, it stashes the body on ErrorRecord.ErrorDetails. Get-HttpErrorResponse
            # must prefer that captured body over the (now-disposed) Response.Content.
            $exception = [System.Exception]::new("fake")
            $errorRecord = [System.Management.Automation.ErrorRecord]::new(
                $exception, "FakeError", [System.Management.Automation.ErrorCategory]::NotSpecified, $null
            )
            $errorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new(
                '{"errors":["something went wrong"]}'
            )

            $result = Get-HttpErrorResponse -ErrorRecord $errorRecord

            $result.Body | Should -Be '{"errors":["something went wrong"]}'
        }

        It "Get-HttpErrorResponse falls back gracefully when ErrorDetails is empty and Response is unavailable" {
            # When the ErrorRecord has neither ErrorDetails nor an HttpResponseMessage
            # (e.g. network failure, DNS failure), Get-HttpErrorResponse should return
            # a null status and empty body instead of throwing.
            $exception = [System.Exception]::new("network down")
            $errorRecord = [System.Management.Automation.ErrorRecord]::new(
                $exception, "NetworkError", [System.Management.Automation.ErrorCategory]::ConnectionError, $null
            )

            $result = Get-HttpErrorResponse -ErrorRecord $errorRecord

            $result.StatusCode | Should -BeNullOrEmpty
            $result.Body | Should -BeNullOrEmpty
        }
    }

    Context "SeedLoader credential and namespace-prefix logic" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/../Dms-Management.psm1" -Force
            Import-Module "$script:sourceDockerComposeRoot/env-utility.psm1" -Force
        }

        It "Assert-CmsSeedLoaderClaimSetLoaded throws when CMS does not surface the SeedLoader claim set" {
            # Regression: Add-Application stores ClaimSetName as a string, so a stale CMS image
            # without the embedded SeedLoader claim set would accept credential creation and surface
            # confusing 401/403 noise from BulkLoadClient later. The preflight must fail fast here.
            $invokerWithoutSeedLoader = {
                @(
                    [pscustomobject]@{ id = 1; claimSetName = "EdFiSandbox" },
                    [pscustomobject]@{ id = 2; claimSetName = "BootstrapDescriptorsandEdOrgs" }
                )
            }
            {
                Assert-CmsSeedLoaderClaimSetLoaded `
                    -CmsUrl "http://unused" `
                    -AccessToken "unused" `
                    -ApiInvoker $invokerWithoutSeedLoader
            } | Should -Throw -ExpectedMessage "*SeedLoader*claim set*"

            $invokerWithSeedLoader = {
                @(
                    [pscustomobject]@{ id = 1; claimSetName = "EdFiSandbox" },
                    [pscustomobject]@{ id = 7; claimSetName = "SeedLoader" }
                )
            }
            {
                Assert-CmsSeedLoaderClaimSetLoaded `
                    -CmsUrl "http://unused" `
                    -AccessToken "unused" `
                    -ApiInvoker $invokerWithSeedLoader
            } | Should -Not -Throw
        }

        It "builds namespace prefix list from baseline, manifest, and additional prefixes with deduplication" {
            $result = Get-SeedLoaderNamespacePrefixes `
                -ExtensionPrefixes @("uri://extension.org") `
                -AdditionalPrefixes @("uri://ed-fi.org", "uri://extra.org")

            $result[0] | Should -Be "uri://ed-fi.org"
            $result[1] | Should -Be "uri://gbisd.edu"
            $result | Should -Contain "uri://extension.org"
            $result | Should -Contain "uri://extra.org"
            @($result | Where-Object { $_ -eq "uri://ed-fi.org" }).Count | Should -Be 1
        }

        It "rejects AdditionalNamespacePrefix entries that are malformed or missing uri:// prefix" {
            { Get-SeedLoaderNamespacePrefixes -AdditionalPrefixes @("") } | Should -Throw -ExpectedMessage "*null or whitespace*"
            { Get-SeedLoaderNamespacePrefixes -AdditionalPrefixes @("http://example.org") } | Should -Throw -ExpectedMessage "*uri://*"
        }

        It "creates SeedLoader credentials distinct from smoke-test credentials and not persisted to disk" {
            # Static content check: smoke-test helpers must not be wired into seed delivery.
            $scriptContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1") -Raw
            $scriptContent | Should -Not -Match "(?i)smoke"
            $scriptContent | Should -Not -Match "Add-SmokeTest"

            $mgmtContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "../Dms-Management.psm1") -Raw
            $mgmtContent | Should -Match 'ClaimSetName.*=.*"SeedLoader"'
        }

        It "Wait-CmsClientAvailable returns immediately when the first poll succeeds" {
            $calls = 0
            { Wait-CmsClientAvailable `
                -CmsUrl "http://localhost:8081" `
                -ClientId "k" `
                -ClientSecret "s" `
                -DelayMs 0 `
                -Invoker {
                    param($uri, $body, $attempt)
                    $script:calls = $attempt
                    $calls = $attempt
                    return 200
                } } | Should -Not -Throw
        }

        It "Wait-CmsClientAvailable retries on 401 and stops as soon as it gets a 200" {
            $script:invocations = [System.Collections.Generic.List[int]]::new()
            Wait-CmsClientAvailable `
                -CmsUrl "http://localhost:8081" `
                -ClientId "k" `
                -ClientSecret "s" `
                -MaxAttempts 10 `
                -DelayMs 0 `
                -Invoker {
                    param($uri, $body, $attempt)
                    $script:invocations.Add($attempt)
                    if ($attempt -lt 3) { return 401 }
                    return 200
                }

            $script:invocations.Count | Should -Be 3
            $script:invocations[0] | Should -Be 1
            $script:invocations[-1] | Should -Be 3
        }

        It "Wait-CmsClientAvailable throws after MaxAttempts when the client never becomes available" {
            $script:invocations = [System.Collections.Generic.List[int]]::new()
            {
                Wait-CmsClientAvailable `
                    -CmsUrl "http://localhost:8081" `
                    -ClientId "k" `
                    -ClientSecret "s" `
                    -MaxAttempts 4 `
                    -DelayMs 0 `
                    -Invoker {
                        param($uri, $body, $attempt)
                        $script:invocations.Add($attempt)
                        return 401
                    }
            } | Should -Throw -ExpectedMessage "*did not become available*"

            $script:invocations.Count | Should -Be 4
        }

        It "Wait-CmsClientAvailable hands the URI and form body to the Invoker" {
            $capturedUri = $null
            $capturedBody = $null
            Wait-CmsClientAvailable `
                -CmsUrl "http://localhost:8081/" `
                -ClientId "the-key" `
                -ClientSecret "the-secret" `
                -MaxAttempts 1 `
                -DelayMs 0 `
                -Invoker {
                    param($uri, $body, $attempt)
                    $script:capturedUri = $uri
                    $script:capturedBody = $body
                    return 200
                }

            $script:capturedUri | Should -Be "http://localhost:8081/connect/token"
            $script:capturedBody | Should -Be "client_id=the-key&client_secret=the-secret&grant_type=client_credentials"
        }

        It "Remove-CmsApplication builds the DELETE URI as <CmsUrl>/v3/applications/<id>" {
            Mock -ModuleName Dms-Management -CommandName Invoke-RestMethod -MockWith { return $null }

            Remove-CmsApplication `
                -CmsUrl "http://localhost:8081" `
                -ApplicationId 42 `
                -AccessToken "fake-token"

            Should -Invoke `
                -ModuleName Dms-Management `
                -CommandName Invoke-RestMethod `
                -ParameterFilter { $Uri -eq "http://localhost:8081/v3/applications/42" -and $Method -eq "Delete" } `
                -Times 1 `
                -Exactly
        }

        It "Remove-CmsApplication forwards Tenant header when supplied" {
            Mock -ModuleName Dms-Management -CommandName Invoke-RestMethod -MockWith { return $null }

            Remove-CmsApplication `
                -CmsUrl "http://localhost:8081" `
                -ApplicationId 7 `
                -AccessToken "fake-token" `
                -Tenant "edfi-tenant"

            Should -Invoke `
                -ModuleName Dms-Management `
                -CommandName Invoke-RestMethod `
                -ParameterFilter { $Headers["Tenant"] -eq "edfi-tenant" -and $Uri -eq "http://localhost:8081/v3/applications/7" } `
                -Times 1 `
                -Exactly
        }
    }

    Context "environment, URL, OAuth, selector, and XSD resolution" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/env-utility.psm1" -Force
            Import-Module "$script:sourceDockerComposeRoot/../Dms-Management.psm1" -Force
        }

        It "Write-DerivedEnvFile overrides existing keys without touching other lines" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
FAILURE_RATIO=0.01
SAMPLING_DURATION_SECONDS=10
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                Write-DerivedEnvFile `
                    -BaseEnvironmentFile $base `
                    -TargetPath $derived `
                    -KeyOverrides @{ FAILURE_RATIO = "0.95" }

                $content = Get-Content -LiteralPath $derived -Raw
                $content | Should -Match "(?m)^FAILURE_RATIO=0\.95$"
                $content | Should -Not -Match "FAILURE_RATIO=0\.01"
                $content | Should -Match "(?m)^LOG_LEVEL=DEBUG$"
                $content | Should -Match "(?m)^SAMPLING_DURATION_SECONDS=10$"
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "Write-DerivedEnvFile appends a key when it doesn't exist in the base" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            "LOG_LEVEL=DEBUG" | Set-Content -LiteralPath $base -Encoding utf8
            try {
                Write-DerivedEnvFile -BaseEnvironmentFile $base -TargetPath $derived -KeyOverrides @{ NEW_KEY = "abc" }
                $content = Get-Content -LiteralPath $derived -Raw
                $content | Should -Match "(?m)^NEW_KEY=abc$"
                $content | Should -Match "(?m)^LOG_LEVEL=DEBUG$"
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "Write-DerivedEnvFile is idempotent across reruns (same input to same output)" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
FAILURE_RATIO=0.01
SCHEMA_PACKAGES='[
  { "version": "1.0", "name": "EdFi.DataStandard52.ApiSchema" },
  { "version": "1.0", "name": "EdFi.DataStandard52.Sample.ApiSchema" }
]'
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                Write-DerivedEnvFile -BaseEnvironmentFile $base -TargetPath $derived `
                    -KeyOverrides @{ FAILURE_RATIO = "0.95" }
                $first = Get-Content -LiteralPath $derived -Raw

                Write-DerivedEnvFile -BaseEnvironmentFile $base -TargetPath $derived `
                    -KeyOverrides @{ FAILURE_RATIO = "0.95" }
                $second = Get-Content -LiteralPath $derived -Raw

                $first | Should -Be $second
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "Write-DerivedEnvFile leaves the base env file untouched" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            $originalContent = "LOG_LEVEL=DEBUG`nFAILURE_RATIO=0.01`n"
            Set-Content -LiteralPath $base -Value $originalContent -Encoding utf8 -NoNewline
            try {
                Write-DerivedEnvFile -BaseEnvironmentFile $base -TargetPath $derived -KeyOverrides @{ FAILURE_RATIO = "0.95" }
                $baseAfter = Get-Content -LiteralPath $base -Raw
                $baseAfter | Should -Be $originalContent
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "Resolve-BootstrapDerivedEnv retains Sample+Homograph in SCHEMA_PACKAGES" {
            # The derived env applies the seed profile (loose circuit breaker) without
            # narrowing the schema surface: built-in and custom seed runs alike must see
            # every package the base env serves.
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
FAILURE_RATIO=0.01
SCHEMA_PACKAGES='[
  { "version": "1.0", "name": "EdFi.DataStandard52.ApiSchema" },
  { "version": "1.0", "name": "EdFi.DataStandard52.Sample.ApiSchema" },
  { "version": "1.0", "name": "EdFi.DataStandard52.Homograph.ApiSchema" },
  { "version": "1.0", "name": "EdFi.DataStandard52.TPDM.ApiSchema" }
]'
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                $result = Resolve-BootstrapDerivedEnv -BaseEnvironmentFile $base -DerivedTargetPath $derived
                $result | Should -Be $derived

                $content = Get-Content -LiteralPath $derived -Raw
                $content | Should -Match "(?m)^FAILURE_RATIO=0\.95$" -Because "circuit-breaker override must still apply"
                $content | Should -Not -Match "FAILURE_RATIO=0\.01"
                $content | Should -Match "EdFi.DataStandard52.ApiSchema"
                $content | Should -Match "EdFi.DataStandard52.TPDM.ApiSchema"
                $content | Should -Match "EdFi.DataStandard52.Sample.ApiSchema" -Because "the full schema surface must stay active for seed runs"
                $content | Should -Match "EdFi.DataStandard52.Homograph.ApiSchema" -Because "the full schema surface must stay active for seed runs"
                $content | Should -Match "(?m)^LOG_LEVEL=DEBUG$"
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "bootstrap-wrapper.psm1 defines the derived-env shim and the entry scripts delegate to it" {
            # Original drift detector for the two wrappers. After the shared body moved to
            # bootstrap-wrapper.psm1, drift is eliminated by construction; this test now asserts
            # the single source of truth is in place and that both entry scripts import it.
            $modulePath = Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1"
            Test-Path -LiteralPath $modulePath | Should -BeTrue
            $moduleContent = Get-Content -LiteralPath $modulePath -Raw
            $moduleContent | Should -Match "function Get-EffectiveBootstrapEnvFile" -Because "the shared module must define the shim"
            $moduleContent | Should -Match "Resolve-BootstrapDerivedEnv" -Because "the shared module must call the canonical helper"

            foreach ($name in @("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                $entry = Join-Path $script:sourceDockerComposeRoot $name
                $content = Get-Content -LiteralPath $entry -Raw
                $content | Should -Match "Import-Module[^\n]+bootstrap-wrapper\.psm1" -Because "$name must import the shared wrapper module"
                $content | Should -Match "Invoke-BootstrapWrapper" -Because "$name must delegate to Invoke-BootstrapWrapper"
            }
        }

        It "load-dms-seed-data.ps1 gates SchoolYearType REST precondition on BuiltIn templates" {
            # Custom -SeedDataPath / ApiSchemaPath tiers bring their own SchoolYear lifecycle, so
            # the bootstrap script must not mutate /ed-fi/schoolYearTypes for them. A regression here
            # would silently POST 47 years' worth of rows against a developer's custom payload.
            $script = Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1"
            $content = Get-Content -LiteralPath $script -Raw
            $content | Should -Match '\$runSchoolYearTypePrecondition\s*=\s*\(\$seedSource\.Kind\s+-eq\s+"BuiltIn"\)' `
                -Because "the precondition must be gated by the seed source kind"
            $content | Should -Match 'if\s*\(\s*\$runSchoolYearTypePrecondition\s*\)' `
                -Because "the precondition call must be guarded by the gate variable"
        }

        It "load-dms-seed-data.ps1 defaults EnvironmentFile before reading it" {
            # Regression guard for direct seed invocation without -EnvironmentFile: the script must
            # resolve the shared default (.env, seeded once from .env.example when absent) before
            # ReadValuesFromEnvFile calls Test-Path.
            $script = Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1"
            $content = Get-Content -LiteralPath $script -Raw
            $defaultIndex = $content.IndexOf('# Resolve environment file')
            $readIndex = $content.IndexOf('$envValues = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile')

            $defaultIndex | Should -BeGreaterOrEqual 0
            $readIndex | Should -BeGreaterThan $defaultIndex
            $content.Substring($defaultIndex, $readIndex - $defaultIndex) |
                Should -Match '\$EnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path \$EnvironmentFile' `
                -Because "direct invocation must resolve through the shared local-settings resolver before calling ReadValuesFromEnvFile"
        }

        It "resolves env file and derives CMS URL, DMS URL, identity provider from local settings" {
            $envFile = Join-Path ([System.IO.Path]::GetTempPath()) "test-$([Guid]::NewGuid().ToString('N')).env"
            @"
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=9999
DMS_HTTP_PORTS=8888
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            try {
                $envValues = ReadValuesFromEnvFile -EnvironmentFile $envFile
                $cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
                $dmsUrl = Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues
                $idp = Resolve-IdentityProvider -EnvValues $envValues

                $cmsUrl | Should -Be "http://localhost:9999"
                $dmsUrl | Should -Be "http://localhost:8888"
                $idp | Should -Be "self-contained"
            }
            finally {
                Remove-Item -LiteralPath $envFile -Force -ErrorAction SilentlyContinue
            }
        }

        It "ReadValuesFromEnvFile preserves values containing equals signs" {
            $envFile = Join-Path ([System.IO.Path]::GetTempPath()) "test-$([Guid]::NewGuid().ToString('N')).env"
            @"
FEED_URL=https://example.test/feed?api-version=6.0
BASE64_SECRET=abc==
CONNECTION_STRING=Server=localhost;Password=a=b;TrustServerCertificate=true
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            try {
                $envValues = ReadValuesFromEnvFile -EnvironmentFile $envFile

                $envValues["FEED_URL"] | Should -Be "https://example.test/feed?api-version=6.0"
                $envValues["BASE64_SECRET"] | Should -Be "abc=="
                $envValues["CONNECTION_STRING"] | Should -Be "Server=localhost;Password=a=b;TrustServerCertificate=true"
            }
            finally {
                Remove-Item -LiteralPath $envFile -Force -ErrorAction SilentlyContinue
            }
        }

        It "builds host-side OAuth token URLs from port env-vars for self-contained and keycloak" {
            $envValues = @{
                DMS_CONFIG_ASPNETCORE_HTTP_PORTS = "8081"
                KEYCLOAK_PORT                    = "8045"
            }

            $url = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "self-contained"
            $url | Should -Be "http://localhost:8081/connect/token"

            $urlWithYear = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "self-contained" -SchoolYear 2024
            $urlWithYear | Should -Be "http://localhost:8081/connect/token/2024"

            $kcUrl = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "keycloak"
            $kcUrl | Should -Be "http://localhost:8045/realms/edfi/protocol/openid-connect/token"
        }

        It "Resolve-OAuthTokenUrl never returns a container DNS name" {
            # Regression: BulkLoadClient runs on the host, so OAuth URLs derived from container
            # service names (ed-fi-api-config, dms-keycloak) would be unreachable.
            $envValues = ReadValuesFromEnvFile -EnvironmentFile (Join-Path $script:sourceDockerComposeRoot ".env.example")

            $selfContained = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "self-contained"
            $selfContained | Should -Match '^http://localhost:\d+/'
            $selfContained | Should -Not -Match 'ed-fi-api-config'

            $keycloak = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "keycloak"
            $keycloak | Should -Match '^http://localhost:\d+/'
            $keycloak | Should -Not -Match 'dms-keycloak'
        }

        It "Resolve-DmsRouteUrl composes tenant + qualifier segments matching CoreEndpointModule.BuildRoutePattern" {
            $base = "http://localhost:8080"

            # No tenant, no qualifiers
            (Resolve-DmsRouteUrl -BaseUrl $base) | Should -Be $base

            # Tenant only
            (Resolve-DmsRouteUrl -BaseUrl $base -Tenant "Tenant1") | Should -Be "$base/Tenant1"

            # Qualifier only
            (Resolve-DmsRouteUrl -BaseUrl $base -RouteQualifierValues @("2024")) | Should -Be "$base/2024"

            # Tenant + qualifier (canonical order: tenant first, then qualifiers)
            (Resolve-DmsRouteUrl -BaseUrl $base -Tenant "Tenant1" -RouteQualifierValues @("2024")) |
                Should -Be "$base/Tenant1/2024"

            # Multiple qualifiers preserve order
            (Resolve-DmsRouteUrl -BaseUrl $base -Tenant "Tenant1" -RouteQualifierValues @("100", "2024")) |
                Should -Be "$base/Tenant1/100/2024"

            # Empty / whitespace-only segments are skipped
            (Resolve-DmsRouteUrl -BaseUrl $base -Tenant "  " -RouteQualifierValues @("", "  ", "2024")) |
                Should -Be "$base/2024"

            # Direct callers may pass a trailing slash; route composition must stay canonical.
            (Resolve-DmsRouteUrl -BaseUrl "$base/") | Should -Be $base
            (Resolve-DmsRouteUrl -BaseUrl "$base/" -Tenant "Tenant1" -RouteQualifierValues @("2024")) |
                Should -Be "$base/Tenant1/2024"
        }

        It "selects data stores via explicit DataStoreId, SchoolYear matching, or single auto-select" {
            # Explicit DataStoreId now does a CMS lookup so it can verify the instance is route-unqualified
            # (see "rejects -DataStoreId targeting a route-qualified instance" below).
            function script:Get-DataStore {
                @(
                    [pscustomobject]@{ id = [long]42; name          = "A"; dataStoreContexts     = @() },
                    [pscustomobject]@{ id = [long]99; name          = "B"; dataStoreContexts     = @() }
                )
            }
            try {
                $result = Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" -DataStoreId @(42, 99)
                $result.DataStoreIds | Should -Be @([long]42, [long]99)
            }
            finally {
                Remove-Item function:script:Get-DataStore -ErrorAction SilentlyContinue
            }

            # SchoolYear matching: shadow Get-DataStore with a script-scope function so the
            # dot-sourced Resolve-SeedTargetDataStores picks up the test stub via standard lookup.
            function script:Get-DataStore {
                @(
                    [pscustomobject]@{
                        id = [long]7
                        name          = "Year 2024"
                        dataStoreContexts     = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" }
                        )
                    },
                    [pscustomobject]@{
                        id = [long]8
                        name          = "Year 2025"
                        dataStoreContexts     = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2025" }
                        )
                    }
                )
            }
            try {
                $result = Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" -SchoolYear @(2024)
                $result.DataStoreIds | Should -Be @([long]7)

                # Single instance auto-selects
                function script:Get-DataStore {
                    @([pscustomobject]@{ id = [long]5; name          = "Single"; dataStoreContexts     = @() })
                }
                $result = Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused"
                $result.DataStoreIds | Should -Be @([long]5)
            }
            finally {
                Remove-Item function:script:Get-DataStore -ErrorAction SilentlyContinue
            }
        }

        It "rejects -DataStoreId targeting a route-qualified instance or an unknown id" {
            # DMS rejects requests whose URL qualifier count doesn't match the instance's route
            # contexts; -DataStoreId alone can't produce the qualifier values, so the seed phase must
            # surface this at validation rather than at the first BulkLoadClient POST.
            function script:Get-DataStore {
                @(
                    [pscustomobject]@{
                        id = [long]11
                        name          = "Route-qualified"
                        dataStoreContexts     = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2026" }
                        )
                    },
                    [pscustomobject]@{
                        id = [long]12
                        name          = "Plain"
                        dataStoreContexts     = @()
                    }
                )
            }
            try {
                { Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" -DataStoreId @(11) } |
                    Should -Throw -ExpectedMessage "*route context*schoolYear*"
                { Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" -DataStoreId @(999) } |
                    Should -Throw -ExpectedMessage "*was not found in CMS*"

                # Plain instance still resolves
                $result = Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" -DataStoreId @(12)
                $result.DataStoreIds | Should -Be @([long]12)
            }
            finally {
                Remove-Item function:script:Get-DataStore -ErrorAction SilentlyContinue
            }
        }

        It "rejects auto-select when the single instance carries a route context" {
            # Symmetric to the explicit -DataStoreId route-qualified rejection: a route-qualified
            # instance cannot be auto-selected because the orchestrator's single-instance branch
            # posts to {base}[/{tenant}] without composing the required qualifier segments.
            function script:Get-DataStore {
                @(
                    [pscustomobject]@{
                        id = [long]42
                        name          = "Year 2024"
                        dataStoreContexts     = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" }
                        )
                    }
                )
            }
            try {
                { Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" } |
                    Should -Throw -ExpectedMessage "*Single data store*route context*schoolYear*"
            }
            finally {
                Remove-Item function:script:Get-DataStore -ErrorAction SilentlyContinue
            }
        }

        It "fails when no selector resolves or multiple instances match without explicit selection" {
            try {
                # Zero instances, no selector.
                function script:Get-DataStore { @() }
                { Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" } | Should -Throw -ExpectedMessage "*No DMS data stores*"

                # Multiple instances, no selector.
                function script:Get-DataStore {
                    @(
                        [pscustomobject]@{ id = [long]1; name          = "A"; dataStoreContexts     = @() },
                        [pscustomobject]@{ id = [long]2; name          = "B"; dataStoreContexts     = @() }
                    )
                }
                { Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" } | Should -Throw -ExpectedMessage "*Multiple data stores*"

                # SchoolYear with no matching instance
                { Resolve-SeedTargetDataStores -CmsUrl "http://unused" -AccessToken "unused" -SchoolYear @(2099) } | Should -Throw -ExpectedMessage "*No data store found*schoolYear*2099*"
            }
            finally {
                Remove-Item function:script:Get-DataStore -ErrorAction SilentlyContinue
            }
        }

        It "orchestrator reads CONFIG_SERVICE_TENANT from env and forwards it to Resolve-SeedTargetDataStores and New-SeedLoaderCredentials" {
            # Regression: a prior implementation never read CONFIG_SERVICE_TENANT from envValues,
            # so in multi-tenant local stacks the seed flow could not see tenant-scoped instances
            # and created seed credentials in the wrong tenant context.
            $scriptPath = Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1"
            $content = Get-Content -LiteralPath $scriptPath -Raw

            $content | Should -Match '\$tenant\s*=.*CONFIG_SERVICE_TENANT'
            $content | Should -Match '(?s)Resolve-SeedTargetDataStores\b[^#]*?-Tenant\s+\$tenant'
            $content | Should -Match '(?s)New-SeedLoaderCredentials\b[^#]*?-Tenant\s+\$tenant'
        }

        It "selects XSD directory from staged ApiSchema manifest or fails when unavailable" {
            $tmpRoot = New-TestDirectory

            # Create the same relative layout emitted by prepare-dms-schema.ps1:
            # .bootstrap/ApiSchema/content/<project>/xsd
            $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
            $apiSchemaDir = Join-Path $bootstrapRoot "ApiSchema"
            $xsdSourceDir = Join-Path $apiSchemaDir "content/Ed-Fi/xsd"
            $nestedXsdDir = Join-Path $xsdSourceDir "nested"
            New-Item -ItemType Directory -Path $xsdSourceDir -Force | Out-Null
            New-Item -ItemType Directory -Path $nestedXsdDir -Force | Out-Null
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdSourceDir "Interchange-EducationOrganization.xsd") -Encoding utf8
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $nestedXsdDir "Interchange-Student.xsd") -Encoding utf8

            # Create an ApiSchema manifest under .bootstrap that references the xsd directory
            $manifestRelPath = "ApiSchema/bootstrap-api-schema-manifest.json"
            $manifestPath = Join-Path $bootstrapRoot $manifestRelPath
            @{
                projects = @(
                    @{ projectName = "Ed-Fi"; xsdDirectory = "content/Ed-Fi/xsd" }
                )
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

            $workspaceRoot = Join-Path $tmpRoot "seed-workspace"
            New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null

            $manifest = @{
                schema = @{ apiSchemaManifestPath = $manifestRelPath }
            }
            $result = Get-SeedXsdDirectory `
                -Manifest $manifest `
                -WorkspaceRoot $workspaceRoot `
                -BootstrapRoot $bootstrapRoot

            $result | Should -Be (Join-Path $workspaceRoot "xsd")
            $copied = @(Get-ChildItem -LiteralPath $result -Filter "*.xsd" -ErrorAction SilentlyContinue)
            $copied.Count | Should -Be 2
            $copied.Name | Should -Contain "Interchange-EducationOrganization.xsd"
            $copied.Name | Should -Contain "Interchange-Student.xsd"

            $interchangeNames = Get-BulkLoadClientInterchangeNames -XsdDirectory $result
            $interchangeNames | Should -Contain "EducationOrganization"
            $interchangeNames | Should -Contain "Student"

            # Fail when no xsdDirectory in manifest
            $emptyManifestRelPath = "ApiSchema/empty-manifest.json"
            $emptyManifestPath = Join-Path $bootstrapRoot $emptyManifestRelPath
            @{ projects = @(@{ projectName = "Ed-Fi" }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $emptyManifestPath -Encoding utf8

            $emptyManifest = @{
                schema = @{ apiSchemaManifestPath = $emptyManifestRelPath }
            }
            $emptyWorkspace = Join-Path $tmpRoot "empty-workspace"
            New-Item -ItemType Directory -Path $emptyWorkspace -Force | Out-Null
            {
                Get-SeedXsdDirectory `
                    -Manifest $emptyManifest `
                    -WorkspaceRoot $emptyWorkspace `
                    -BootstrapRoot $bootstrapRoot
            } | Should -Throw -ExpectedMessage "*No staged XSD files*"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "rejects invalid staged ApiSchema manifest paths before reading XSD metadata" {
            $tmpRoot = New-TestDirectory
            try {
                $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
                New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null

                $outsideXsdDir = Join-Path $tmpRoot "outside-xsd"
                New-Item -ItemType Directory -Path $outsideXsdDir -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $outsideXsdDir "Interchange-Outside.xsd") -Encoding utf8

                $outsideManifestPath = Join-Path $tmpRoot "outside-api-schema-manifest.json"
                @{
                    projects = @(
                        @{ projectName = "Outside"; xsdDirectory = $outsideXsdDir }
                    )
                } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $outsideManifestPath -Encoding utf8

                $invalidPaths = @(
                    "../outside-api-schema-manifest.json",
                    $outsideManifestPath,
                    "ApiSchema//bootstrap-api-schema-manifest.json",
                    "ApiSchema/./bootstrap-api-schema-manifest.json",
                    "C:/outside/bootstrap-api-schema-manifest.json"
                )

                $caseIndex = 0
                foreach ($invalidPath in $invalidPaths) {
                    $caseIndex++
                    $workspaceRoot = Join-Path $tmpRoot "seed-workspace-$caseIndex"
                    New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null
                    $manifest = @{
                        schema = @{ apiSchemaManifestPath = $invalidPath }
                    }

                    {
                        Get-SeedXsdDirectory `
                            -Manifest $manifest `
                            -WorkspaceRoot $workspaceRoot `
                            -BootstrapRoot $bootstrapRoot
                    } | Should -Throw -ExpectedMessage "*schema.apiSchemaManifestPath*"

                    $xsdDestDir = Join-Path $workspaceRoot "xsd"
                    if (Test-Path -LiteralPath $xsdDestDir) {
                        @(Get-ChildItem -LiteralPath $xsdDestDir -File -ErrorAction SilentlyContinue).Count | Should -Be 0
                    }
                }
            }
            finally {
                Remove-Item -LiteralPath $tmpRoot -Recurse -Force
            }
        }

        It "rejects invalid staged ApiSchema xsdDirectory paths before copying files" {
            $tmpRoot = New-TestDirectory
            try {
                $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
                $apiSchemaDir = Join-Path $bootstrapRoot "ApiSchema"
                New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null

                $outsideXsdDir = Join-Path $tmpRoot "outside-xsd"
                New-Item -ItemType Directory -Path $outsideXsdDir -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $outsideXsdDir "Interchange-Outside.xsd") -Encoding utf8

                $manifestRelPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                $manifestPath = Join-Path $bootstrapRoot $manifestRelPath
                $manifest = @{
                    schema = @{ apiSchemaManifestPath = $manifestRelPath }
                }

                $invalidXsdDirectories = @(
                    "../outside-xsd",
                    $outsideXsdDir,
                    "content//Ed-Fi/xsd",
                    "content/./Ed-Fi/xsd",
                    "C:/outside/xsd"
                )

                $caseIndex = 0
                foreach ($invalidXsdDirectory in $invalidXsdDirectories) {
                    $caseIndex++
                    @{
                        projects = @(
                            @{ projectName = "Ed-Fi"; xsdDirectory = $invalidXsdDirectory }
                        )
                    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

                    $workspaceRoot = Join-Path $tmpRoot "seed-workspace-$caseIndex"
                    New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null

                    {
                        Get-SeedXsdDirectory `
                            -Manifest $manifest `
                            -WorkspaceRoot $workspaceRoot `
                            -BootstrapRoot $bootstrapRoot
                    } | Should -Throw -ExpectedMessage "*xsdDirectory*"

                    $xsdDestDir = Join-Path $workspaceRoot "xsd"
                    if (Test-Path -LiteralPath $xsdDestDir) {
                        @(Get-ChildItem -LiteralPath $xsdDestDir -File -ErrorAction SilentlyContinue).Count | Should -Be 0
                    }
                }
            }
            finally {
                Remove-Item -LiteralPath $tmpRoot -Recurse -Force
            }
        }

        It "deduplicates shared XSD directories from staged ApiSchema manifests" {
            $tmpRoot = New-TestDirectory
            try {
                $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
                $apiSchemaDir = Join-Path $bootstrapRoot "ApiSchema"
                $xsdSourceDir = Join-Path $apiSchemaDir "content/Ed-Fi/xsd"
                New-Item -ItemType Directory -Path $xsdSourceDir -Force | Out-Null
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdSourceDir "Interchange-EducationOrganization.xsd") -Encoding utf8

                $manifestRelPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                $manifestPath = Join-Path $bootstrapRoot $manifestRelPath
                @{
                    projects = @(
                        @{ projectName = "Ed-Fi"; xsdDirectory = "content/Ed-Fi/xsd" },
                        @{ projectName = "Sample"; xsdDirectory = "content/Ed-Fi/xsd" }
                    )
                } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

                $workspaceRoot = Join-Path $tmpRoot "seed-workspace"
                New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null
                $manifest = @{
                    schema = @{ apiSchemaManifestPath = $manifestRelPath }
                }

                $result = Get-SeedXsdDirectory `
                    -Manifest $manifest `
                    -WorkspaceRoot $workspaceRoot `
                    -BootstrapRoot $bootstrapRoot

                $copied = @(Get-ChildItem -LiteralPath $result -Filter "*.xsd" -ErrorAction SilentlyContinue)
                $copied.Count | Should -Be 1
                $copied.Name | Should -Contain "Interchange-EducationOrganization.xsd"
            }
            finally {
                Remove-Item -LiteralPath $tmpRoot -Recurse -Force
            }
        }

        It "combines pinned core XSDs with staged extension XSDs for built-in extension seed packages" {
            $tmpRoot = New-TestDirectory
            try {
                $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
                $coreXsdDir = Join-Path $tmpRoot "data-standard/Schemas/Bulk"
                $apiSchemaDir = Join-Path $bootstrapRoot "ApiSchema"
                $stagedCoreXsdDir = Join-Path $apiSchemaDir "content/Ed-Fi/xsd"
                $extensionXsdDir = Join-Path $apiSchemaDir "content/TPDM/xsd"
                New-Item -ItemType Directory -Path $coreXsdDir -Force | Out-Null
                New-Item -ItemType Directory -Path $stagedCoreXsdDir -Force | Out-Null
                New-Item -ItemType Directory -Path $extensionXsdDir -Force | Out-Null

                "<xs:schema>pinned core</xs:schema>" | Set-Content -LiteralPath (Join-Path $coreXsdDir "Interchange-Student.xsd") -Encoding utf8
                "<xs:schema>staged core duplicate</xs:schema>" | Set-Content -LiteralPath (Join-Path $stagedCoreXsdDir "Interchange-Student.xsd") -Encoding utf8
                "<xs:schema />" | Set-Content -LiteralPath (Join-Path $extensionXsdDir "TPDM-EXTENSION-Interchange-Candidate-Extension.xsd") -Encoding utf8

                $manifestRelPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                $manifestPath = Join-Path $bootstrapRoot $manifestRelPath
                @{
                    projects = @(
                        @{
                            projectName = "Ed-Fi"
                            projectEndpointName = "ed-fi"
                            isExtensionProject = $false
                            xsdDirectory = "content/Ed-Fi/xsd"
                        },
                        @{
                            projectName = "TPDM"
                            projectEndpointName = "tpdm"
                            isExtensionProject = $true
                            xsdDirectory = "content/TPDM/xsd"
                        }
                    )
                } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

                $manifest = @{
                    schema = @{
                        apiSchemaManifestPath = $manifestRelPath
                        selectedExtensions = @("tpdm")
                    }
                }

                $result = New-BuiltInSeedXsdDirectory `
                    -DataStandardXsdDirectory $coreXsdDir `
                    -Manifest $manifest `
                    -BootstrapRoot $bootstrapRoot `
                    -IncludeExtensionXsds

                $xsdFiles = @(Get-ChildItem -LiteralPath $result -Filter "*.xsd" -ErrorAction SilentlyContinue)
                $xsdFiles.Name | Should -Contain "Interchange-Student.xsd"
                $xsdFiles.Name | Should -Contain "TPDM-EXTENSION-Interchange-Candidate-Extension.xsd"

                $interchangeNames = Get-BulkLoadClientInterchangeNames -XsdDirectory $result
                $interchangeNames | Should -Contain "Student"
                $interchangeNames | Should -Contain "Candidate"

                Get-Content -LiteralPath (Join-Path $result "Interchange-Student.xsd") -Raw |
                    Should -Match "pinned core"
            }
            finally {
                Remove-Item -LiteralPath $tmpRoot -Recurse -Force
            }
        }
    }

    Context "BulkLoadClient XML interface preflight" {
        It "uses cached pinned BulkLoadClient package before the feed-backed resolver" {
            $tmpRoot = New-TestDirectory
            $originalLocation = Get-Location
            $packageDir = Join-Path $tmpRoot ".packages/edfi.suite3.bulkloadclient.console.$(Get-BulkLoadClientPinnedVersion)"
            $dllDir = Join-Path $packageDir "tools/net10.0/any"
            New-Item -ItemType Directory -Path $dllDir -Force | Out-Null
            $expectedDll = Join-Path $dllDir "EdFi.BulkLoadClient.Console.dll"
            "" | Set-Content -LiteralPath $expectedDll -Encoding utf8

            function script:Get-BulkLoadClient {
                throw "feed-backed resolver should not be called when pinned package is cached"
            }

            try {
                Set-Location $tmpRoot
                Resolve-BootstrapBulkLoadClient | Should -Be $expectedDll
            }
            finally {
                Set-Location $originalLocation
                Remove-Item Function:\Get-BulkLoadClient -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "passes when --help output advertises every required XML-mode flag" {
            $fakeHelp = @"
EdFi.BulkLoadClient.Console fake
  -b, --baseUrl     The base url
  -d, --data        Path to folder containing the data files to be submitted
  -w, --working     Path to a writable folder
  -k, --key         The web API OAuth key
  -s, --secret      The web API OAuth secret
  -o, --oauthurl    The OAuth url
  -x, --xsd         Path to a folder containing the Ed-Fi Xsd Schema files
"@
            $helpInvoker = { param($dll) $fakeHelp }
            { Assert-BulkLoadClientXmlInterface -BulkLoadClientDll "fake.dll" -HelpInvoker $helpInvoker } | Should -Not -Throw
        }

        It "fails fast when --help output omits a required XML-mode flag" {
            # Drop -x from the help output so the preflight must reject it
            $fakeHelp = @"
EdFi.BulkLoadClient.Console fake
  -b, --baseUrl     The base url
  -d, --data        Path to folder containing the data files to be submitted
  -w, --working     Path to a writable folder
  -k, --key         The web API OAuth key
  -s, --secret      The web API OAuth secret
  -o, --oauthurl    The OAuth url
"@
            $helpInvoker = { param($dll) $fakeHelp }
            { Assert-BulkLoadClientXmlInterface -BulkLoadClientDll "fake.dll" -HelpInvoker $helpInvoker } |
                Should -Throw -ExpectedMessage "*XML-mode interface is unavailable*-x*"
        }

        It "passes against the resolved pinned BulkLoadClient DLL when package checks are explicitly enabled" -Skip:(($env:DMS_BOOTSTRAP_RUN_PACKAGE_TESTS -ne "true") -or (-not (Get-Command -Name dotnet -ErrorAction SilentlyContinue))) {
            # Resolution and probe both run against the real package only when the package smoke test is opted in.
            $dll = Resolve-BootstrapBulkLoadClient
            { Assert-BulkLoadClientXmlInterface -BulkLoadClientDll $dll } | Should -Not -Throw
        }
    }

    Context "shared BulkLoadClient pin" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/../Package-Management.psm1" -Force
        }

        It "Get-BulkLoadClientPinnedVersion returns the exact repo pin" {
            # Tripwire: bumping the shared pin is a deliberate change that must be reviewed
            # against the BulkLoadClient XML flag preflight and invocation shape.
            Get-BulkLoadClientPinnedVersion | Should -Be "7.3.20162"
        }

        It "Get-BulkLoadClient resolves the shared pin when no version is supplied" {
            Mock -CommandName Get-NugetPackage -ModuleName Package-Management -MockWith { ".packages/fake" }

            Get-BulkLoadClient | Out-Null

            Should -Invoke Get-NugetPackage -ModuleName Package-Management -Times 1 -Exactly -ParameterFilter {
                $PackageVersion -eq (Get-BulkLoadClientPinnedVersion)
            }
        }

        It "Get-BulkLoadClient still honors an explicit version override" {
            Mock -CommandName Get-NugetPackage -ModuleName Package-Management -MockWith { ".packages/fake" }

            Get-BulkLoadClient -PackageVersion "9.9.9" | Out-Null

            Should -Invoke Get-NugetPackage -ModuleName Package-Management -Times 1 -Exactly -ParameterFilter {
                $PackageVersion -eq "9.9.9"
            }
        }
    }

    Context "template-build BulkLoadClient pin" {
        BeforeAll {
            # Register Template-Management so InModuleScope can target it. Importing from its own
            # directory satisfies the module's CWD-relative sibling imports.
            Push-Location (Join-Path $script:sourceRepoRoot "eng/DatabaseTemplates")
            try {
                Import-Module ./Template-Management.psm1 -Force
            }
            finally {
                Pop-Location
            }
        }

        It "Initialize-BulkLoad resolves the shared pin by default" {
            $fakeBlcRoot = New-TestDirectory
            $dllDir = Join-Path $fakeBlcRoot "tools/net10.0/any"
            New-Item -ItemType Directory -Path $dllDir -Force | Out-Null
            "" | Set-Content -LiteralPath (Join-Path $dllDir "EdFi.BulkLoadClient.Console.dll") -Encoding utf8

            try {
                InModuleScope Template-Management -Parameters @{ FakeBlcRoot = $fakeBlcRoot } {
                    param($FakeBlcRoot)
                    Mock -CommandName Get-BulkLoadClient -MockWith { $FakeBlcRoot }

                    $paths = Initialize-BulkLoad
                    $paths.bulkLoadClientExe | Should -Not -BeNullOrEmpty

                    Should -Invoke Get-BulkLoadClient -Times 1 -Exactly -ParameterFilter {
                        $PackageVersion -eq (Get-BulkLoadClientPinnedVersion)
                    }
                }
            }
            finally {
                Remove-Item -LiteralPath $fakeBlcRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "Initialize-BulkLoad rejects a <Override> override before any resolution" -ForEach @(
            @{ Override = '7.3' }
            @{ Override = '7.3.*' }
        ) {
            InModuleScope Template-Management -Parameters @{ Override = $Override } {
                param($Override)
                Mock -CommandName Get-BulkLoadClient -MockWith { throw "resolver must not be called" }

                { Initialize-BulkLoad -BulkLoadVersion $Override } |
                    Should -Throw -ExpectedMessage "*diagnostic override*exact three-part numeric version*"

                Should -Invoke Get-BulkLoadClient -Times 0 -Exactly
            }
        }
    }

    Context "BulkLoadClient invocation and school-year loop" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/env-utility.psm1" -Force
            # SchoolYearType preconditions call ConvertTo-FormBody; import its module so this
            # context is self-sufficient regardless of module-table churn from sibling contexts.
            Import-Module "$script:sourceDockerComposeRoot/../Dms-Management.psm1" -Force
        }

        It "treats SchoolYearType REST 201 and 200 as created and 409 as already existing" {
            $oauthUrl = "http://localhost:8081/connect/token"
            $statuses = [System.Collections.Generic.Queue[int]]::new()
            foreach ($status in @(201, 200, 409)) {
                $statuses.Enqueue($status)
            }
            $requests = [System.Collections.Generic.List[hashtable]]::new()
            $webInvoker = {
                param(
                    [string]$Uri,
                    [string]$Method,
                    $Body,
                    [string]$ContentType,
                    [hashtable]$Headers,
                    $SkipHttpErrorCheck
                )

                $requests.Add(@{
                    Uri                = $Uri
                    Method             = $Method
                    Body               = $Body
                    ContentType        = $ContentType
                    Headers            = $Headers
                    SkipHttpErrorCheck = $SkipHttpErrorCheck
                })

                if ($Uri -eq $oauthUrl) {
                    return [pscustomobject]@{ access_token = "fake-token" }
                }

                $status = $statuses.Dequeue()
                return [pscustomobject]@{
                    StatusCode = $status
                    Content    = "status-$status"
                }
            }.GetNewClosure()

            Invoke-SchoolYearTypeRestPrecondition `
                -DmsBaseUrl "http://localhost:8080/2025" `
                -Key "client-key" `
                -Secret "client-secret" `
                -OAuthUrl $oauthUrl `
                -FirstYear 2024 `
                -LastYear 2026 `
                -CurrentYear 2025 `
                -WebInvoker $webInvoker

            $requests.Count | Should -Be 4
            $requests[0].Uri | Should -Be $oauthUrl
            $requests[0].Body | Should -Be "client_id=client-key&client_secret=client-secret&grant_type=client_credentials"

            $posts = @($requests | Where-Object { $_.Uri -eq "http://localhost:8080/2025/data/ed-fi/schoolYearTypes" })
            $posts.Count | Should -Be 3
            $posts[0].Headers.Authorization | Should -Be "Bearer fake-token"
            $posts[0].SkipHttpErrorCheck | Should -BeTrue
            $posts[0].Body | Should -Match '"schoolYear":2024'
            $posts[1].Body | Should -Match '"schoolYear":2025'
            $posts[1].Body | Should -Match '"currentSchoolYear":true'
            $posts[2].Body | Should -Match '"schoolYear":2026'
        }

        It "throws when SchoolYearType REST returns an unexpected status" {
            $oauthUrl = "http://localhost:8081/connect/token"
            $webInvoker = {
                param(
                    [string]$Uri,
                    [string]$Method,
                    $Body,
                    [string]$ContentType,
                    [hashtable]$Headers,
                    $SkipHttpErrorCheck
                )

                if ($Uri -eq $oauthUrl) {
                    return [pscustomobject]@{ access_token = "fake-token" }
                }

                return [pscustomobject]@{
                    StatusCode = 500
                    Content    = "server failed"
                }
            }.GetNewClosure()

            {
                Invoke-SchoolYearTypeRestPrecondition `
                    -DmsBaseUrl "http://localhost:8080" `
                    -Key "client-key" `
                    -Secret "client-secret" `
                    -OAuthUrl $oauthUrl `
                    -FirstYear 2024 `
                    -LastYear 2024 `
                    -CurrentYear 2024 `
                    -WebInvoker $webInvoker
            } | Should -Throw -ExpectedMessage "*HTTP 500*server failed*"
        }

        It "constructs BulkLoadClient arguments with base URL, data directory, OAuth, key, secret, and XSD" {
            $capture = @{ dll = $null; args = $null }

            $invoker = {
                param([string]$dll, [string[]]$invokerArgs)
                $capture.dll = $dll
                $capture.args = $invokerArgs
                return 0
            }.GetNewClosure()

            Invoke-BulkLoadClient `
                -BulkLoadClientDll "fake.dll" `
                -DmsBaseUrl "http://localhost:8080" `
                -DataDirectory "/data" `
                -WorkingDirectory "/working" `
                -Key "my-key" `
                -Secret "my-secret" `
                -OAuthUrl "http://localhost:8081/connect/token" `
                -XsdDirectory "/xsd" `
                -Invoker $invoker

            $capture.dll | Should -Be "fake.dll"
            $capture.args | Should -Contain "-b"
            $capture.args | Should -Contain "http://localhost:8080"
            $capture.args | Should -Contain "-d"
            $capture.args | Should -Contain "/data"
            $capture.args | Should -Contain "-w"
            $capture.args | Should -Contain "/working"
            $capture.args | Should -Contain "-k"
            $capture.args | Should -Contain "my-key"
            $capture.args | Should -Contain "-s"
            $capture.args | Should -Contain "my-secret"
            $capture.args | Should -Contain "-o"
            $capture.args | Should -Contain "http://localhost:8081/connect/token"
            $capture.args | Should -Contain "-x"
            $capture.args | Should -Contain "/xsd"
            # -n (--novalidation) MUST NOT be passed: sample XML and XSDs are now sourced from the same
            # Ed-Fi-Data-Standard tag, so validation is on by construction.
            $capture.args | Should -Not -Contain "-n"
            # Tuning flags required to prevent circuit-breaker tripping and rate-limiter flooding.
            # DMS's Polly breaker (FailureRatio=0.01, MinimumThroughput=2, 10s sampling, 30s break)
            # opens almost immediately under unbounded concurrency; these conservative defaults keep
            # the relational backend stable. See Invoke-BulkLoadClient in load-dms-seed-data.ps1.
            $capture.args | Should -Contain "-c"
            $capture.args | Should -Contain "-l"
            $capture.args | Should -Contain "-t"
            $capture.args | Should -Contain "-r"
            # Verify the values are numeric strings (non-empty)
            $cIndex = [array]::IndexOf($capture.args, "-c")
            $lIndex = [array]::IndexOf($capture.args, "-l")
            $tIndex = [array]::IndexOf($capture.args, "-t")
            $rIndex = [array]::IndexOf($capture.args, "-r")
            [int]($capture.args[$cIndex + 1]) | Should -BeGreaterThan 0
            [int]($capture.args[$lIndex + 1]) | Should -BeGreaterThan 0
            [int]($capture.args[$tIndex + 1]) | Should -BeGreaterThan 0
            [int]($capture.args[$rIndex + 1]) | Should -BeGreaterOrEqual 1
        }

        It "invokes BulkLoadClient once for single instance and once per year for school-year range" {
            $counter = @{ count = 0 }
            $invoker = {
                param([string]$dll, [string[]]$invokerArgs)
                $counter.count++
                return 0
            }.GetNewClosure()

            # Single-instance: one invocation
            $counter.count = 0
            Invoke-BulkLoadClient `
                -BulkLoadClientDll "fake.dll" `
                -DmsBaseUrl "http://localhost:8080" `
                -DataDirectory "/data" `
                -WorkingDirectory "/working" `
                -Key "k" -Secret "s" `
                -OAuthUrl "http://localhost:8081/connect/token" `
                -XsdDirectory "/xsd" `
                -Invoker $invoker

            $counter.count | Should -Be 1

            # School-year loop: one invocation per year
            $counter.count = 0
            $envValues = @{ DMS_CONFIG_ASPNETCORE_HTTP_PORTS = "8081" }
            foreach ($year in @(2024, 2025)) {
                $perYearBase = "http://localhost:8080/$year"
                $perYearOAuth = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "self-contained" -SchoolYear $year
                Invoke-BulkLoadClient `
                    -BulkLoadClientDll "fake.dll" `
                    -DmsBaseUrl $perYearBase `
                    -DataDirectory "/data" `
                    -WorkingDirectory "/working" `
                    -Key "k" -Secret "s" `
                    -OAuthUrl $perYearOAuth `
                    -XsdDirectory "/xsd" `
                    -Invoker $invoker
            }
            $counter.count | Should -Be 2
        }

        It "uses school-year URL segment before /data and per-year OAuth token URL for self-contained" {
            $recorded = [System.Collections.Generic.List[hashtable]]::new()
            $invoker = {
                param([string]$dll, [string[]]$invokerArgs)
                $invocation = @{}
                for ($i = 0; $i -lt $invokerArgs.Count - 1; $i++) {
                    if ($invokerArgs[$i] -in @("-b", "-d", "-w", "-k", "-s", "-o", "-x")) {
                        $invocation[$invokerArgs[$i]] = $invokerArgs[$i + 1]
                    }
                }
                $recorded.Add($invocation)
                return 0
            }.GetNewClosure()

            $envValues = @{ DMS_CONFIG_ASPNETCORE_HTTP_PORTS = "8081" }
            $dmsBase = "http://localhost:8080"

            foreach ($year in @(2024, 2025)) {
                $perYearBase = "$dmsBase/$year"
                $perYearOAuth = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "self-contained" -SchoolYear $year
                Invoke-BulkLoadClient `
                    -BulkLoadClientDll "fake.dll" `
                    -DmsBaseUrl $perYearBase `
                    -DataDirectory "/data" `
                    -WorkingDirectory "/working" `
                    -Key "k" -Secret "s" `
                    -OAuthUrl $perYearOAuth `
                    -XsdDirectory "/xsd" `
                    -Invoker $invoker
            }

            $recorded.Count | Should -Be 2

            $inv2024 = $recorded[0]
            $inv2024["-b"] | Should -Be "http://localhost:8080/2024"
            $inv2024["-o"] | Should -Be "http://localhost:8081/connect/token/2024"

            $inv2025 = $recorded[1]
            $inv2025["-b"] | Should -Be "http://localhost:8080/2025"
            $inv2025["-o"] | Should -Be "http://localhost:8081/connect/token/2025"
        }

        It "fails and retains seed workspace when BulkLoadClient exits with non-zero code" {
            $tmpRoot = New-TestDirectory
            $seedDataDir = Join-Path $tmpRoot "workspace-data"
            New-Item -ItemType Directory -Path $seedDataDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $seedDataDir "marker.xml") -Encoding utf8

            $failingInvoker = {
                param([string]$dll, [string[]]$invokerArgs)
                return 1
            }

            {
                Invoke-BulkLoadClient `
                    -BulkLoadClientDll "fake.dll" `
                    -DmsBaseUrl "http://localhost:8080" `
                    -DataDirectory $seedDataDir `
                    -WorkingDirectory $tmpRoot `
                    -Key "k" -Secret "s" `
                    -OAuthUrl "http://localhost:8081/connect/token" `
                    -XsdDirectory "/xsd" `
                    -Invoker $failingInvoker
            } | Should -Throw -ExpectedMessage "*non-zero code*"

            Test-Path -LiteralPath $seedDataDir | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $seedDataDir "marker.xml") | Should -BeTrue

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }
    }

    Context "wrapper opt-in" {
        It "remains absent from direct load-dms-seed-data.ps1 invocation parameter set" {
            $seedScript = Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1"
            Test-Path -LiteralPath $seedScript | Should -BeTrue

            $content = Get-Content -LiteralPath $seedScript -Raw
            # Extract declared parameter names from inside the param() block
            $paramBody = ([regex]::Match($content, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $declaredParams = [regex]::Matches($paramBody, '\$(\w+)') |
                ForEach-Object { $_.Groups[1].Value }
            $declaredParams | Should -Not -Contain "LoadSeedData"
        }

        It "bootstrap-local-dms.ps1 declares -LoadSeedData and seed-owned flags without exposing -DataStoreId" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            Test-Path -LiteralPath $wrapperScript | Should -BeTrue

            $wrapperContent = Get-Content -LiteralPath $wrapperScript -Raw
            $wrapperParamBody = ([regex]::Match($wrapperContent, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $wrapperParams = [regex]::Matches($wrapperParamBody, '\$(\w+)') |
                ForEach-Object { $_.Groups[1].Value }

            $wrapperParams | Should -Contain "LoadSeedData"
            $wrapperParams | Should -Contain "SeedTemplate"
            $wrapperParams | Should -Contain "SeedDataPath"
            $wrapperParams | Should -Contain "AdditionalNamespacePrefix"
            $wrapperParams | Should -Contain "IdentityProvider"
            $wrapperParams | Should -Contain "EnvironmentFile"
            $wrapperParams | Should -Not -Contain "DataStoreId"
        }

        It "bootstrap-local-dms.ps1 gates the seed phase on -LoadSeedData" {
            # Use an isolated copy so we can stub both downstream phase scripts.
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            # Stage a fake bootstrap manifest so the wrapper's pre-start preflight passes when
            # the second invocation below adds -LoadSeedData.
            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            "{}" | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            # Without -LoadSeedData: start runs, seed must not
            & $wrapperCopy
            Test-Path -LiteralPath $startProbe | Should -BeTrue
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when -LoadSeedData is absent"

            # With -LoadSeedData: seed must run
            Remove-Item -LiteralPath $startProbe -Force
            & $wrapperCopy -LoadSeedData
            Test-Path -LiteralPath $startProbe | Should -BeTrue
            Test-Path -LiteralPath $seedProbe | Should -BeTrue -Because "seed phase must run when -LoadSeedData is present"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 forces -EnableConfig when -LoadSeedData is supplied" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            # Stage a fake bootstrap manifest so the wrapper's pre-start preflight passes.
            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            "{}" | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startArgsProbe = Join-Path $tmpRoot "start-args.txt"
            # Stub declares -EnableConfig explicitly so it binds when the wrapper passes it,
            # then writes the bound value to the probe file.
            "param([switch]`$EnableConfig, [Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startArgsProbe' -Value (`"EnableConfig=`$EnableConfig`") -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest)" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            # -LoadSeedData alone (no explicit -EnableConfig); wrapper must force config on.
            & $wrapperCopy -LoadSeedData
            $captured = Get-Content -LiteralPath $startArgsProbe -Raw
            $captured.Trim() | Should -Be "EnableConfig=True" -Because "wrapper must force -EnableConfig when -LoadSeedData is supplied"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects malformed -SchoolYearRange before any phase invocation" {
            # Regression: malformed -SchoolYearRange used to be forwarded to start-(local|published)-dms.ps1
            # and parsed only later in the seed-phase block, so Docker/CMS side effects could fire before
            # the late parse-time throw surfaced.
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            { & $wrapperCopy -SchoolYearRange "garbage" } | Should -Throw -ExpectedMessage "*Invalid -SchoolYearRange*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when SchoolYearRange is malformed"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when SchoolYearRange is malformed"

            # Well-formed ranges still pass through
            & $wrapperCopy -SchoolYearRange "2024-2025"
            Test-Path -LiteralPath $startProbe | Should -BeTrue

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 auto-stages core-only standard mode when no workspace is pre-staged" {
            # bootstrap-design.md Section 9.4.1: the thin wrapper requires no manual prepare
            # step. With no -Extensions and no pre-staged .bootstrap/bootstrap-manifest.json, the wrapper
            # stages core-only standard mode (prepare-dms-schema.ps1 + prepare-dms-claims.ps1) before the
            # start phase, then reaches start (and the seed phase under -LoadSeedData) without throwing.
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $prepareProbe = Join-Path $tmpRoot "prepare-invoked.txt"
            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            # prepare-dms-schema.ps1 stub records that the core-only prepare phase ran and asserts no
            # -Extensions were forwarded (core-only path).
            "param([string[]]`$Extensions, [Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$prepareProbe' -Value (`"extensions=`$(`$Extensions -join ',')`") -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "prepare-dms-schema.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest)" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "prepare-dms-claims.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            # No .bootstrap/bootstrap-manifest.json exists; -LoadSeedData must NOT throw -- the core-only
            # prepare phase stages the workspace first, then start and seed run.
            { & $wrapperCopy -LoadSeedData } | Should -Not -Throw

            (Get-Content -LiteralPath $prepareProbe -Raw).Trim() | Should -Be "extensions=" -Because "core-only auto-stage must run prepare-dms-schema.ps1 with no -Extensions"
            Test-Path -LiteralPath $startProbe | Should -BeTrue -Because "start phase must run after core-only staging"
            Test-Path -LiteralPath $seedProbe | Should -BeTrue -Because "seed phase must run when -LoadSeedData is supplied"

            # The no-argument core-only happy path also stages and starts (no seed).
            Remove-Item -LiteralPath $prepareProbe, $startProbe, $seedProbe -Force -ErrorAction SilentlyContinue
            & $wrapperCopy
            Test-Path -LiteralPath $prepareProbe | Should -BeTrue -Because "no-argument wrapper must auto-stage core-only"
            Test-Path -LiteralPath $startProbe | Should -BeTrue
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run without -LoadSeedData"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects unsupported env identity provider before derived-env or phase invocation" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            foreach ($moduleName in @("bootstrap-wrapper.psm1", "env-utility.psm1", "bootstrap-manifest.psm1")) {
                Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $moduleName) -Destination $tmpDockerCompose
            }
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            '{"schema":{"selectionMode":"Standard"}}' | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $envFile = Join-Path $tmpRoot "bad-idp.env"
            "DMS_CONFIG_IDENTITY_PROVIDER=oauth`n" | Set-Content -LiteralPath $envFile -Encoding utf8

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            { & $wrapperCopy -LoadSeedData -EnvironmentFile $envFile } |
                Should -Throw -ExpectedMessage "*Unsupported identity provider*oauth*from env file*"

            Test-Path -LiteralPath (Join-Path $bootstrapDir ".env.derived") | Should -BeFalse -Because "invalid env provider must fail before derived env is written"
            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when wrapper identity provider validation fails"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when wrapper identity provider validation fails"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects descending -SchoolYearRange before any phase invocation" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            { & $wrapperCopy -SchoolYearRange "2026-2024" } | Should -Throw -ExpectedMessage "*StartYear*must be less than or equal to EndYear*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when SchoolYearRange is descending"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when SchoolYearRange is descending"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects -SeedTemplate + -SeedDataPath simultaneously before any phase invocation" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            '{"schema":{"selectionMode":"Standard"}}' | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $customSeedDir = Join-Path $tmpRoot "custom-seeds"
            New-Item -ItemType Directory -Path $customSeedDir -Force | Out-Null

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            { & $wrapperCopy -LoadSeedData -SeedTemplate Minimal -SeedDataPath $customSeedDir } |
                Should -Throw -ExpectedMessage "*mutually exclusive*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when SeedTemplate and SeedDataPath are both supplied"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when SeedTemplate and SeedDataPath are both supplied"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects ApiSchemaPath manifest mode without -SeedDataPath before any phase invocation" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            '{"schema":{"selectionMode":"ApiSchemaPath"}}' | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            { & $wrapperCopy -LoadSeedData } | Should -Throw -ExpectedMessage "*Expert mode*requires -SeedDataPath*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when ApiSchemaPath mode is missing -SeedDataPath"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when ApiSchemaPath mode is missing -SeedDataPath"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects ApiSchemaPath manifest mode + -SeedTemplate before any phase invocation" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            '{"schema":{"selectionMode":"ApiSchemaPath"}}' | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            { & $wrapperCopy -LoadSeedData -SeedTemplate Minimal } | Should -Throw -ExpectedMessage "*Expert mode*does not support -SeedTemplate*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when ApiSchemaPath mode is given -SeedTemplate"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when ApiSchemaPath mode is given -SeedTemplate"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-published-dms.ps1 declares -LoadSeedData and seed-owned flags without exposing -DataStoreId" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            Test-Path -LiteralPath $wrapperScript | Should -BeTrue

            $wrapperContent = Get-Content -LiteralPath $wrapperScript -Raw
            $wrapperParamBody = ([regex]::Match($wrapperContent, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $wrapperParams = [regex]::Matches($wrapperParamBody, '\$(\w+)') |
                ForEach-Object { $_.Groups[1].Value }

            $wrapperParams | Should -Contain "LoadSeedData"
            $wrapperParams | Should -Contain "SeedTemplate"
            $wrapperParams | Should -Contain "SeedDataPath"
            $wrapperParams | Should -Contain "AdditionalNamespacePrefix"
            $wrapperParams | Should -Contain "IdentityProvider"
            $wrapperParams | Should -Contain "EnvironmentFile"
            $wrapperParams | Should -Contain "EnableConfig"
            $wrapperParams | Should -Contain "AddExtensionSecurityMetadata"
            $wrapperParams | Should -Not -Contain "DataStoreId"
        }

        It "bootstrap-published-dms.ps1 gates the seed phase on -LoadSeedData" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            # Stage a fake bootstrap manifest so the wrapper's pre-start preflight passes.
            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            "{}" | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-published-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-published-dms.ps1"

            # Without -LoadSeedData: start runs, seed must not
            & $wrapperCopy
            Test-Path -LiteralPath $startProbe | Should -BeTrue
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when -LoadSeedData is absent"

            # With -LoadSeedData: seed must run
            Remove-Item -LiteralPath $startProbe -Force
            & $wrapperCopy -LoadSeedData
            Test-Path -LiteralPath $startProbe | Should -BeTrue
            Test-Path -LiteralPath $seedProbe | Should -BeTrue -Because "seed phase must run when -LoadSeedData is present"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-published-dms.ps1 auto-stages core-only standard mode when no workspace is pre-staged" {
            # bootstrap-design.md Section 9.4.1: like the local wrapper, the published thin wrapper requires
            # no manual prepare step. With no -Extensions and no pre-staged .bootstrap/bootstrap-manifest.json,
            # it must stage core-only standard mode (prepare-dms-schema.ps1 + prepare-dms-claims.ps1) before
            # the start phase, then reach start (and the seed phase under -LoadSeedData) without throwing.
            # The other published-wrapper tests pre-stage a fake manifest, which bypasses this auto-stage path.
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $prepareProbe = Join-Path $tmpRoot "prepare-invoked.txt"
            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            # prepare-dms-schema.ps1 stub records that the core-only prepare phase ran and asserts no
            # -Extensions were forwarded (core-only path).
            "param([string[]]`$Extensions, [Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$prepareProbe' -Value (`"extensions=`$(`$Extensions -join ',')`") -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "prepare-dms-schema.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest)" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "prepare-dms-claims.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-published-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-published-dms.ps1"

            # No .bootstrap/bootstrap-manifest.json exists; -LoadSeedData must NOT throw -- the core-only
            # prepare phase stages the workspace first, then start and seed run.
            { & $wrapperCopy -LoadSeedData } | Should -Not -Throw

            (Get-Content -LiteralPath $prepareProbe -Raw).Trim() | Should -Be "extensions=" -Because "core-only auto-stage must run prepare-dms-schema.ps1 with no -Extensions"
            Test-Path -LiteralPath $startProbe | Should -BeTrue -Because "start phase must run after core-only staging"
            Test-Path -LiteralPath $seedProbe | Should -BeTrue -Because "seed phase must run when -LoadSeedData is supplied"

            # The no-argument core-only happy path also stages and starts (no seed).
            Remove-Item -LiteralPath $prepareProbe, $startProbe, $seedProbe -Force -ErrorAction SilentlyContinue
            & $wrapperCopy
            Test-Path -LiteralPath $prepareProbe | Should -BeTrue -Because "no-argument wrapper must auto-stage core-only"
            Test-Path -LiteralPath $startProbe | Should -BeTrue
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run without -LoadSeedData"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-published-dms.ps1 forces -EnableConfig when -LoadSeedData is supplied" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            # Stage a fake bootstrap manifest so the wrapper's pre-start preflight passes.
            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            "{}" | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $startArgsProbe = Join-Path $tmpRoot "start-args.txt"
            "param([switch]`$EnableConfig, [Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startArgsProbe' -Value (`"EnableConfig=`$EnableConfig`") -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-published-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest)" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-published-dms.ps1"

            & $wrapperCopy -LoadSeedData
            $captured = Get-Content -LiteralPath $startArgsProbe -Raw
            $captured.Trim() | Should -Be "EnableConfig=True" -Because "wrapper must force -EnableConfig when -LoadSeedData is supplied"

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "start-published-dms.ps1 no longer declares -LoadSeedData" {
            # The direct-SQL database-template path moves to the phase-command model: use
            # Restore-DatabaseTemplate (setup-database-template.psm1) via the
            # bootstrap-published-dms.ps1 -RestoreTemplate flow, or load-dms-seed-data.ps1
            # directly for the API-based seed path.
            $startScript = Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"
            Test-Path -LiteralPath $startScript | Should -BeTrue
            $content = Get-Content -LiteralPath $startScript -Raw
            $paramBody = ([regex]::Match($content, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $declaredParams = [regex]::Matches($paramBody, '\$(\w+)') |
                ForEach-Object { $_.Groups[1].Value }
            $declaredParams | Should -Not -Contain "LoadSeedData" -Because "start-published-dms.ps1 must not declare -LoadSeedData after the direct-SQL seed path is removed"
        }

        It "start-local-dms.ps1 no longer declares -LoadSeedData (DMS-1153 de-scope)" {
            # DMS-1153 removes -LoadSeedData from start-local-dms.ps1. The direct-SQL database-template
            # path moves to the phase-command model; use load-dms-seed-data.ps1 directly or the
            # bootstrap-local-dms.ps1 wrapper with -LoadSeedData for the API-based seed path.
            $startScript = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"
            Test-Path -LiteralPath $startScript | Should -BeTrue
            $content = Get-Content -LiteralPath $startScript -Raw
            $paramBody = ([regex]::Match($content, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $declaredParams = [regex]::Matches($paramBody, '\$(\w+)') |
                ForEach-Object { $_.Groups[1].Value }
            $declaredParams | Should -Not -Contain "LoadSeedData" -Because "start-local-dms.ps1 must not declare -LoadSeedData after the DMS-1153 de-scope"
        }

        It "start-(local|published)-dms.ps1 derive CMS URL from env-utility's Resolve-CmsBaseUrl" {
            # Regression guard: hard-coded http://localhost:8081 in the start scripts diverges from
            # the seed phase's env-derived CMS URL when DMS_CONFIG_ASPNETCORE_HTTP_PORTS is overridden,
            # silently splitting credential creation and seed authentication across two CMS endpoints.
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $startScript = Join-Path $script:sourceDockerComposeRoot $name
                Test-Path -LiteralPath $startScript | Should -BeTrue
                $content = Get-Content -LiteralPath $startScript -Raw
                $content | Should -Match 'Resolve-CmsBaseUrl\s+-EnvValues\s+\$envValues' -Because "$name must resolve CMS URL via Resolve-CmsBaseUrl so a custom DMS_CONFIG_ASPNETCORE_HTTP_PORTS is honored"
                $content | Should -Not -Match '"http://localhost:8081"' -Because "$name must not hard-code the default CMS URL; the resolver fallback already returns http://localhost:8081 when the env override is absent"
            }
        }
    }

    Context "wrapper -RestoreTemplate mutual exclusion with seed-source flags" {
        It "rejects -RestoreTemplate combined with -LoadSeedData, -SeedTemplate, or -SeedDataPath on both wrappers before any phase invocation" {
            foreach ($wrapperEntryScriptName in @("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                $wrapperScript = Join-Path $script:sourceDockerComposeRoot $wrapperEntryScriptName
                $tmpRoot = New-TestDirectory
                $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

                Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
                Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
                Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

                $startScriptName = $wrapperEntryScriptName -replace '^bootstrap-', 'start-'
                $startProbe = Join-Path $tmpRoot "start-invoked.txt"
                $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

                "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                    Set-Content -LiteralPath (Join-Path $tmpDockerCompose $startScriptName) -Encoding utf8
                "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                    Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

                $wrapperCopy = Join-Path $tmpDockerCompose $wrapperEntryScriptName
                $customSeedDir = Join-Path $tmpRoot "custom-seeds"
                New-Item -ItemType Directory -Path $customSeedDir -Force | Out-Null

                { & $wrapperCopy -RestoreTemplate Minimal -LoadSeedData } |
                    Should -Throw -ExpectedMessage "*-RestoreTemplate and -LoadSeedData are mutually exclusive*" -Because "$wrapperEntryScriptName must reject -RestoreTemplate + -LoadSeedData"
                { & $wrapperCopy -RestoreTemplate Minimal -SeedTemplate Populated } |
                    Should -Throw -ExpectedMessage "*-RestoreTemplate and -SeedTemplate are mutually exclusive*" -Because "$wrapperEntryScriptName must reject -RestoreTemplate + -SeedTemplate"
                { & $wrapperCopy -RestoreTemplate Minimal -SeedDataPath $customSeedDir } |
                    Should -Throw -ExpectedMessage "*-RestoreTemplate and -SeedDataPath are mutually exclusive*" -Because "$wrapperEntryScriptName must reject -RestoreTemplate + -SeedDataPath"

                Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "no phase may run when $wrapperEntryScriptName rejects the -RestoreTemplate combination"
                Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "no phase may run when $wrapperEntryScriptName rejects the -RestoreTemplate combination"

                Remove-Item -LiteralPath $tmpRoot -Recurse -Force
            }
        }

        It "rejects -RestoreTemplate combined with -LoadSeedData on both wrappers under -DatabaseEngine mssql (gating is engine-agnostic)" {
            foreach ($wrapperEntryScriptName in @("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")) {
                $wrapperScript = Join-Path $script:sourceDockerComposeRoot $wrapperEntryScriptName
                $tmpRoot = New-TestDirectory
                $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

                Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
                Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
                Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

                $startScriptName = $wrapperEntryScriptName -replace '^bootstrap-', 'start-'
                $startProbe = Join-Path $tmpRoot "start-invoked.txt"

                "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                    Set-Content -LiteralPath (Join-Path $tmpDockerCompose $startScriptName) -Encoding utf8

                $wrapperCopy = Join-Path $tmpDockerCompose $wrapperEntryScriptName

                { & $wrapperCopy -RestoreTemplate Minimal -LoadSeedData -DatabaseEngine mssql } |
                    Should -Throw -ExpectedMessage "*-RestoreTemplate and -LoadSeedData are mutually exclusive*" -Because "$wrapperEntryScriptName must reject the combination the same way under -DatabaseEngine mssql"

                Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "no phase may run when $wrapperEntryScriptName rejects the -RestoreTemplate combination under -DatabaseEngine mssql"

                Remove-Item -LiteralPath $tmpRoot -Recurse -Force
            }
        }
    }

    Context "database-template package id engine-token conversion" {
        BeforeAll {
            Import-Module (Join-Path $script:sourceDockerComposeRoot "env-utility.psm1") -Force
        }

        Context "Convert-TemplatePackageToken" {
            It "maps the template segment Populated to Minimal and back, leaving prefix, engine, and version untouched" {
                $populated = "EdFi.Api.Populated.Template.PostgreSql.5.2.0"

                $minimal = Convert-TemplatePackageToken -PackageId $populated -Template "Minimal"
                $minimal | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"

                (Convert-TemplatePackageToken -PackageId $minimal -Template "Populated") | Should -Be $populated
            }

            It "maps the engine segment PostgreSql to MsSql and back, leaving prefix, template, and version untouched" {
                $postgres = "EdFi.Dms.Minimal.Template.PostgreSql.6.1.0"

                $mssql = Convert-TemplatePackageToken -PackageId $postgres -Engine "MsSql"
                $mssql | Should -Be "EdFi.Dms.Minimal.Template.MsSql.6.1.0"

                (Convert-TemplatePackageToken -PackageId $mssql -Engine "PostgreSql") | Should -Be $postgres
            }

            It "leaves the Smoke template segment untouched unless -Template is passed explicitly" {
                $smoke = "EdFi.Api.Smoke.Template.PostgreSql.5.2.0"

                (Convert-TemplatePackageToken -PackageId $smoke -Engine "MsSql") | Should -Be "EdFi.Api.Smoke.Template.MsSql.5.2.0" -Because "omitting -Template must leave the Smoke segment alone"
                (Convert-TemplatePackageToken -PackageId $smoke -Template "Minimal") | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" -Because "an explicit -Template must still rewrite Smoke"
            }

            It "returns a package id unchanged when it does not match the <template>.Template.<engine>.<version> shape, and passes blank input through" {
                $unrecognized = "Some.Unrelated.Package.Id.1.0.0"
                (Convert-TemplatePackageToken -PackageId $unrecognized -Engine "MsSql" -Template "Populated") | Should -Be $unrecognized
                (Convert-TemplatePackageToken -PackageId "" -Engine "MsSql") | Should -Be ""
            }
        }

        Context "Resolve-DatabaseEngineEnvironmentFile DATABASE_TEMPLATE_PACKAGE rewrite" {
            BeforeEach {
                $script:engineTokenWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-seed-enginetoken-$([Guid]::NewGuid().ToString('N'))"
                $script:engineTokenComposeRoot = Join-Path $script:engineTokenWork "compose"
                New-Item -ItemType Directory -Path $script:engineTokenComposeRoot -Force | Out-Null

                Set-Content -LiteralPath (Join-Path $script:engineTokenComposeRoot ".env.mssql") -Value @"
MSSQL_SA_PASSWORD=Abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_DATASTORE=mssql
DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline
            }

            AfterEach {
                if (Test-Path -LiteralPath $script:engineTokenWork) {
                    Remove-Item -LiteralPath $script:engineTokenWork -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            It "rewrites a PostgreSql DATABASE_TEMPLATE_PACKAGE to MsSql when composing from a plain .env-style base" {
                $basePath = Join-Path $script:engineTokenWork ".env"
                Set-Content -LiteralPath $basePath -Value "DMS_DATASTORE=postgresql`nDATABASE_TEMPLATE_PACKAGE=EdFi.Api.Minimal.Template.PostgreSql.5.2.0`n" -NoNewline

                $derivedPath = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $basePath -DockerComposeRoot $script:engineTokenComposeRoot

                $values = ReadValuesFromEnvFile $derivedPath
                $values["DATABASE_TEMPLATE_PACKAGE"] | Should -Be "EdFi.Api.Minimal.Template.MsSql.5.2.0"
            }

            It "rewrites a PostgreSql DATABASE_TEMPLATE_PACKAGE to MsSql when composing from a data-standard-derived (.env.ds61-style) base" {
                $basePath = Join-Path $script:engineTokenWork ".env.ds61"
                Set-Content -LiteralPath $basePath -Value "DMS_DATASTORE=postgresql`nDATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.6.1.0`n" -NoNewline

                $derivedPath = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $basePath -DockerComposeRoot $script:engineTokenComposeRoot

                $values = ReadValuesFromEnvFile $derivedPath
                $values["DATABASE_TEMPLATE_PACKAGE"] | Should -Be "EdFi.Api.Populated.Template.MsSql.6.1.0"
            }

            It "is idempotent on re-compose: composing an already-corrected derived file returns it unchanged" {
                $basePath = Join-Path $script:engineTokenWork ".env"
                Set-Content -LiteralPath $basePath -Value "DMS_DATASTORE=postgresql`nDATABASE_TEMPLATE_PACKAGE=EdFi.Api.Minimal.Template.PostgreSql.5.2.0`n" -NoNewline

                $firstPass = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $basePath -DockerComposeRoot $script:engineTokenComposeRoot
                $secondPass = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $firstPass -DockerComposeRoot $script:engineTokenComposeRoot

                $secondPass | Should -Be $firstPass -Because "re-composing an already-corrected mssql-flagged file must not produce a derived-of-derived file"
                (ReadValuesFromEnvFile $secondPass)["DATABASE_TEMPLATE_PACKAGE"] | Should -Be "EdFi.Api.Minimal.Template.MsSql.5.2.0"
            }

            It "corrects a stale PostgreSql DATABASE_TEMPLATE_PACKAGE on an already-composed mssql base without mutating the source file" {
                # Models a base file that already carries DMS_DATASTORE=mssql (e.g. hand-edited, or
                # composed by an earlier phase) but whose DATABASE_TEMPLATE_PACKAGE was never rewritten.
                $staleBasePath = Join-Path $script:engineTokenWork ".env"
                $staleContent = "DMS_DATASTORE=mssql`nDATABASE_TEMPLATE_PACKAGE=EdFi.Api.Minimal.Template.PostgreSql.5.2.0`n"
                Set-Content -LiteralPath $staleBasePath -Value $staleContent -NoNewline

                $correctedPath = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $staleBasePath -DockerComposeRoot $script:engineTokenComposeRoot

                $correctedPath | Should -Not -Be $staleBasePath -Because "the correction must land in a new derived file, not the source"
                (ReadValuesFromEnvFile $correctedPath)["DATABASE_TEMPLATE_PACKAGE"] | Should -Be "EdFi.Api.Minimal.Template.MsSql.5.2.0"

                # The source file must be untouched.
                (Get-Content -LiteralPath $staleBasePath -Raw) | Should -Be $staleContent
            }
        }

        Context "Resolve-RestoreTemplatePackageId engine-token forcing" {
            BeforeAll {
                # Other spec files (e.g. BootstrapEntryPointWorkflow.Tests.ps1) stub a fixture module
                # also named setup-database-template from a different path so InModuleScope stays
                # unambiguous, any stale copy is removed before the real module is imported.
                Get-Module -Name setup-database-template -All | Remove-Module -Force -ErrorAction SilentlyContinue
                Import-Module (Join-Path $script:sourceDockerComposeRoot "setup-database-template.psm1") -Force
            }

            It "forces a stale PostgreSql DATABASE_TEMPLATE_PACKAGE to MsSql when the resolved engine is mssql" {
                # Models a hand-crafted env file that sets DMS_DATASTORE=mssql directly (bypassing
                # Resolve-DatabaseEngineEnvironmentFile's overlay composition, which is what
                # normally keeps the engine segment correct) alongside a stale PostgreSql package id.
                InModuleScope setup-database-template {
                    $envValues = @{ DATABASE_TEMPLATE_PACKAGE = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" }

                    $resolved = Resolve-RestoreTemplatePackageId -EnvValues $envValues -DatabaseEngine "mssql" -RestoreTemplate "Populated"

                    $resolved | Should -Be "EdFi.Api.Populated.Template.MsSql.5.2.0" -Because "the resolved engine must win over a stale engine token so the restore does not later fail looking for a .bak under a PostgreSql package"
                }
            }

            It "leaves an already engine-consistent MsSql DATABASE_TEMPLATE_PACKAGE unchanged apart from the Minimal|Populated template swap" {
                InModuleScope setup-database-template {
                    $envValues = @{ DATABASE_TEMPLATE_PACKAGE = "EdFi.Api.Populated.Template.MsSql.6.1.0" }

                    $resolved = Resolve-RestoreTemplatePackageId -EnvValues $envValues -DatabaseEngine "mssql" -RestoreTemplate "Minimal"

                    $resolved | Should -Be "EdFi.Api.Minimal.Template.MsSql.6.1.0"
                }
            }

            It "leaves an already engine-consistent PostgreSql DATABASE_TEMPLATE_PACKAGE unchanged apart from the Minimal|Populated template swap" {
                InModuleScope setup-database-template {
                    $envValues = @{ DATABASE_TEMPLATE_PACKAGE = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" }

                    $resolved = Resolve-RestoreTemplatePackageId -EnvValues $envValues -DatabaseEngine "postgresql" -RestoreTemplate "Populated"

                    $resolved | Should -Be "EdFi.Api.Populated.Template.PostgreSql.5.2.0"
                }
            }

            It "rewrites the historical default package's engine segment to MsSql when DATABASE_TEMPLATE_PACKAGE is not set" {
                InModuleScope setup-database-template {
                    $resolved = Resolve-RestoreTemplatePackageId -EnvValues @{} -DatabaseEngine "mssql" -RestoreTemplate "Minimal"

                    $resolved | Should -Be "EdFi.Api.Minimal.Template.MsSql.5.2.0"
                }
            }
        }
    }

    Context "IDE workflow shapes (DMS-1153)" {
        # Helpers shared across IDE workflow tests

        function script:New-IdeWrapperFixture {
            param(
                [string]$StartScriptStub = "param([Parameter(ValueFromRemainingArguments)]\$rest)"
            )
            $tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms-ide-$([Guid]::NewGuid().ToString('N'))"
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1") -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose
            Copy-WrapperCompositionPrerequisites -DockerComposeRoot $tmpDockerCompose

            $bootstrapDir = Join-Path $tmpDockerCompose ".bootstrap"
            New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
            "{}" | Set-Content -LiteralPath (Join-Path $bootstrapDir "bootstrap-manifest.json") -Encoding utf8

            $StartScriptStub | Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8

            return [pscustomobject]@{
                TmpRoot          = $tmpRoot
                TmpDockerCompose = $tmpDockerCompose
                WrapperScript    = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"
            }
        }

        It "bootstrap-local-dms.ps1 declares -InfraOnly and -DmsBaseUrl" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            Test-Path -LiteralPath $wrapperScript | Should -BeTrue

            $content = Get-Content -LiteralPath $wrapperScript -Raw
            $paramBody = ([regex]::Match($content, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $params = [regex]::Matches($paramBody, '\$(\w+)') | ForEach-Object { $_.Groups[1].Value }

            $params | Should -Contain "InfraOnly" -Because "bootstrap-local-dms.ps1 must expose -InfraOnly (DMS-1153 AC)"
            $params | Should -Contain "DmsBaseUrl" -Because "bootstrap-local-dms.ps1 must expose -DmsBaseUrl (DMS-1153 AC)"
            $params | Should -Not -Contain "DataStoreId" -Because "-DataStoreId must never be public on the wrapper"
        }

        It "bootstrap-published-dms.ps1 does NOT declare -InfraOnly or -DmsBaseUrl (D5: IDE shapes are local-only)" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            Test-Path -LiteralPath $wrapperScript | Should -BeTrue

            $content = Get-Content -LiteralPath $wrapperScript -Raw
            $paramBody = ([regex]::Match($content, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
            $params = [regex]::Matches($paramBody, '\$(\w+)') | ForEach-Object { $_.Groups[1].Value }

            $params | Should -Not -Contain "InfraOnly" -Because "bootstrap-published-dms.ps1 must not gain IDE workflow params (story D5)"
            $params | Should -Not -Contain "DmsBaseUrl" -Because "bootstrap-published-dms.ps1 must not gain IDE workflow params (story D5)"
        }

        It "wrapper rejects -DmsBaseUrl without -InfraOnly before any phase invocation" {
            $fixture = New-IdeWrapperFixture
            $startProbe = Join-Path $fixture.TmpRoot "start-invoked.txt"
            "param([Parameter(ValueFromRemainingArguments)]\$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "start-local-dms.ps1") -Encoding utf8

            { & $fixture.WrapperScript -DmsBaseUrl "http://localhost:8080" } |
                Should -Throw -ExpectedMessage "*InfraOnly*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when -DmsBaseUrl is used without -InfraOnly"

            Remove-Item -LiteralPath $fixture.TmpRoot -Recurse -Force
        }

        It "wrapper rejects -LoadSeedData -InfraOnly without -DmsBaseUrl before any phase invocation" {
            # -InfraOnly without -DmsBaseUrl is terminal (stops before any DMS process).
            # -LoadSeedData needs a healthy DMS endpoint; combining these two without -DmsBaseUrl
            # would schedule seed delivery against a DMS that does not exist.
            $fixture = New-IdeWrapperFixture
            $startProbe = Join-Path $fixture.TmpRoot "start-invoked.txt"
            "param([Parameter(ValueFromRemainingArguments)]\$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "start-local-dms.ps1") -Encoding utf8

            { & $fixture.WrapperScript -InfraOnly -LoadSeedData } |
                Should -Throw -ExpectedMessage "*-DmsBaseUrl*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when -LoadSeedData -InfraOnly is missing -DmsBaseUrl"

            Remove-Item -LiteralPath $fixture.TmpRoot -Recurse -Force
        }

        It "wrapper -InfraOnly (primary shape): runs infra, configure, provision then stops — does NOT invoke DMS-only startup" {
            $fixture = New-IdeWrapperFixture
            $sequencePath = Join-Path $fixture.TmpRoot "sequence.txt"

            # Start stub writes which mode it was called in
            @"
param([switch] `$InfraOnly, [switch] `$DmsOnly, [switch] `$EnableConfig, [string] `$DmsBaseUrl, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
if (`$InfraOnly -and [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra' }
elseif (`$InfraOnly -and -not [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)) { Add-Content -LiteralPath '$sequencePath' -Value 'start-healthwait' }
elseif (`$DmsOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-dms' }
"@ | Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "start-local-dms.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'configure'; [pscustomobject]@{ SelectedDataStoreIds = [long[]]@(1); HasRouteQualifiedDataStores = `$false }" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "provision-dms-schema.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'seed'" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            & $fixture.WrapperScript -InfraOnly

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence | Should -Contain "start-infra" -Because "infra phase must run"
            $sequence | Should -Contain "configure" -Because "configure phase must run"
            $sequence | Should -Contain "provision" -Because "provision phase must run"
            $sequence | Should -Not -Contain "start-dms" -Because "-DmsOnly must NOT run in the -InfraOnly terminal shape"
            $sequence | Should -Not -Contain "start-healthwait" -Because "health-wait must NOT run without -DmsBaseUrl"
            $sequence | Should -Not -Contain "seed" -Because "seed must NOT run in the terminal pre-DMS shape"

            Remove-Item -LiteralPath $fixture.TmpRoot -Recurse -Force
        }

        It "wrapper -InfraOnly -DmsBaseUrl (continuation shape): does NOT pass -DmsBaseUrl to initial infra invocation, passes it only to health-wait" {
            $fixture = New-IdeWrapperFixture
            $sequencePath = Join-Path $fixture.TmpRoot "sequence.txt"

            # Start stub records whether DmsBaseUrl was present at each call point
            @"
param([switch] `$InfraOnly, [switch] `$DmsOnly, [switch] `$EnableConfig, [string] `$DmsBaseUrl, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
if (`$InfraOnly -and [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra-nodmsurl' }
elseif (`$InfraOnly -and -not [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)) { Add-Content -LiteralPath '$sequencePath' -Value "start-healthwait url=`$DmsBaseUrl" }
elseif (`$DmsOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-dms' }
"@ | Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "start-local-dms.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'configure'; [pscustomobject]@{ SelectedDataStoreIds = [long[]]@(1); HasRouteQualifiedDataStores = `$false }" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "provision-dms-schema.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'seed'" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            & $fixture.WrapperScript -InfraOnly -DmsBaseUrl "http://localhost:8080"

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence | Should -Contain "start-infra-nodmsurl" -Because "initial infra invocation must NOT receive -DmsBaseUrl"
            $sequence | Should -Contain "configure" -Because "configure phase must run"
            $sequence | Should -Contain "provision" -Because "provision phase must run"
            $sequence | Should -Contain "start-healthwait url=http://localhost:8080" -Because "health-wait invocation must receive -DmsBaseUrl after provision"
            $sequence | Should -Not -Contain "start-dms" -Because "-DmsOnly must NOT run in the IDE continuation shape"
            $sequence | Should -Not -Contain "seed" -Because "seed must NOT run when -LoadSeedData is absent"

            Remove-Item -LiteralPath $fixture.TmpRoot -Recurse -Force
        }

        It "wrapper -InfraOnly -DmsBaseUrl -LoadSeedData: forwards -DmsBaseUrl to seed phase" {
            $fixture = New-IdeWrapperFixture
            $sequencePath = Join-Path $fixture.TmpRoot "sequence.txt"
            $seedArgsPath = Join-Path $fixture.TmpRoot "seed-args.txt"

            @"
param([switch] `$InfraOnly, [switch] `$DmsOnly, [string] `$DmsBaseUrl, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
if (`$InfraOnly -and [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra' }
elseif (`$InfraOnly -and -not [string]::IsNullOrWhiteSpace(`$DmsBaseUrl)) { Add-Content -LiteralPath '$sequencePath' -Value 'start-healthwait' }
"@ | Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "start-local-dms.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'configure'; [pscustomobject]@{ SelectedDataStoreIds = [long[]]@(1); HasRouteQualifiedDataStores = `$false }" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "provision-dms-schema.ps1") -Encoding utf8

            # Seed stub captures its arguments
            @"
param([string] `$DmsBaseUrl, [string] `$IdentityProvider, [long[]] `$DataStoreId, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value 'seed'
Set-Content -LiteralPath '$seedArgsPath' -Value "url=`$DmsBaseUrl ids=`$(`$DataStoreId -join ',')" -Encoding utf8
"@ | Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            # Stage a fake bootstrap manifest so -LoadSeedData preflight passes
            '{"schema":{"selectionMode":"Standard"}}' |
                Set-Content -LiteralPath (Join-Path $fixture.TmpDockerCompose ".bootstrap/bootstrap-manifest.json") -Encoding utf8

            & $fixture.WrapperScript -InfraOnly -DmsBaseUrl "http://localhost:8080" -LoadSeedData

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence | Should -Contain "seed" -Because "seed phase must run when -LoadSeedData is set in the continuation shape"

            $seedArgs = Get-Content -LiteralPath $seedArgsPath -Raw
            $seedArgs | Should -Match "url=http://localhost:8080" -Because "-DmsBaseUrl must be forwarded to load-dms-seed-data.ps1"

            Remove-Item -LiteralPath $fixture.TmpRoot -Recurse -Force
        }
    }

    Context "orchestrator call shape (regression guards)" {
        BeforeAll {
            $script:seedScriptPath = Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1"
            $script:seedScriptContent = Get-Content -LiteralPath $script:seedScriptPath -Raw
        }

        It "every Invoke-SeedTierLoad call site passes -XsdDirectory and never -Manifest" {
            # Regression guard for finding #1: the single-instance branch previously called
            # Invoke-SeedTierLoad with -Manifest $manifest, which is not a parameter on the function
            # and would have crashed at PowerShell parameter binding. Multi-instance branch did it
            # correctly. This test asserts both branches share the same call shape so they cannot
            # drift apart silently again.
            # Filter to actual call sites only; the function definition looks like
            # "Invoke-SeedTierLoad {", whereas a call site is "Invoke-SeedTierLoad `" (backtick
            # line-continuation followed by -Tier ...).
            $invocations = ($script:seedScriptContent -split '(?=Invoke-SeedTierLoad)') |
                Where-Object { $_ -match '^Invoke-SeedTierLoad\s+`' }
            $invocations.Count | Should -BeGreaterOrEqual 2 -Because "expected at least two call sites (single-instance + school-year branches)"

            foreach ($block in $invocations) {
                $callBlock = ($block -split '\r?\n\s*\r?\n')[0]
                $callBlock | Should -Match '-XsdDirectory\s+\$\w+' -Because "Invoke-SeedTierLoad call site must pass -XsdDirectory: $callBlock"
                $callBlock | Should -Not -Match '-Manifest\s+\$\w+' -Because "no Invoke-SeedTierLoad call site may pass -Manifest (not a declared parameter): $callBlock"
            }
        }

        It "preflights seed workspace materialization before env, health, or CMS credential work" {
            $preflightIndex = $script:seedScriptContent.IndexOf("Preflighting seed workspace materialization")
            $envIndex = $script:seedScriptContent.IndexOf("# --- Step 1: Env resolution ---")
            $healthIndex = $script:seedScriptContent.IndexOf("Wait-DmsHealthy -DmsBaseUrl")
            $credentialIndex = $script:seedScriptContent.LastIndexOf("New-SeedLoaderCredentials")

            $preflightIndex | Should -BeGreaterThan -1
            $preflightIndex | Should -BeLessThan $envIndex -Because "bad seed paths should fail before env-dependent service checks"
            $preflightIndex | Should -BeLessThan $healthIndex -Because "bad seed paths should fail before DMS health checks"
            $preflightIndex | Should -BeLessThan $credentialIndex -Because "bad seed paths should fail before creating SeedLoader CMS state"
        }

        It "SchoolYearType precondition POSTs to /data/ed-fi/schoolYearTypes (no /v3/)" {
            # Regression guard for finding #2: DMS Discovery exposes /data, not /data/v3/.
            $script:seedScriptContent | Should -Match '/data/ed-fi/schoolYearTypes'
            $script:seedScriptContent | Should -Not -Match '/data/v3/ed-fi/schoolYearTypes' -Because "ODS/API legacy /data/v3/ shape does not exist in DMS"
        }

        It "SchoolYearType precondition CurrentYear default is not a hard-coded literal" {
            # Regression guard for finding #7: a hard-coded year silently rots; use Get-CurrentSchoolYear.
            $script:seedScriptContent | Should -Match '\[int\]\$CurrentYear\s*=\s*\(Get-CurrentSchoolYear\)' -Because "CurrentYear default must derive from Get-CurrentSchoolYear, not a literal"
        }

        It "load-dms-seed-data.ps1 does not probe /{year}/health (DMS maps /health only at root)" {
            # Regression guard for the Round-15 regression we shipped and then reverted: a per-year
            # preflight that appended /health to a route-qualified base URL silently stalled every
            # -SchoolYear / -SchoolYearRange run for the full Wait-DmsHealthy timeout. DMS registers
            # /health only at the unqualified root (HealthCheckEndpointModule.cs), so any per-year
            # health probe must use a different endpoint or be omitted.
            $script:seedScriptContent | Should -Not -Match 'Wait-DmsHealthy\s+-DmsBaseUrl\s+"?\$resolvedDmsBaseUrl/\$\w*[Yy]ear' -Because "DMS /health is not route-qualified; appending /{year} to the health probe stalls -SchoolYear runs"
        }

        It "load-dms-seed-data.ps1 composes DMS data URLs via Resolve-DmsRouteUrl (tenant + qualifiers)" {
            # Regression guard: a multi-tenant local stack expects DMS data writes at
            # `{base}/{tenant}/.../data/...` (CoreEndpointModule.BuildRoutePattern). Earlier rounds
            # concatenated `"$resolvedDmsBaseUrl/$year"` directly, which silently dropped the
            # tenant segment and 404'd every multi-tenant seed run. Both the per-year and the
            # single-instance branches must route through the helper instead.
            $script:seedScriptContent | Should -Match 'Resolve-DmsRouteUrl\s+`?\s*-BaseUrl\s+\$resolvedDmsBaseUrl\s+`?\s*-Tenant\s+\$tenant\s+`?\s*-RouteQualifierValues\s+@\(\[string\]\$year\)' -Because "per-year branch must compose URL via Resolve-DmsRouteUrl with tenant + year"
            $script:seedScriptContent | Should -Match 'Resolve-DmsRouteUrl\s+-BaseUrl\s+\$resolvedDmsBaseUrl\s+-Tenant\s+\$tenant' -Because "single-instance branch must compose URL via Resolve-DmsRouteUrl with tenant"
            $script:seedScriptContent | Should -Not -Match '\$perYearBase\s*=\s*"\$resolvedDmsBaseUrl/\$year"' -Because "per-year base must not bypass the URL composer; tenant segment would be dropped"
        }
    }

    Context "round-2 regression guards" {
        BeforeAll {
            $script:buildDmsContent = Get-Content -LiteralPath (Join-Path $script:sourceRepoRoot "build-dms.ps1") -Raw
            $script:localWrapperContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1") -Raw
            $script:publishedWrapperContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1") -Raw
            $script:wrapperModuleContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Raw
        }

        It "build-dms.ps1 E2ETest rejects -LoadSeedData instead of running a direct-SQL template seed" {
            # E2ETest resets and provisions E2E_DATABASE_NAME after stack startup. The old
            # direct-SQL database-template seed targeted POSTGRES_DB_NAME and was not a valid
            # signal for the provisioned E2E database.
            $script:buildDmsContent | Should -Match 'E2ETest -LoadSeedData is not supported after legacy backend removal' -Because "E2ETest must fail fast when seed data is requested"
            $script:buildDmsContent | Should -Not -Match 'setup-database-template\.psm1' -Because "build-dms.ps1 E2ETest must not import the legacy direct-SQL seed module"
            $script:buildDmsContent | Should -Not -Match '(?m)^\s*LoadSeedData\s+-EnvironmentFile' -Because "build-dms.ps1 E2ETest must not invoke the legacy direct-SQL seed module"
            $script:buildDmsContent | Should -Not -Match 'start-local-dms\.ps1[^\n]+-LoadSeedData' -Because "build-dms.ps1 must not forward -LoadSeedData to start-local-dms.ps1"
        }

        It "build-dms.ps1 E2E local path must NOT call load-dms-seed-data.ps1 (manifest is guaranteed absent)" {
            # The API-based load-dms-seed-data.ps1 hard-requires a staged bootstrap manifest, but
            # Start-DockerEnvironment tears down with -RemoveBootstrap in the same invocation, so a
            # manifest can never be present when the seed step runs. Routing this flow to
            # load-dms-seed-data.ps1 would make build-dms.ps1 -LoadSeedData fail unconditionally
            # with 'Bootstrap manifest not found'.
            # Match the invocation form (./load-dms-seed-data.ps1) only; explanatory comments may
            # still reference the script by name.
            $script:buildDmsContent | Should -Not -Match '\./load-dms-seed-data\.ps1' -Because "build-dms.ps1 must not route seed loading through the manifest-requiring API path while its teardown removes the manifest"
        }

        It "build-dms.ps1 E2ETest published-image path does not forward -LoadSeedData" {
            # build-dms.ps1 rejects E2ETest -LoadSeedData before choosing local vs published
            # image startup. start-published-dms.ps1 may retain the switch for direct use.
            $script:buildDmsContent | Should -Not -Match 'start-published-dms\.ps1[^\n]+-LoadSeedData' -Because "build-dms.ps1 E2ETest must not forward -LoadSeedData to start-published-dms.ps1"
        }

        It "shared wrapper module normalizes -SeedDataPath against caller CWD before Push-Location" {
            # Regression guard for round-2 finding #2: a relative -SeedDataPath supplied from the
            # repo root would otherwise resolve against eng/docker-compose/ after the Push-Location.
            # Both wrappers share bootstrap-wrapper.psm1, so the guard checks the shared module.
            $script:wrapperModuleContent | Should -Match "(?s)PSBoundParameters\.ContainsKey\('SeedDataPath'\).*GetFullPath.*Get-Location.*Push-Location\s+\`$PSScriptRoot" -Because "the SeedDataPath normalization must come before the Push-Location in the shared module"
        }

        It "shared wrapper module normalizes -EnvironmentFile against caller CWD before Push-Location" {
            # Regression guard: a relative -EnvironmentFile (e.g. eng/docker-compose/.env.e2e supplied
            # from the repo root) would otherwise be resolved by Get-EffectiveBootstrapEnvFile against
            # eng/docker-compose/ after Push-Location, producing eng/docker-compose/eng/docker-compose/...
            # and failing derived-env materialization before any phase starts.
            $script:wrapperModuleContent | Should -Match "(?s)PSBoundParameters\.ContainsKey\('EnvironmentFile'\).*GetFullPath.*Get-Location.*Push-Location\s+\`$PSScriptRoot" -Because "the EnvironmentFile normalization must come before the Push-Location in the shared module"
        }

        It "shared wrapper module reuses the validated SchoolYearRange parse for seed args" {
            # Regression guard: the wrapper validates -SchoolYearRange once at entry (captures
            # $rangeStartYear/$rangeEndYear), and the seed-args block must reuse those values
            # instead of re-parsing the regex - single source of truth for the validated range.
            $script:wrapperModuleContent | Should -Match '\$seedArgs\.SchoolYear\s*=\s*@\(\$rangeStartYear\.\.\$rangeEndYear\)' -Because "seed args must reuse the captured range bounds rather than re-parsing -SchoolYearRange"
        }

        It "New-SeedLoaderCredentials accepts -AdminToken and the orchestrator forwards the captured token" {
            # Regression guard for the recurring duplicate-Add-CmsClient finding: the orchestrator
            # at load-dms-seed-data.ps1 already obtains a CMS admin token at Step 4 and must forward
            # it to New-SeedLoaderCredentials so the helper skips its internal Add-CmsClient +
            # Get-CmsToken round-trip.
            $mgmtContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "../Dms-Management.psm1") -Raw
            $mgmtContent | Should -Match '\[string\]\$AdminToken\s*=\s*""' -Because "New-SeedLoaderCredentials must declare an optional -AdminToken parameter"
            $mgmtContent | Should -Match '(?s)if\s*\(\s*-not\s+\[string\]::IsNullOrWhiteSpace\(\$AdminToken\)\s*\)\s*\{\s*\$token\s*=\s*\$AdminToken' -Because "supplied -AdminToken must short-circuit the internal Add-CmsClient + Get-CmsToken call"

            $script:seedScriptContent | Should -Match 'New-SeedLoaderCredentials[\s\S]+?-AdminToken\s+\$cmsToken' -Because "orchestrator must forward the existing $cmsToken into New-SeedLoaderCredentials"
        }

        It "seed-delivery pinned data-standard tag requires inventory review when changed" {
            # The SeedLoader claims fixture and default EdOrg envelope are hand-curated against the
            # v5.2.0 Sample XML inventory. A bump here must intentionally update those inventories
            # in the same change.
            $script:seedScriptContent | Should -Match '\$script:DataStandardRefTag\s*=\s*"v5\.2\.0"' -Because "DataStandardRefTag changes require regenerating the SeedLoader claims inventory and EdOrg envelope"
        }

        It "SeedLoader default EdOrg envelope remains pinned to the v5.2.0 Sample XML top-level EdOrgs" {
            $mgmtContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "../Dms-Management.psm1") -Raw
            $mgmtContent | Should -Match '\[long\[\]\]\$EducationOrganizationIds\s*=\s*@\(\[long\]255950,\s*\[long\]255901,\s*\[long\]255901001,\s*\[long\]255901044,\s*\[long\]255901107,\s*\[long\]19,\s*\[long\]19255901,\s*\[long\]6000203\)' -Because "EdOrg envelope changes must be reviewed against the pinned Sample XML inventory"
        }

        It "Wait-CmsClientAvailable defaults to a 30-second cold-stack budget" {
            $mgmtContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "../Dms-Management.psm1") -Raw
            $mgmtContent | Should -Match '\[int\]\$MaxAttempts\s*=\s*60' -Because "60 attempts x 500ms gives the intended 30-second default wait budget"
            $mgmtContent | Should -Match '\[int\]\$DelayMs\s*=\s*500'
        }

        It "build-dms.ps1 Start-BootstrapDockerEnvironment rejects -RestoreTemplate combined with -LoadSeedData before any Docker work" {
            # Mirrors the mutual-exclusion check the bootstrap wrapper enforces at its own entry point;
            # Start-BootstrapDockerEnvironment must reject the same combination before DockerBuild or
            # Stop-DockerEnvironment run.
            $throwIndex = $script:buildDmsContent.IndexOf('throw "-RestoreTemplate and -LoadSeedData are mutually exclusive.')
            $throwIndex | Should -BeGreaterThan -1 -Because "Start-BootstrapDockerEnvironment must reject -RestoreTemplate + -LoadSeedData with the same contract message as the bootstrap wrapper"

            $functionStart = $script:buildDmsContent.IndexOf("function Start-BootstrapDockerEnvironment")
            $functionStart | Should -BeGreaterThan -1
            $dockerBuildIndex = $script:buildDmsContent.IndexOf("Invoke-Step { DockerBuild }", $functionStart)
            # Search for the actual call site (line-continued with a backtick), not the explanatory
            # comment above the throw, which also mentions "Stop-DockerEnvironment" by name.
            $stopEnvironmentIndex = $script:buildDmsContent.IndexOf("Stop-DockerEnvironment ``", $functionStart)

            $throwIndex | Should -BeGreaterThan $functionStart
            $throwIndex | Should -BeLessThan $dockerBuildIndex -Because "the mutual-exclusion check must run before any Docker build work"
            $throwIndex | Should -BeLessThan $stopEnvironmentIndex -Because "the mutual-exclusion check must run before stopping the existing Docker environment"

            # The check must not be conditioned on -DatabaseEngine: the same throw applies to both engines.
            $checkLine = ($script:buildDmsContent -split "`n") | Where-Object { $_ -match 'if \(\$LoadSeedData -and \$RestoreTemplate\)' } | Select-Object -First 1
            $checkLine | Should -Not -BeNullOrEmpty
            $checkLine | Should -Not -Match "DatabaseEngine" -Because "the mutual-exclusion gating must be identical for both database engines"
        }

        It "build-dms.ps1 Start-BootstrapDockerEnvironment forwards -RestoreTemplate, -DatabaseEngine, and -DataStandardVersion into the bootstrap wrapper splat" {
            $script:buildDmsContent | Should -Match 'if\s*\(\$RestoreTemplate\)\s*\{\s*\$bootstrapArgs\.RestoreTemplate\s*=\s*\$RestoreTemplate\s*\}' -Because "-RestoreTemplate must be forwarded to the bootstrap wrapper only when supplied"
            $script:buildDmsContent | Should -Match 'if\s*\(\$DatabaseEngine\)\s*\{\s*\$bootstrapArgs\.DatabaseEngine\s*=\s*\$DatabaseEngine\s*\}' -Because "-DatabaseEngine must be forwarded to the bootstrap wrapper only when supplied"
            $script:buildDmsContent | Should -Match 'if\s*\(\$DataStandardVersionSupplied\)\s*\{\s*\$bootstrapArgs\.DataStandardVersion\s*=\s*\$DataStandardVersion\s*\}' -Because "-DataStandardVersion must be forwarded to the bootstrap wrapper only when the caller explicitly supplied it"
        }
    }
}
