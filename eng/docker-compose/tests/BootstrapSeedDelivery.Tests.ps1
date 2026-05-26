# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

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
        # Initialize-CoreSeedSource, New-SeedWorkspace, Get-SeedFileTargetName, Resolve-SeedTargetInstances,
        # Get-SeedXsdDirectory, Invoke-BulkLoadClient, etc.). The orchestration block is guarded
        # against dot-sourcing via the InvocationName check at the bottom of the script.
        Import-Module "$script:sourceDockerComposeRoot/bootstrap-manifest.psm1" -Force
        . "$script:sourceDockerComposeRoot/load-dms-seed-data.ps1"
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

        It "rejects both InstanceId and SchoolYear when supplied together" {
            # Regression: passing -InstanceId @(7) -SchoolYear @(2024) would short-circuit instance
            # selection to id 7 but the orchestrator loop still iterated $SchoolYear, building
            # /{year} URLs that routed to whichever instance had the matching route context — not
            # instance 7. The credentials and the URL silently disagreed.
            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }

            {
                Assert-SeedSelectionInputs `
                    -Manifest $manifest `
                    -InstanceId @(7) `
                    -SchoolYear @(2024)
            } | Should -Throw -ExpectedMessage "*-InstanceId and -SchoolYear are mutually exclusive*"

            # Each in isolation is still accepted
            { Assert-SeedSelectionInputs -Manifest $manifest -InstanceId @(7) } | Should -Not -Throw
            { Assert-SeedSelectionInputs -Manifest $manifest -SchoolYear @(2024) } | Should -Not -Throw
        }

        It "rejects multiple -InstanceId values because the unqualified path cannot disambiguate" {
            # Regression: -InstanceId @(1,2) flowed through Resolve-SeedTargetInstances as a 2-id array,
            # New-SeedLoaderCredentials minted one credential authorized for both, and the orchestrator's
            # non-SchoolYear branch issued a single bulk-load pass against the unqualified base URL.
            # DMS cannot pick between two authorized instances without a route qualifier in the URL.
            $manifest = @{
                schema = @{ selectionMode = "Standard"; selectedExtensions = @() }
                seed   = @{ extensionNamespacePrefixes = @() }
            }

            $thrown = $null
            try {
                Assert-SeedSelectionInputs -Manifest $manifest -InstanceId @(1, 2)
            }
            catch {
                $thrown = $_.Exception.Message
            }
            $thrown | Should -Not -BeNullOrEmpty
            $thrown | Should -Match "Multiple -InstanceId values"
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
            # ADescriptor) — the sample-side DiagnosisDescriptor overwrote, not appended.
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
    }

    Context "seed workspace materialization" {
        It "stages XML files into a flat deterministic .bootstrap/seed/data directory" {
            $sourceDir = New-TestDirectory
            $subDir = Join-Path $sourceDir "descriptors"
            New-Item -ItemType Directory -Path $subDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "SchoolYearTypes.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $subDir "GradeDescriptor.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($sourceDir)

            $stagedItems = @(Get-ChildItem -LiteralPath $workspace.DataDirectory)
            $stagedItems | Should -Not -BeNullOrEmpty

            $subDirs = @($stagedItems | Where-Object { $_.PSIsContainer })
            $subDirs.Count | Should -Be 0 -Because "materialization must produce a flat directory"

            $files = @($stagedItems | Where-Object { -not $_.PSIsContainer })
            $files.Count | Should -Be 2

            Remove-Item -LiteralPath $sourceDir -Recurse -Force
        }

        It "keeps package-backed seed source outside the disposable BulkLoadClient workspace" {
            $tmpRoot = New-TestDirectory
            $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
            $sourceDir = Join-Path $bootstrapRoot "seed-source/minimal"
            New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
            "<root />" | Set-Content -LiteralPath (Join-Path $sourceDir "SchoolYearTypes.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $bootstrapRoot `
                -SourceDirectories @($sourceDir)

            $workspace.StagedFiles.Count | Should -Be 1
            Test-Path -LiteralPath $sourceDir -PathType Container | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $sourceDir "SchoolYearTypes.xml") | Should -BeTrue

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "generates collision-safe file names using source key and path hash" {
            $srcA = New-TestDirectory
            $srcB = New-TestDirectory
            "<root />" | Set-Content -LiteralPath (Join-Path $srcA "Common.xml") -Encoding utf8
            "<root />" | Set-Content -LiteralPath (Join-Path $srcB "Common.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($srcA, $srcB)

            $files = @(Get-ChildItem -LiteralPath $workspace.DataDirectory -File)
            $files.Count | Should -Be 2

            $names = $files | ForEach-Object Name
            $names[0] | Should -Not -Be $names[1]

            $keyA = Split-Path -Leaf $srcA
            $keyB = Split-Path -Leaf $srcB
            ($names | Where-Object { $_ -match [regex]::Escape($keyA) }).Count | Should -Be 1
            ($names | Where-Object { $_ -match [regex]::Escape($keyB) }).Count | Should -Be 1

            Remove-Item -LiteralPath $srcA -Recurse -Force
            Remove-Item -LiteralPath $srcB -Recurse -Force
        }

        It "fails before BulkLoadClient when deterministic names would collide" {
            $srcA = New-TestDirectory
            "<root />" | Set-Content -LiteralPath (Join-Path $srcA "Descriptor.xml") -Encoding utf8

            {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($srcA, $srcA)
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
            "<root />" | Set-Content -LiteralPath (Join-Path $srcA "SchoolYearTypes.xml") -Encoding utf8

            $workspace = New-SeedWorkspace `
                -BootstrapRoot $script:repo.BootstrapRoot `
                -SourceDirectories @($srcA)
            Test-Path -LiteralPath $workspace.DataDirectory | Should -BeTrue

            $seedRoot = Join-Path $script:repo.BootstrapRoot "seed"
            Remove-Item -LiteralPath $seedRoot -Recurse -Force
            Test-Path -LiteralPath $workspace.DataDirectory | Should -BeFalse

            try {
                New-SeedWorkspace `
                    -BootstrapRoot $script:repo.BootstrapRoot `
                    -SourceDirectories @($srcA, $srcA)
            }
            catch {
                # Expected — collision was detected
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
                    [pscustomobject]@{ id = 1; name = "EdFiSandbox" },
                    [pscustomobject]@{ id = 2; name = "BootstrapDescriptorsandEdOrgs" }
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
                    [pscustomobject]@{ id = 1; name = "EdFiSandbox" },
                    [pscustomobject]@{ id = 7; name = "SeedLoader" }
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
            # Static content check — smoke-test helpers must not be wired into seed delivery.
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

        It "Remove-CmsApplication builds the DELETE URI as <CmsUrl>/v2/applications/<id>" {
            Mock -ModuleName Dms-Management -CommandName Invoke-RestMethod -MockWith { return $null }

            Remove-CmsApplication `
                -CmsUrl "http://localhost:8081" `
                -ApplicationId 42 `
                -AccessToken "fake-token"

            Should -Invoke `
                -ModuleName Dms-Management `
                -CommandName Invoke-RestMethod `
                -ParameterFilter { $Uri -eq "http://localhost:8081/v2/applications/42" -and $Method -eq "Delete" } `
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
                -ParameterFilter { $Headers["Tenant"] -eq "edfi-tenant" -and $Uri -eq "http://localhost:8081/v2/applications/7" } `
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

        It "Write-DerivedEnvFile filters SCHEMA_PACKAGES entries by name (Sample/Homograph dropped)" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
SCHEMA_PACKAGES='[
  { "version": "1.0", "name": "EdFi.DataStandard52.ApiSchema" },
  { "version": "1.0", "name": "EdFi.Sample.ApiSchema", "extensionName": "Sample" },
  { "version": "1.0", "name": "EdFi.Homograph.ApiSchema", "extensionName": "Homograph" },
  { "version": "1.0", "name": "EdFi.TPDM.ApiSchema" }
]'
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                Write-DerivedEnvFile `
                    -BaseEnvironmentFile $base `
                    -TargetPath $derived `
                    -SchemaPackageExclusions @("EdFi.Sample.ApiSchema", "EdFi.Homograph.ApiSchema")

                $content = Get-Content -LiteralPath $derived -Raw
                $content | Should -Match "EdFi.DataStandard52.ApiSchema"
                $content | Should -Match "EdFi.TPDM.ApiSchema"
                $content | Should -Not -Match "EdFi.Sample.ApiSchema"
                $content | Should -Not -Match "EdFi.Homograph.ApiSchema"
                # The SCHEMA_PACKAGES key must still exist as quoted JSON
                $content | Should -Match "(?ms)^SCHEMA_PACKAGES='\["
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "Write-DerivedEnvFile is idempotent across reruns (same input → same output)" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
FAILURE_RATIO=0.01
SCHEMA_PACKAGES='[
  { "version": "1.0", "name": "EdFi.DataStandard52.ApiSchema" },
  { "version": "1.0", "name": "EdFi.Sample.ApiSchema" }
]'
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                Write-DerivedEnvFile -BaseEnvironmentFile $base -TargetPath $derived `
                    -KeyOverrides @{ FAILURE_RATIO = "0.95" } `
                    -SchemaPackageExclusions @("EdFi.Sample.ApiSchema")
                $first = Get-Content -LiteralPath $derived -Raw

                Write-DerivedEnvFile -BaseEnvironmentFile $base -TargetPath $derived `
                    -KeyOverrides @{ FAILURE_RATIO = "0.95" } `
                    -SchemaPackageExclusions @("EdFi.Sample.ApiSchema")
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

        It "Resolve-BootstrapDerivedEnv with -FilterSampleHomograph filters Sample+Homograph" {
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
FAILURE_RATIO=0.01
SCHEMA_PACKAGES='[
  { "version": "1.0", "name": "EdFi.DataStandard52.ApiSchema" },
  { "version": "1.0", "name": "EdFi.Sample.ApiSchema" },
  { "version": "1.0", "name": "EdFi.Homograph.ApiSchema" },
  { "version": "1.0", "name": "EdFi.TPDM.ApiSchema" }
]'
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                $result = Resolve-BootstrapDerivedEnv -BaseEnvironmentFile $base -DerivedTargetPath $derived -FilterSampleHomograph
                $result | Should -Be $derived

                $content = Get-Content -LiteralPath $derived -Raw
                $content | Should -Match "(?m)^FAILURE_RATIO=0\.95$"
                $content | Should -Not -Match "FAILURE_RATIO=0\.01"
                $content | Should -Match "EdFi.DataStandard52.ApiSchema"
                $content | Should -Match "EdFi.TPDM.ApiSchema"
                $content | Should -Not -Match "EdFi.Sample.ApiSchema"
                $content | Should -Not -Match "EdFi.Homograph.ApiSchema"
                $content | Should -Match "(?m)^LOG_LEVEL=DEBUG$"
            }
            finally {
                Remove-Item -LiteralPath $base -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue
            }
        }

        It "Resolve-BootstrapDerivedEnv without -FilterSampleHomograph retains Sample+Homograph" {
            # Round-2 finding #3: when a custom -SeedDataPath is supplied, the wrappers do not pass
            # -FilterSampleHomograph, and the developer's XML must be able to reference Sample or
            # Homograph resources against the running API.
            $base = Join-Path ([System.IO.Path]::GetTempPath()) "base-$([Guid]::NewGuid().ToString('N')).env"
            $derived = Join-Path ([System.IO.Path]::GetTempPath()) "derived-$([Guid]::NewGuid().ToString('N')).env"
            @"
LOG_LEVEL=DEBUG
FAILURE_RATIO=0.01
SCHEMA_PACKAGES='[
  { "version": "1.0", "name": "EdFi.DataStandard52.ApiSchema" },
  { "version": "1.0", "name": "EdFi.Sample.ApiSchema" },
  { "version": "1.0", "name": "EdFi.Homograph.ApiSchema" },
  { "version": "1.0", "name": "EdFi.TPDM.ApiSchema" }
]'
"@ | Set-Content -LiteralPath $base -Encoding utf8
            try {
                $result = Resolve-BootstrapDerivedEnv -BaseEnvironmentFile $base -DerivedTargetPath $derived
                $result | Should -Be $derived

                $content = Get-Content -LiteralPath $derived -Raw
                $content | Should -Match "(?m)^FAILURE_RATIO=0\.95$" -Because "circuit-breaker override must still apply"
                $content | Should -Match "EdFi.Sample.ApiSchema" -Because "custom-path callers must retain the full schema surface"
                $content | Should -Match "EdFi.Homograph.ApiSchema" -Because "custom-path callers must retain the full schema surface"
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
            # service names (dms-config-service, dms-keycloak) would be unreachable.
            $envValues = ReadValuesFromEnvFile -EnvironmentFile (Join-Path $script:sourceDockerComposeRoot ".env.example")

            $selfContained = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider "self-contained"
            $selfContained | Should -Match '^http://localhost:\d+/'
            $selfContained | Should -Not -Match 'dms-config-service'

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
        }

        It "selects DMS instances via explicit InstanceId, SchoolYear matching, or single auto-select" {
            # Explicit InstanceId now does a CMS lookup so it can verify the instance is route-unqualified
            # (see "rejects -InstanceId targeting a route-qualified instance" below).
            function script:Get-DmsInstances {
                @(
                    [pscustomobject]@{ id = [long]42; instanceName = "A"; dmsInstanceRouteContexts = @() },
                    [pscustomobject]@{ id = [long]99; instanceName = "B"; dmsInstanceRouteContexts = @() }
                )
            }
            try {
                $result = Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" -InstanceId @(42, 99)
                $result.InstanceIds | Should -Be @([long]42, [long]99)
            }
            finally {
                Remove-Item function:script:Get-DmsInstances -ErrorAction SilentlyContinue
            }

            # SchoolYear matching: shadow Get-DmsInstances with a script-scope function so the
            # dot-sourced Resolve-SeedTargetInstances picks up the test stub via standard lookup.
            function script:Get-DmsInstances {
                @(
                    [pscustomobject]@{
                        id = [long]7
                        instanceName = "Year 2024"
                        dmsInstanceRouteContexts = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" }
                        )
                    },
                    [pscustomobject]@{
                        id = [long]8
                        instanceName = "Year 2025"
                        dmsInstanceRouteContexts = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2025" }
                        )
                    }
                )
            }
            try {
                $result = Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" -SchoolYear @(2024)
                $result.InstanceIds | Should -Be @([long]7)

                # Single instance auto-selects
                function script:Get-DmsInstances {
                    @([pscustomobject]@{ id = [long]5; instanceName = "Single"; dmsInstanceRouteContexts = @() })
                }
                $result = Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused"
                $result.InstanceIds | Should -Be @([long]5)
            }
            finally {
                Remove-Item function:script:Get-DmsInstances -ErrorAction SilentlyContinue
            }
        }

        It "rejects -InstanceId targeting a route-qualified instance or an unknown id" {
            # DMS rejects requests whose URL qualifier count doesn't match the instance's route
            # contexts; -InstanceId alone can't produce the qualifier values, so the seed phase must
            # surface this at validation rather than at the first BulkLoadClient POST.
            function script:Get-DmsInstances {
                @(
                    [pscustomobject]@{
                        id = [long]11
                        instanceName = "Route-qualified"
                        dmsInstanceRouteContexts = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2026" }
                        )
                    },
                    [pscustomobject]@{
                        id = [long]12
                        instanceName = "Plain"
                        dmsInstanceRouteContexts = @()
                    }
                )
            }
            try {
                { Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" -InstanceId @(11) } |
                    Should -Throw -ExpectedMessage "*route context*schoolYear*"
                { Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" -InstanceId @(999) } |
                    Should -Throw -ExpectedMessage "*was not found in CMS*"

                # Plain instance still resolves
                $result = Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" -InstanceId @(12)
                $result.InstanceIds | Should -Be @([long]12)
            }
            finally {
                Remove-Item function:script:Get-DmsInstances -ErrorAction SilentlyContinue
            }
        }

        It "rejects auto-select when the single instance carries a route context" {
            # Symmetric to the explicit -InstanceId route-qualified rejection: a route-qualified
            # instance cannot be auto-selected because the orchestrator's single-instance branch
            # posts to {base}[/{tenant}] without composing the required qualifier segments.
            function script:Get-DmsInstances {
                @(
                    [pscustomobject]@{
                        id = [long]42
                        instanceName = "Year 2024"
                        dmsInstanceRouteContexts = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" }
                        )
                    }
                )
            }
            try {
                { Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" } |
                    Should -Throw -ExpectedMessage "*Single DMS instance*route context*schoolYear*"
            }
            finally {
                Remove-Item function:script:Get-DmsInstances -ErrorAction SilentlyContinue
            }
        }

        It "fails when no selector resolves or multiple instances match without explicit selection" {
            try {
                # Zero instances — no selector
                function script:Get-DmsInstances { @() }
                { Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" } | Should -Throw -ExpectedMessage "*No DMS instances*"

                # Multiple instances — no selector
                function script:Get-DmsInstances {
                    @(
                        [pscustomobject]@{ id = [long]1; instanceName = "A"; dmsInstanceRouteContexts = @() },
                        [pscustomobject]@{ id = [long]2; instanceName = "B"; dmsInstanceRouteContexts = @() }
                    )
                }
                { Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" } | Should -Throw -ExpectedMessage "*Multiple DMS instances*"

                # SchoolYear with no matching instance
                { Resolve-SeedTargetInstances -CmsUrl "http://unused" -AccessToken "unused" -SchoolYear @(2099) } | Should -Throw -ExpectedMessage "*No DMS instance found*schoolYear*2099*"
            }
            finally {
                Remove-Item function:script:Get-DmsInstances -ErrorAction SilentlyContinue
            }
        }

        It "orchestrator reads CONFIG_SERVICE_TENANT from env and forwards it to Resolve-SeedTargetInstances and New-SeedLoaderCredentials" {
            # Regression: a prior implementation never read CONFIG_SERVICE_TENANT from envValues,
            # so in multi-tenant local stacks the seed flow could not see tenant-scoped instances
            # and created seed credentials in the wrong tenant context.
            $scriptPath = Join-Path $script:sourceDockerComposeRoot "load-dms-seed-data.ps1"
            $content = Get-Content -LiteralPath $scriptPath -Raw

            $content | Should -Match '\$tenant\s*=.*CONFIG_SERVICE_TENANT'
            $content | Should -Match '(?s)Resolve-SeedTargetInstances\b[^#]*?-Tenant\s+\$tenant'
            $content | Should -Match '(?s)New-SeedLoaderCredentials\b[^#]*?-Tenant\s+\$tenant'
        }

        It "selects XSD directory from staged ApiSchema manifest or fails when unavailable" {
            $tmpRoot = New-TestDirectory

            # Create a fake xsd source directory with .xsd files
            $xsdSourceDir = Join-Path $tmpRoot "xsd-source"
            New-Item -ItemType Directory -Path $xsdSourceDir -Force | Out-Null
            "<xs:schema />" | Set-Content -LiteralPath (Join-Path $xsdSourceDir "Ed-Fi-Core.xsd") -Encoding utf8

            # Create an ApiSchema manifest under .bootstrap that references the xsd directory
            $bootstrapRoot = Join-Path $tmpRoot ".bootstrap"
            $apiSchemaDir = Join-Path $bootstrapRoot "ApiSchema"
            New-Item -ItemType Directory -Path $apiSchemaDir -Force | Out-Null
            $manifestRelPath = "ApiSchema/bootstrap-api-schema-manifest.json"
            $manifestPath = Join-Path $bootstrapRoot $manifestRelPath
            @{
                projects = @(
                    @{ projectName = "Ed-Fi"; xsdDirectory = $xsdSourceDir }
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
            $copied.Count | Should -Be 1

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
    }

    Context "BulkLoadClient XML interface preflight" {
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

    Context "BulkLoadClient invocation and school-year loop" {
        BeforeAll {
            Import-Module "$script:sourceDockerComposeRoot/env-utility.psm1" -Force
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
                param([string]$dll, [string[]]$args)
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

        It "bootstrap-local-dms.ps1 declares -LoadSeedData and seed-owned flags without exposing -InstanceId" {
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
            $wrapperParams | Should -Not -Contain "InstanceId"
        }

        It "bootstrap-local-dms.ps1 gates the seed phase on -LoadSeedData" {
            # Use an isolated copy so we can stub both downstream phase scripts.
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose

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

            # -LoadSeedData alone (no explicit -EnableConfig) — wrapper must force config on
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

        It "bootstrap-local-dms.ps1 rejects missing bootstrap manifest before any phase invocation when -LoadSeedData is supplied" {
            # Regression: an absent .bootstrap/bootstrap-manifest.json used to throw only inside
            # load-dms-seed-data.ps1 — after the wrapper had already invoked start-local-dms.ps1
            # and spun up Docker + CMS. The wrapper must catch this BEFORE the start phase.
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose

            $startProbe = Join-Path $tmpRoot "start-invoked.txt"
            $seedProbe = Join-Path $tmpRoot "seed-invoked.txt"

            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$startProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "start-local-dms.ps1") -Encoding utf8
            "param([Parameter(ValueFromRemainingArguments)]`$rest); Set-Content -LiteralPath '$seedProbe' -Value 'invoked' -Encoding utf8" |
                Set-Content -LiteralPath (Join-Path $tmpDockerCompose "load-dms-seed-data.ps1") -Encoding utf8

            $wrapperCopy = Join-Path $tmpDockerCompose "bootstrap-local-dms.ps1"

            # The sandbox tmpDockerCompose does NOT have .bootstrap/bootstrap-manifest.json,
            # so -LoadSeedData must throw before invoking start-local-dms.ps1.
            { & $wrapperCopy -LoadSeedData } | Should -Throw -ExpectedMessage "*bootstrap-manifest.json*"

            Test-Path -LiteralPath $startProbe | Should -BeFalse -Because "start phase must not run when manifest is missing"
            Test-Path -LiteralPath $seedProbe | Should -BeFalse -Because "seed phase must not run when manifest is missing"

            # Without -LoadSeedData the preflight is skipped, start phase still runs
            & $wrapperCopy
            Test-Path -LiteralPath $startProbe | Should -BeTrue

            Remove-Item -LiteralPath $tmpRoot -Recurse -Force
        }

        It "bootstrap-local-dms.ps1 rejects descending -SchoolYearRange before any phase invocation" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-local-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose

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

        It "bootstrap-published-dms.ps1 declares -LoadSeedData and seed-owned flags without exposing -InstanceId" {
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
            $wrapperParams | Should -Not -Contain "InstanceId"
        }

        It "bootstrap-published-dms.ps1 gates the seed phase on -LoadSeedData" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose

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

        It "bootstrap-published-dms.ps1 forces -EnableConfig when -LoadSeedData is supplied" {
            $wrapperScript = Join-Path $script:sourceDockerComposeRoot "bootstrap-published-dms.ps1"
            $tmpRoot = New-TestDirectory
            $tmpDockerCompose = Join-Path $tmpRoot "eng/docker-compose"
            New-Item -ItemType Directory -Path $tmpDockerCompose -Force | Out-Null

            Copy-Item -LiteralPath $wrapperScript -Destination $tmpDockerCompose
            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot "bootstrap-wrapper.psm1") -Destination $tmpDockerCompose

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

        It "start-(local|published)-dms.ps1 still declare -LoadSeedData pending Story 04 verification gate" {
            # bootstrap-design.md §6.4 (line 1250) gates the removal of -LoadSeedData on
            # "verifying the repo-pinned BulkLoadClient XML mode against DMS discovery,
            # dependencies, OAuth, data, and XSD metadata or staged-XSD behavior." Story 04
            # owns the XSD staging that closes this gate. Until then, -LoadSeedData stays on
            # start-(local|published)-dms.ps1 invoking the direct-SQL database-template path,
            # so build-dms.ps1's smoke flow remains operational.
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $startScript = Join-Path $script:sourceDockerComposeRoot $name
                Test-Path -LiteralPath $startScript | Should -BeTrue
                $content = Get-Content -LiteralPath $startScript -Raw
                $paramBody = ([regex]::Match($content, '(?s)param\s*\((.*?)\)\s*\n')).Groups[1].Value
                $declaredParams = [regex]::Matches($paramBody, '\$(\w+)') |
                    ForEach-Object { $_.Groups[1].Value }
                $declaredParams | Should -Contain "LoadSeedData" -Because "$name must retain -LoadSeedData until the Story 04 verification gate closes per bootstrap-design.md §6.4"
            }
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
            # Filter to actual call sites only — the function definition looks like
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
            $script:envUtilityContent = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "env-utility.psm1") -Raw
        }

        It "build-dms.ps1 -LoadSeedData routes to the direct-SQL path via start-(local|published)-dms.ps1" {
            # Round 3: build-dms.ps1 forwards -LoadSeedData directly to start-(local|published)-dms.ps1,
            # which loads the populated DB template via setup-database-template.psm1. The deletion of
            # this path is gated on bootstrap-design.md §6.4 (line 1250) Story-04 XSD verification.
            # The new API-based path (bootstrap-(local|published)-dms.ps1 + load-dms-seed-data.ps1)
            # ships in parallel as the forward developer-facing contract.
            $script:buildDmsContent | Should -Match 'start-local-dms\.ps1[^\n]+-LoadSeedData' -Because "build-dms.ps1 must forward -LoadSeedData to start-local-dms.ps1"
            $script:buildDmsContent | Should -Match 'start-published-dms\.ps1[^\n]+-LoadSeedData' -Because "build-dms.ps1 must forward -LoadSeedData to start-published-dms.ps1 (UsePublishedImage branch)"
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
            # instead of re-parsing the regex — single source of truth for the validated range.
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

        It "shared wrapper module passes -SeedDataPathSupplied to Get-EffectiveBootstrapEnvFile" {
            # Regression guard for round-2 finding #3: the Sample/Homograph exclusion must depend on
            # whether a custom -SeedDataPath was supplied, so the shared wrapper body must forward
            # that signal.
            $script:wrapperModuleContent | Should -Match 'Get-EffectiveBootstrapEnvFile[\s\S]+?-SeedDataPathSupplied' -Because "the shared module's Get-EffectiveBootstrapEnvFile call must forward -SeedDataPathSupplied"
        }

        It "Resolve-BootstrapDerivedEnv exposes -FilterSampleHomograph and applies it conditionally" {
            # Regression guard for round-2 finding #3: the env utility must accept the switch and
            # the Sample/Homograph exclusion list must be empty when the switch is not set.
            $script:envUtilityContent | Should -Match '\[switch\]\$FilterSampleHomograph' -Because "Resolve-BootstrapDerivedEnv must accept -FilterSampleHomograph"
            $script:envUtilityContent | Should -Match '(?s)if\s*\(\s*\$FilterSampleHomograph\s*\)\s*\{[^}]*EdFi\.Sample\.ApiSchema[^}]*EdFi\.Homograph\.ApiSchema' -Because "the exclusion list must live inside the if(FilterSampleHomograph) branch"
        }
    }
}
