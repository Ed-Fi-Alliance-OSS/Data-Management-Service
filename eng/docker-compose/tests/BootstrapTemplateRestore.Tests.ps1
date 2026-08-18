# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The HTTP feed mock body executes in the mocked module''s session state, where test-scope locals are invisible; global variables are the documented crossing mechanism and are removed in AfterAll.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Test helpers intentionally mirror production plural-noun contracts (environment value sets).')]
param()

BeforeAll {
    $script:dockerComposeDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:dockerComposeDir "bootstrap-restore.psm1") -Force
    Import-Module (Join-Path $script:dockerComposeDir "../DatabaseTemplates/Template-RestoreCore.psm1") -Force
    Import-Module (Join-Path $script:dockerComposeDir "../DatabaseTemplates/Template-RestoreTrust.psm1") -Force

    function script:New-TestWorkspace {
        $path = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    function script:New-TestEnvironmentValues {
        param (
            [string]$PackageId = "EdFi.Api.Populated.Template.PostgreSql.5.2.0",
            [string]$NugetVersion = "1.0.123",
            [string]$FeedUrl = ""
        )

        $values = @{ DATABASE_TEMPLATE_PACKAGE = $PackageId }
        if (-not [string]::IsNullOrWhiteSpace($NugetVersion)) {
            $values["DATABASE_TEMPLATE_NUGET_VERSION"] = $NugetVersion
        }
        if (-not [string]::IsNullOrWhiteSpace($FeedUrl)) {
            $values["DATABASE_TEMPLATE_FEED_URL"] = $FeedUrl
        }
        return $values
    }

    function script:New-RestoreTrustWorld {
        # One self-consistent trust world: a signing key and a tracked policy trusting it as
        # producer 'local-dev' (plus a deliberately absent local overlay path).
        $workspace = New-TestWorkspace
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "signer.pem")

        $trackedPolicyPath = Join-Path $workspace "template-trust-policy.json"
        [ordered]@{
            version   = 1
            producers = @(
                [ordered]@{
                    name       = "local-dev"
                    provider   = "detached-attestation"
                    publicKeys = @(
                        [ordered]@{
                            keyId            = $signingKey.KeyId
                            algorithm        = $signingKey.Algorithm
                            publicKeySpkiB64 = $signingKey.PublicKeySpkiB64
                        }
                    )
                }
            )
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $trackedPolicyPath -Encoding utf8

        return [pscustomobject]@{
            Workspace         = $workspace
            SigningKey        = $signingKey
            TrackedPolicyPath = $trackedPolicyPath
            LocalPolicyPath   = (Join-Path $workspace "template-trust-policy.local.json")
        }
    }

    function script:New-RestoreTestPackage {
        # Builds a structurally real template package: nuspec + restore manifest + artifact
        # zipped as <id>.<version>.nupkg, with a sibling attestation document signed AFTER
        # the final bytes exist (unless skipped or tampered afterwards).
        param (
            [Parameter(Mandatory = $true)]
            [string]$Directory,

            [Parameter(Mandatory = $true)]
            $TrustWorld,

            [string]$PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
            [string]$PackageVersion = "1.0.123",
            [ValidateSet("postgresql", "mssql")]
            [string]$DatabaseEngine = "postgresql",
            [ValidateSet("Minimal", "Populated")]
            [string]$TemplateKind = "Minimal",
            [string]$Producer = "local-dev",

            [switch]$OmitManifest,
            [switch]$ExtraArtifact,
            [switch]$WrongNuspecVersion,
            [switch]$WrongArtifactSha,
            [switch]$SkipAttestation,
            [switch]$TamperAfterSigning
        )

        $layout = Join-Path (New-TestWorkspace) "layout"
        New-Item -ItemType Directory -Path $layout -Force | Out-Null

        $artifactExtension = if ($DatabaseEngine -eq "mssql") { "bak" } else { "sql" }
        $artifactFileName = "$PackageId.$artifactExtension"
        $artifactPath = Join-Path $layout $artifactFileName
        Set-Content -LiteralPath $artifactPath -Value "fake artifact bytes $([Guid]::NewGuid())"
        $artifactSha256 = Get-FileSha256Hex -Path $artifactPath
        if ($WrongArtifactSha) {
            $artifactSha256 = "ee" * 32
        }

        if (-not $OmitManifest) {
            $inventory = @{
                schemas    = @(
                    @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                    @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                    @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                    @{ schemaName = $(if ($DatabaseEngine -eq "mssql") { "dbo" } else { "public" }); objects = @() }
                )
                principals = @()
            }
            $manifestArguments = @{
                PackageId                = $PackageId
                PackageVersion           = $PackageVersion
                DatabaseEngine           = $DatabaseEngine
                TemplateKind             = $TemplateKind
                DataStandardVersion      = "5.2.0"
                ProjectName              = [string[]]@("edfi")
                ApiSchemaFormatVersion   = "1.0.0"
                EffectiveSchemaHash      = ("ab" * 32)
                ResourceKeyCount         = 42
                ResourceKeySeedHashB64   = [System.Convert]::ToBase64String([byte[]](1..32))
                RelationalMappingVersion = "v2"
                EngineVersion            = $(if ($DatabaseEngine -eq "mssql") { "17.0.900.7" } else { "16.8" })
                DocumentJsonColumnType   = $(if ($DatabaseEngine -eq "mssql") { "nvarchar" } else { "jsonb" })
                Inventory                = $inventory
                ArtifactFileName         = $artifactFileName
                ArtifactSha256           = $artifactSha256
            }
            if ($DatabaseEngine -eq "mssql") {
                $manifestArguments.DatabaseCompatibilityLevel = 170
            }
            $manifest = New-TemplateRestoreManifest @manifestArguments
            $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $layout (Get-RestoreManifestFileName)) -Encoding utf8
        }

        if ($ExtraArtifact) {
            Set-Content -LiteralPath (Join-Path $layout "undeclared-extra.$artifactExtension") -Value "smuggled artifact"
        }

        $nuspecVersion = if ($WrongNuspecVersion) { "9.9.9" } else { $PackageVersion }
        @"
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>$PackageId</id>
    <version>$nuspecVersion</version>
    <description>Test template package</description>
    <authors>Ed-Fi Alliance</authors>
  </metadata>
</package>
"@ | Set-Content -LiteralPath (Join-Path $layout "$PackageId.nuspec") -Encoding utf8

        $packagePath = Join-Path $Directory "$PackageId.$PackageVersion.nupkg"
        $zipPath = Join-Path (New-TestWorkspace) "package.zip"
        Compress-Archive -Path (Join-Path $layout "*") -DestinationPath $zipPath -Force
        Copy-Item -LiteralPath $zipPath -Destination $packagePath -Force

        $attestationPath = $null
        if (-not $SkipAttestation) {
            $attestationJson = New-TemplateAttestation `
                -PackageId $PackageId `
                -PackageVersion $PackageVersion `
                -PackageSha256 (Get-FileSha256Hex -Path $packagePath) `
                -Producer $Producer `
                -PrivateKeyPath $TrustWorld.SigningKey.PrivateKeyPath
            $attestationPath = Join-Path $Directory (Get-TemplateAttestationFileName -PackageFileName ([System.IO.Path]::GetFileName($packagePath)))
            Set-Content -LiteralPath $attestationPath -Value $attestationJson -Encoding utf8
        }

        if ($TamperAfterSigning) {
            Add-Content -LiteralPath $packagePath -Value "tampered after signing"
        }

        return [pscustomobject]@{
            PackageId       = $PackageId
            PackageVersion  = $PackageVersion
            PackagePath     = $packagePath
            AttestationPath = $attestationPath
        }
    }

    function script:New-CompanionAttestationPackage {
        # Zips an attestation document as "<PackageId>.Attestation.<version>.nupkg" for the
        # HTTP-feed transport tests.
        param (
            [Parameter(Mandatory = $true)]
            [string]$Directory,

            [Parameter(Mandatory = $true)]
            $Package
        )

        $layout = Join-Path (New-TestWorkspace) "companion-layout"
        New-Item -ItemType Directory -Path $layout -Force | Out-Null
        Copy-Item -LiteralPath $Package.AttestationPath -Destination (Join-Path $layout ([System.IO.Path]::GetFileName($Package.AttestationPath)))

        $companionPath = Join-Path $Directory "$($Package.PackageId).Attestation.$($Package.PackageVersion).nupkg"
        $zipPath = Join-Path (New-TestWorkspace) "companion.zip"
        Compress-Archive -Path (Join-Path $layout "*") -DestinationPath $zipPath -Force
        Copy-Item -LiteralPath $zipPath -Destination $companionPath -Force

        return $companionPath
    }

    function script:Invoke-TrustedFindAndStage {
        # Full pre-Docker consumer flow: find, authenticate, stage.
        param (
            [Parameter(Mandatory = $true)]
            $TrustWorld,

            [Parameter(Mandatory = $true)]
            [string]$PackageDirectory,

            [Parameter(Mandatory = $true)]
            [hashtable]$EnvironmentValues,

            [string]$RestoreTemplate = "Minimal",
            [string]$DatabaseEngine = "postgresql",
            [string]$StageRoot = ""
        )

        $package = Find-RestoreTemplatePackage `
            -EnvironmentValues $EnvironmentValues `
            -RestoreTemplate $RestoreTemplate `
            -DatabaseEngine $DatabaseEngine `
            -PackageDirectory $PackageDirectory

        $trust = Assert-TrustedRestorePackage `
            -Package $package `
            -TrackedPolicyPath $TrustWorld.TrackedPolicyPath `
            -LocalPolicyPath $TrustWorld.LocalPolicyPath

        if ([string]::IsNullOrWhiteSpace($StageRoot)) {
            $StageRoot = New-TestWorkspace
        }

        return Initialize-RestorePackageStage `
            -Package $package `
            -AuthenticatedPackageSha256 $trust.PackageSha256 `
            -Producer $trust.Producer `
            -DatabaseEngine $DatabaseEngine `
            -RestoreTemplate $RestoreTemplate `
            -StageRoot $StageRoot
    }
}

Describe "Resolve-RestoreTemplatePackageIdentity" {
    It "swaps the kind and engine segments so a stale base value cannot select the wrong package" {
        $identity = Resolve-RestoreTemplatePackageIdentity `
            -EnvironmentValues (New-TestEnvironmentValues -PackageId "EdFi.Api.Populated.Template.PostgreSql.5.2.0") `
            -RestoreTemplate Minimal -DatabaseEngine mssql
        $identity.PackageId | Should -Be "EdFi.Api.Minimal.Template.MsSql.5.2.0"
        $identity.PackageVersion | Should -Be "1.0.123"

        $identity = Resolve-RestoreTemplatePackageIdentity `
            -EnvironmentValues (New-TestEnvironmentValues -PackageId "EdFi.Api.Minimal.Template.MsSql.6.1.0") `
            -RestoreTemplate Populated -DatabaseEngine postgresql
        $identity.PackageId | Should -Be "EdFi.Api.Populated.Template.PostgreSql.6.1.0"
    }

    It "rejects a missing or malformed DATABASE_TEMPLATE_PACKAGE" {
        { Resolve-RestoreTemplatePackageIdentity -EnvironmentValues @{} -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*DATABASE_TEMPLATE_PACKAGE is not set*"
        { Resolve-RestoreTemplatePackageIdentity -EnvironmentValues (New-TestEnvironmentValues -PackageId "EdFi.Api.Full.Template.PostgreSql.5.2.0") -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*must contain exactly one '.Minimal.Template.' or '.Populated.Template.'*"
    }

    It "requires the exact NuGet version for feed resolution and never conflates it with the Data Standard version" {
        $failure = { Resolve-RestoreTemplatePackageIdentity `
                -EnvironmentValues (New-TestEnvironmentValues -NugetVersion "") `
                -RestoreTemplate Minimal -DatabaseEngine postgresql -RequireNugetVersion }
        $failure | Should -Throw "*DATABASE_TEMPLATE_NUGET_VERSION is not set*"
        $failure | Should -Throw "*NOT the Data Standard version*"
        $failure | Should -Throw "*never floats to latest*"
    }
}

Describe "Get-RestorePackageVersionFromFileName" {
    It "parses the version for the expected id, case-insensitively" {
        Get-RestorePackageVersionFromFileName -PackageFileName "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.123.nupkg" -PackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" |
            Should -Be "1.0.123"
        Get-RestorePackageVersionFromFileName -PackageFileName "edfi.api.minimal.template.postgresql.5.2.0.1.0.123.nupkg" -PackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" |
            Should -Be "1.0.123"
    }

    It "rejects a file that does not match the expected id" {
        { Get-RestorePackageVersionFromFileName -PackageFileName "SomethingElse.1.0.123.nupkg" -PackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" } |
            Should -Throw "*does not match the expected template package id*"
    }
}

Describe "Find-RestoreTemplatePackage (explicit -PackageDirectory)" {
    It "locates the single template package and its sibling attestation, ignoring companion packages" {
        $trustWorld = New-RestoreTrustWorld
        $packageDirectory = New-TestWorkspace
        $built = New-RestoreTestPackage -Directory $packageDirectory -TrustWorld $trustWorld
        New-CompanionAttestationPackage -Directory $packageDirectory -Package $built | Out-Null

        $package = Find-RestoreTemplatePackage `
            -EnvironmentValues (New-TestEnvironmentValues) `
            -RestoreTemplate Minimal -DatabaseEngine postgresql `
            -PackageDirectory $packageDirectory

        $package.PackageId | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
        $package.PackageVersion | Should -Be "1.0.123"
        $package.PackagePath | Should -Be $built.PackagePath
        $package.AttestationJson | Should -Not -BeNullOrEmpty
    }

    It "rejects zero or multiple template packages, id mismatches, and version mismatches" {
        $trustWorld = New-RestoreTrustWorld
        $environmentValues = New-TestEnvironmentValues

        { Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory (New-TestWorkspace) } |
            Should -Throw "*Expected exactly one template .nupkg*found 0*"

        $twoPackagesDirectory = New-TestWorkspace
        Set-Content -LiteralPath (Join-Path $twoPackagesDirectory "a.nupkg") -Value "x"
        Set-Content -LiteralPath (Join-Path $twoPackagesDirectory "b.nupkg") -Value "y"
        { Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $twoPackagesDirectory } |
            Should -Throw "*found 2*"

        $wrongIdDirectory = New-TestWorkspace
        Set-Content -LiteralPath (Join-Path $wrongIdDirectory "SomethingElse.1.0.123.nupkg") -Value "z"
        { Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $wrongIdDirectory } |
            Should -Throw "*does not match the expected template package id*"

        $versionMismatchDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $versionMismatchDirectory -TrustWorld $trustWorld -PackageVersion "1.0.999" | Out-Null
        { Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $versionMismatchDirectory } |
            Should -Throw "*carries version '1.0.999' but DATABASE_TEMPLATE_NUGET_VERSION requests '1.0.123'*"
    }

    It "fails closed when the sibling attestation document is missing" {
        $trustWorld = New-RestoreTrustWorld
        $packageDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $packageDirectory -TrustWorld $trustWorld -SkipAttestation | Out-Null

        $failure = { Find-RestoreTemplatePackage -EnvironmentValues (New-TestEnvironmentValues) -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $packageDirectory }
        $failure | Should -Throw "*No attestation document was found*"
        $failure | Should -Throw "*no unsigned-package bypass*"
    }
}

Describe "Find-RestoreTemplatePackage (directory feed via DATABASE_TEMPLATE_FEED_URL)" {
    It "resolves the pinned version from a directory feed with its sibling attestation" {
        $trustWorld = New-RestoreTrustWorld
        $feedDirectory = New-TestWorkspace
        $built = New-RestoreTestPackage -Directory $feedDirectory -TrustWorld $trustWorld

        $package = Find-RestoreTemplatePackage `
            -EnvironmentValues (New-TestEnvironmentValues -FeedUrl $feedDirectory) `
            -RestoreTemplate Minimal -DatabaseEngine postgresql

        $package.PackagePath | Should -Be $built.PackagePath
        $package.AttestationJson | Should -Not -BeNullOrEmpty
        $package.DownloadDirectory | Should -BeNullOrEmpty
    }

    It "fails closed when the directory feed carries no sibling attestation" {
        $trustWorld = New-RestoreTrustWorld
        $feedDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $feedDirectory -TrustWorld $trustWorld -SkipAttestation | Out-Null

        { Find-RestoreTemplatePackage -EnvironmentValues (New-TestEnvironmentValues -FeedUrl $feedDirectory) -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*No attestation document was found beside the directory-feed package*"
    }
}

Describe "Find-RestoreTemplatePackage (HTTP v3 feed)" {
    BeforeAll {
        function script:Set-HttpFeedMock {
            # Serves a mocked NuGet v3 flat container from prebuilt files: the service index,
            # per-package version indexes, and the .nupkg downloads. The lookup tables travel
            # through global variables because the mock body executes in the mocked module's
            # session state, where test-scope locals are not visible.
            param (
                [Parameter(Mandatory = $true)]
                [hashtable]$PackageFilesById,

                [Parameter(Mandatory = $true)]
                [hashtable]$VersionsById
            )

            $global:HttpFeedMockPackageFilesById = $PackageFilesById
            $global:HttpFeedMockVersionsById = $VersionsById

            Mock Invoke-WebRequest -ModuleName bootstrap-package-resolver {
                $requestUri = [string]$Uri
                if ($requestUri -eq "https://feed.example/index.json") {
                    return [pscustomobject]@{ Content = '{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://feed.example/flat/"}]}' }
                }

                $indexMatch = [System.Text.RegularExpressions.Regex]::Match($requestUri, '^https://feed\.example/flat/(?<id>[^/]+)/index\.json$')
                if ($indexMatch.Success) {
                    $packageIdLower = $indexMatch.Groups["id"].Value
                    if ($global:HttpFeedMockVersionsById.ContainsKey($packageIdLower)) {
                        $versionList = (@($global:HttpFeedMockVersionsById[$packageIdLower]) | ForEach-Object { '"' + $_ + '"' }) -join ","
                        return [pscustomobject]@{ Content = ('{"versions":[' + $versionList + ']}') }
                    }
                    throw "404 simulated: no such package '$packageIdLower'"
                }

                $downloadMatch = [System.Text.RegularExpressions.Regex]::Match($requestUri, '^https://feed\.example/flat/(?<id>[^/]+)/[^/]+/[^/]+\.nupkg$')
                if ($downloadMatch.Success) {
                    $packageIdLower = $downloadMatch.Groups["id"].Value
                    if ($global:HttpFeedMockPackageFilesById.ContainsKey($packageIdLower)) {
                        Copy-Item -LiteralPath $global:HttpFeedMockPackageFilesById[$packageIdLower] -Destination $OutFile
                        return
                    }
                    throw "404 simulated: no such download '$packageIdLower'"
                }

                throw "Unexpected request in HTTP feed mock: $requestUri"
            }
        }
    }

    AfterAll {
        Remove-Variable -Name HttpFeedMockPackageFilesById -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name HttpFeedMockVersionsById -Scope Global -ErrorAction SilentlyContinue
    }

    It "downloads the template and its companion attestation package, extracting the attestation for verification" {
        $trustWorld = New-RestoreTrustWorld
        $sourceDirectory = New-TestWorkspace
        $built = New-RestoreTestPackage -Directory $sourceDirectory -TrustWorld $trustWorld
        $companionPath = New-CompanionAttestationPackage -Directory $sourceDirectory -Package $built

        Set-HttpFeedMock `
            -PackageFilesById @{
                "edfi.api.minimal.template.postgresql.5.2.0"             = $built.PackagePath
                "edfi.api.minimal.template.postgresql.5.2.0.attestation" = $companionPath
            } `
            -VersionsById @{
                "edfi.api.minimal.template.postgresql.5.2.0"             = @("1.0.123")
                "edfi.api.minimal.template.postgresql.5.2.0.attestation" = @("1.0.123")
            }

        $downloadRoot = New-TestWorkspace
        $package = Find-RestoreTemplatePackage `
            -EnvironmentValues (New-TestEnvironmentValues -FeedUrl "https://feed.example/index.json") `
            -RestoreTemplate Minimal -DatabaseEngine postgresql `
            -DownloadRoot $downloadRoot

        $package.AttestationSource | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.Attestation@1.0.123"
        $package.DownloadDirectory | Should -BeLike "$downloadRoot*"

        # The downloaded bytes authenticate through the exact same code path as local ones.
        $trust = Assert-TrustedRestorePackage -Package $package -TrackedPolicyPath $trustWorld.TrackedPolicyPath -LocalPolicyPath $trustWorld.LocalPolicyPath
        $trust.Producer | Should -Be "local-dev"
    }

    It "fails closed when the companion attestation package is missing from the feed" {
        $trustWorld = New-RestoreTrustWorld
        $sourceDirectory = New-TestWorkspace
        $built = New-RestoreTestPackage -Directory $sourceDirectory -TrustWorld $trustWorld

        Set-HttpFeedMock `
            -PackageFilesById @{ "edfi.api.minimal.template.postgresql.5.2.0" = $built.PackagePath } `
            -VersionsById @{ "edfi.api.minimal.template.postgresql.5.2.0" = @("1.0.123") }

        $failure = { Find-RestoreTemplatePackage `
                -EnvironmentValues (New-TestEnvironmentValues -FeedUrl "https://feed.example/index.json") `
                -RestoreTemplate Minimal -DatabaseEngine postgresql `
                -DownloadRoot (New-TestWorkspace) }
        $failure | Should -Throw "*companion attestation package 'EdFi.Api.Minimal.Template.PostgreSql.5.2.0.Attestation'*could not be resolved*"
        $failure | Should -Throw "*no unsigned-package bypass*"
    }

    It "requires the exact NuGet version before any feed request is made" {
        Set-HttpFeedMock -PackageFilesById @{} -VersionsById @{}

        { Find-RestoreTemplatePackage `
                -EnvironmentValues (New-TestEnvironmentValues -NugetVersion "" -FeedUrl "https://feed.example/index.json") `
                -RestoreTemplate Minimal -DatabaseEngine postgresql `
                -DownloadRoot (New-TestWorkspace) } |
            Should -Throw "*DATABASE_TEMPLATE_NUGET_VERSION is not set*"

        Should -Invoke Invoke-WebRequest -ModuleName bootstrap-package-resolver -Times 0 -Exactly
    }
}

Describe "Assert-TrustedRestorePackage" {
    It "authenticates trusted bytes and rejects tampered, unsigned-identity, and untrusted-signer packages" {
        $trustWorld = New-RestoreTrustWorld
        $environmentValues = New-TestEnvironmentValues

        $trustedDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $trustedDirectory -TrustWorld $trustWorld | Out-Null
        $trustedPackage = Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $trustedDirectory
        $trust = Assert-TrustedRestorePackage -Package $trustedPackage -TrackedPolicyPath $trustWorld.TrackedPolicyPath -LocalPolicyPath $trustWorld.LocalPolicyPath
        $trust.Producer | Should -Be "local-dev"
        $trust.PackageSha256 | Should -Match '^[0-9a-f]{64}$'

        $tamperedDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $tamperedDirectory -TrustWorld $trustWorld -TamperAfterSigning | Out-Null
        $tamperedPackage = Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $tamperedDirectory
        { Assert-TrustedRestorePackage -Package $tamperedPackage -TrackedPolicyPath $trustWorld.TrackedPolicyPath -LocalPolicyPath $trustWorld.LocalPolicyPath } |
            Should -Throw "*failed authentication*does not match the resolved package bytes*"

        $untrustedWorld = New-RestoreTrustWorld
        $untrustedDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $untrustedDirectory -TrustWorld $untrustedWorld | Out-Null
        $untrustedPackage = Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $untrustedDirectory
        { Assert-TrustedRestorePackage -Package $untrustedPackage -TrackedPolicyPath $trustWorld.TrackedPolicyPath -LocalPolicyPath $trustWorld.LocalPolicyPath } |
            Should -Throw "*failed authentication*not signed by any trusted producer key*"
    }

    It "fails closed against an anchor-less policy" {
        $trustWorld = New-RestoreTrustWorld
        $packageDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $packageDirectory -TrustWorld $trustWorld | Out-Null
        $package = Find-RestoreTemplatePackage -EnvironmentValues (New-TestEnvironmentValues) -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $packageDirectory

        $emptyPolicyPath = Join-Path (New-TestWorkspace) "empty-policy.json"
        '{"version":1,"producers":[]}' | Set-Content -LiteralPath $emptyPolicyPath -Encoding utf8
        { Assert-TrustedRestorePackage -Package $package -TrackedPolicyPath $emptyPolicyPath } |
            Should -Throw "*failed authentication*no detached-attestation producers*"
    }
}

Describe "Initialize-RestorePackageStage" {
    It "stages immutable authenticated bytes with a validated manifest, identity triangle, and single declared artifact" {
        $trustWorld = New-RestoreTrustWorld
        $packageDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $packageDirectory -TrustWorld $trustWorld | Out-Null

        $stage = Invoke-TrustedFindAndStage -TrustWorld $trustWorld -PackageDirectory $packageDirectory -EnvironmentValues (New-TestEnvironmentValues)

        $stage.Manifest.templateKind | Should -Be "Minimal"
        $stage.ArtifactSha256 | Should -Be ([string]$stage.Manifest.artifactSha256)
        $stage.Producer | Should -Be "local-dev"
        (Get-Item -LiteralPath $stage.ArtifactPath).IsReadOnly | Should -BeTrue
        (Get-Item -LiteralPath $stage.PackagePath).IsReadOnly | Should -BeTrue

        Remove-RestorePackageStage -StageDirectory $stage.StageDirectory
        Test-Path -LiteralPath $stage.StageDirectory | Should -BeFalse
    }

    It "rejects a package whose staged bytes differ from the authenticated hash and removes the stage" {
        $trustWorld = New-RestoreTrustWorld
        $packageDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $packageDirectory -TrustWorld $trustWorld | Out-Null
        $package = Find-RestoreTemplatePackage -EnvironmentValues (New-TestEnvironmentValues) -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $packageDirectory

        $stageRoot = New-TestWorkspace
        { Initialize-RestorePackageStage -Package $package -AuthenticatedPackageSha256 ("ff" * 32) -Producer "local-dev" -DatabaseEngine postgresql -RestoreTemplate Minimal -StageRoot $stageRoot } |
            Should -Throw "*changed between authentication and staging*"
        @(Get-ChildItem -LiteralPath $stageRoot -Directory).Count | Should -Be 0
    }

    It "rejects engine/kind mismatches, identity-triangle breaks, undeclared artifacts, artifact-hash mismatches, and manifest-less packages - always removing the stage" {
        $trustWorld = New-RestoreTrustWorld
        $environmentValues = New-TestEnvironmentValues

        # Requested kind differs from the manifest's.
        $kindDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $kindDirectory -TrustWorld $trustWorld -TemplateKind "Minimal" -PackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" | Out-Null
        $kindPackage = Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $kindDirectory
        $kindTrust = Assert-TrustedRestorePackage -Package $kindPackage -TrackedPolicyPath $trustWorld.TrackedPolicyPath -LocalPolicyPath $trustWorld.LocalPolicyPath
        $stageRoot = New-TestWorkspace
        { Initialize-RestorePackageStage -Package $kindPackage -AuthenticatedPackageSha256 $kindTrust.PackageSha256 -Producer $kindTrust.Producer -DatabaseEngine mssql -RestoreTemplate Minimal -StageRoot $stageRoot } |
            Should -Throw "*declares databaseEngine 'postgresql'*"
        @(Get-ChildItem -LiteralPath $stageRoot -Directory).Count | Should -Be 0

        # Nuspec version disagrees with the resolved identity.
        $nuspecDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $nuspecDirectory -TrustWorld $trustWorld -WrongNuspecVersion | Out-Null
        { Invoke-TrustedFindAndStage -TrustWorld $trustWorld -PackageDirectory $nuspecDirectory -EnvironmentValues $environmentValues } |
            Should -Throw "*nuspec declares identity*9.9.9*"

        # An undeclared database artifact is smuggled beside the declared one.
        $extraDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $extraDirectory -TrustWorld $trustWorld -ExtraArtifact | Out-Null
        { Invoke-TrustedFindAndStage -TrustWorld $trustWorld -PackageDirectory $extraDirectory -EnvironmentValues $environmentValues } |
            Should -Throw "*artifacts beyond the manifest-declared*undeclared-extra*"

        # The packaged artifact does not match the manifest's hash.
        $hashDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $hashDirectory -TrustWorld $trustWorld -WrongArtifactSha | Out-Null
        { Invoke-TrustedFindAndStage -TrustWorld $trustWorld -PackageDirectory $hashDirectory -EnvironmentValues $environmentValues } |
            Should -Throw "*does not match the manifest's artifactSha256*"

        # A legacy package without a restore manifest is not restore-eligible.
        $legacyDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $legacyDirectory -TrustWorld $trustWorld -OmitManifest | Out-Null
        { Invoke-TrustedFindAndStage -TrustWorld $trustWorld -PackageDirectory $legacyDirectory -EnvironmentValues $environmentValues } |
            Should -Throw "*not eligible for restore*"
    }

    It "never touches Docker anywhere in the pre-Docker consumer flow, on success or failure" {
        Mock docker -ModuleName bootstrap-restore { throw "docker must not be invoked before authentication, staging, and validation complete" }

        $trustWorld = New-RestoreTrustWorld
        $environmentValues = New-TestEnvironmentValues

        $happyDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $happyDirectory -TrustWorld $trustWorld | Out-Null
        $stage = Invoke-TrustedFindAndStage -TrustWorld $trustWorld -PackageDirectory $happyDirectory -EnvironmentValues $environmentValues
        Remove-RestorePackageStage -StageDirectory $stage.StageDirectory

        $tamperedDirectory = New-TestWorkspace
        New-RestoreTestPackage -Directory $tamperedDirectory -TrustWorld $trustWorld -TamperAfterSigning | Out-Null
        $tamperedPackage = Find-RestoreTemplatePackage -EnvironmentValues $environmentValues -RestoreTemplate Minimal -DatabaseEngine postgresql -PackageDirectory $tamperedDirectory
        { Assert-TrustedRestorePackage -Package $tamperedPackage -TrackedPolicyPath $trustWorld.TrackedPolicyPath -LocalPolicyPath $trustWorld.LocalPolicyPath } |
            Should -Throw

        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
    }
}

Describe "Resolve-RestoreTargetDatabaseName" {
    It "resolves the same keys and defaults the configure phase registers" {
        Resolve-RestoreTargetDatabaseName -EnvironmentValues @{ POSTGRES_DB_NAME = "custom_pg" } -DatabaseEngine postgresql | Should -Be "custom_pg"
        Resolve-RestoreTargetDatabaseName -EnvironmentValues @{ MSSQL_DB_NAME = "custom_ms" } -DatabaseEngine mssql | Should -Be "custom_ms"
        Resolve-RestoreTargetDatabaseName -EnvironmentValues @{} -DatabaseEngine postgresql | Should -Be "edfi_datamanagementservice"
        Resolve-RestoreTargetDatabaseName -EnvironmentValues @{} -DatabaseEngine mssql | Should -Be "edfi_datamanagementservice"
    }
}

Describe "Assert-RestoreTargetDatabaseNameSafe" {
    It "accepts a plain datastore target on both engines and the shared-topology name without the switch" {
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" } | Should -Not -Throw
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" } | Should -Not -Throw
    }

    It "rejects every reserved system database on the selected engine" {
        foreach ($name in @("postgres", "template0", "template1", "TEMPLATE1")) {
            { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine postgresql -TargetDatabaseName $name } |
                Should -Throw "*reserved postgresql system database*"
        }
        foreach ($name in @("master", "model", "msdb", "tempdb", "MSDB")) {
            { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine mssql -TargetDatabaseName $name } |
                Should -Throw "*reserved mssql system database*"
        }
    }

    It "rejects unsafe identifiers before any topology comparison" {
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine postgresql -TargetDatabaseName "bad-name" } |
            Should -Throw "*unsupported characters*"
    }

    It "never allows the separate Configuration Service database as a restore target, case-insensitively" {
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine mssql -TargetDatabaseName "edfi_configurationservice" -SeparateConfigDatabase -EffectiveConfigDatabaseName "edfi_configurationservice" } |
            Should -Throw "*never replaces a separate CMS database*"
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine mssql -TargetDatabaseName "EDFI_CONFIGURATIONSERVICE" -SeparateConfigDatabase -EffectiveConfigDatabaseName "edfi_configurationservice" } |
            Should -Throw "*never replaces a separate CMS database*"
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" -SeparateConfigDatabase -EffectiveConfigDatabaseName "edfi_configurationservice" } |
            Should -Not -Throw
        { Assert-RestoreTargetDatabaseNameSafe -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" -SeparateConfigDatabase } |
            Should -Throw "*requires the effective Configuration Service database name*"
    }
}
