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

        function script:New-SchemaToolPublishFixture {
            param([Parameter(Mandatory)][string]$RepoRoot)

            $projectPath = Join-Path $RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj'
            $outputDirectory = Join-Path $RepoRoot 'eng/docker-compose/.bootstrap/tools/api-schema-tools'
            New-Item -ItemType Directory -Path (Split-Path $projectPath -Parent) -Force | Out-Null
            New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
            '<Project />' | Set-Content -LiteralPath $projectPath

            return @{
                ProjectPath = [System.IO.Path]::GetFullPath($projectPath)
                OutputDirectory = [System.IO.Path]::GetFullPath($outputDirectory)
                ExecutablePath = [System.IO.Path]::GetFullPath((Join-Path $outputDirectory $(if ($IsWindows) { 'api-schema-tools.exe' } else { 'api-schema-tools' })))
                Calls = [System.Collections.Generic.List[object]]::new()
                ExitCode = 0
                CreateExecutable = $true
                CreateBinExecutable = $false
                OutputLines = @('fixture restore output', 'fixture build output')
            }
        }

        function script:Install-FakeSchemaToolPublisher {
            $dotnetFunction = Get-Item Function:\dotnet -ErrorAction SilentlyContinue
            $script:oldDotnetScriptBlock = if ($null -ne $dotnetFunction) { $dotnetFunction.ScriptBlock } else { $null }
            Set-Item Function:\global:dotnet -Value {
                $script:publishFixture.Calls.Add(@($args))
                foreach ($line in $script:publishFixture.OutputLines) { Write-Output $line }
                if ($script:publishFixture.CreateExecutable) {
                    'fixture executable' | Set-Content -LiteralPath $script:publishFixture.ExecutablePath
                    'dependency bytes' | Set-Content -LiteralPath (Join-Path $script:publishFixture.OutputDirectory 'dependency.dll')
                }
                if ($script:publishFixture.CreateBinExecutable) {
                    $bin = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net10.0'
                    New-Item -ItemType Directory -Path $bin -Force | Out-Null
                    'fixture bin executable' | Set-Content -LiteralPath (Join-Path $bin $(if ($IsWindows) { 'api-schema-tools.exe' } else { 'api-schema-tools' }))
                }
                $global:LASTEXITCODE = $script:publishFixture.ExitCode
            }
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
                $PlantUndeclaredXsdDirectory,

                # Data Standard version emitted as projectSchema.projectVersion, so tests can
                # prove a non-5.2 content-derived value lands in schema.dataStandardVersion.
                [string]
                $ProjectVersion = "5.2.0"
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
                    projectVersion      = $ProjectVersion
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
                -Version "1.0.335" `
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
            Invokes prepare-dms-schema.ps1 in direct standard mode (no -ApiSchemaPath and no
            -EnvironmentFile), which uses the catalog-pinned core-only fallback.
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
            # Recorded from the staged core schema: its Data Standard version
            # (projectSchema.projectVersion) and ApiSchema format version (apiSchemaVersion).
            $manifest.schema.dataStandardVersion | Should -Be "5.2.0"
            $manifest.schema.apiSchemaFormatVersion | Should -Be "1.0.0"
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
                    -Version "1.0.335" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false | Out-Null

                script:New-FixtureNupkg `
                    -FeedFolder $packagesFeedFolder `
                    -PackageId "EdFi.DataStandard52.TPDM.ApiSchema" `
                    -Version "1.0.335" `
                    -ProjectName "TPDM" `
                    -ProjectEndpointName "tpdm" `
                    -IsExtensionProject $true | Out-Null

                $environmentFilePath = script:New-SchemaPackagesEnvironmentFile `
                    -Directory $script:repo.RepoRoot `
                    -Packages @(
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard52.ApiSchema"
                            version = "1.0.335"
                            feedUrl = $packagesFeedFolder
                        },
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard52.TPDM.ApiSchema"
                            version = "1.0.335"
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
                @($manifest.schema.selectedPackages) | Should -Contain "EdFi.DataStandard52.ApiSchema@1.0.335"
                @($manifest.schema.selectedPackages) | Should -Contain "EdFi.DataStandard52.TPDM.ApiSchema@1.0.335"

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

        It "It_detects_the_core_package_for_any_data_standard_version_DS61" {
            # Core detection must be data-standard-agnostic: the DS 6.1 core package id is
            # EdFi.DataStandard61.ApiSchema (not the catalog-pinned DS 5.2 id), and DS 6.1
            # extension ids carry an extra project segment. A DS 6.1 SCHEMA_PACKAGES set must
            # stage with the 61 core recognized as core, or -DataStandardVersion 6.1 bootstraps
            # would fail with "must list exactly one core package".
            $ds61FeedFolder = script:New-TempDirectory
            try {
                script:New-FixtureNupkg `
                    -FeedFolder $ds61FeedFolder `
                    -PackageId "EdFi.DataStandard61.ApiSchema" `
                    -Version "1.0.335" `
                    -ProjectName "Ed-Fi" `
                    -ProjectEndpointName "ed-fi" `
                    -IsExtensionProject $false `
                    -ProjectVersion "6.1.0" | Out-Null

                script:New-FixtureNupkg `
                    -FeedFolder $ds61FeedFolder `
                    -PackageId "EdFi.DataStandard61.Sample.ApiSchema" `
                    -Version "1.0.335" `
                    -ProjectName "Sample" `
                    -ProjectEndpointName "sample" `
                    -IsExtensionProject $true | Out-Null

                $environmentFilePath = script:New-SchemaPackagesEnvironmentFile `
                    -Directory $script:repo.RepoRoot `
                    -Packages @(
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard61.ApiSchema"
                            version = "1.0.335"
                            feedUrl = $ds61FeedFolder
                        },
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard61.Sample.ApiSchema"
                            version = "1.0.335"
                            feedUrl = $ds61FeedFolder
                        }
                    )

                $tool = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA
                & $script:repo.PrepareSchemaScript `
                    -EnvironmentFile $environmentFilePath `
                    -SchemaToolPath $tool | Out-Null

                $manifest = script:Get-RootManifest
                $manifest.schema.selectionMode | Should -Be "Standard"
                @($manifest.schema.selectedExtensions) | Should -Contain "sample"
                # Content-derived, not pinned to 5.2: the DS 6.1 core's projectVersion is what
                # the manifest records.
                $manifest.schema.dataStandardVersion | Should -Be "6.1.0"

                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                    Should -BeTrue
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Sample/ApiSchema.json") |
                    Should -BeTrue
            }
            finally {
                if (Test-Path -LiteralPath $ds61FeedFolder) {
                    Remove-Item -LiteralPath $ds61FeedFolder -Recurse -Force
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
                    -Version "1.0.335" `
                    -ProjectName "TPDM" `
                    -ProjectEndpointName "tpdm" `
                    -IsExtensionProject $true | Out-Null

                $environmentFilePath = script:New-SchemaPackagesEnvironmentFile `
                    -Directory $script:repo.RepoRoot `
                    -Packages @(
                        [pscustomobject]@{
                            name    = "EdFi.DataStandard52.TPDM.ApiSchema"
                            version = "1.0.335"
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
            # The catalog-default staging records the pinned core package identity so the wrapper's
            # package-tier revalidation stays available for later SCHEMA_PACKAGES-carrying envs.
            @($manifest.schema.selectedPackages) | Should -HaveCount 1
            @($manifest.schema.selectedPackages)[0] | Should -Match '^EdFi\.DataStandard52\.ApiSchema@\d+\.\d+\.\d+'
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
            # Standard mode takes its package set from -EnvironmentFile rather than a named
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
            # package-backed standard staging.
            {
                & $script:repo.PrepareSchemaScript -ApiSchemaPath ""
            } | Should -Throw -ExpectedMessage "*-ApiSchemaPath was supplied but is blank*"

            # The failure must occur before any workspace is staged.
            Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") |
                Should -BeFalse
        }
    }

    Context "Given_StandardMode_ManifestCorrectness" {
        It "It_records_package_identity_only_in_the_schema_section_and_never_feed_URLs" {
            $script:feedFolder = script:New-FixtureFeed

            script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

            $rootManifest = script:Get-RootManifest
            $apiSchemaManifest = script:Get-ApiSchemaManifest

            # The schema section records the staged package identity ("<packageId>@<version>") so
            # the bootstrap wrapper can revalidate a staged workspace against the effective
            # SCHEMA_PACKAGES value without re-downloading anything.
            @($rootManifest.schema.selectedPackages) | Should -HaveCount 1
            @($rootManifest.schema.selectedPackages)[0] | Should -Match '^EdFi\.DataStandard52\.ApiSchema@\d+\.\d+\.\d+'

            # That identity lives ONLY in schema.selectedPackages - nowhere else in the root
            # manifest - and feed URLs never participate in it.
            $rootManifestJson = $rootManifest | ConvertTo-Json -Depth 20
            $rootManifestJson | Should -Not -Match "pkgs\.dev\.azure\.com"
            $rootManifestJson | Should -Not -Match $script:feedFolder.Replace("\", "\\")
            $rootManifestWithoutPackages = $rootManifestJson | ConvertFrom-Json
            $rootManifestWithoutPackages.schema.PSObject.Properties.Remove('selectedPackages')
            ($rootManifestWithoutPackages | ConvertTo-Json -Depth 20) | Should -Not -Match "EdFi\.DataStandard"

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
                $coreNupkgName = "$($corePackageId.ToLowerInvariant()).1.0.335.nupkg"
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
                $coreNupkgPath = Join-Path $divergentFeedFolder "$($corePackageId.ToLowerInvariant()).1.0.335.nupkg"
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
                    -Version "1.0.335" `
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
                    -Version "1.0.335" `
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
                    -Version "1.0.335" `
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
                $mismatchNupkgName = "$($corePackageId.ToLowerInvariant()).1.0.335.nupkg"
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
            # The catalog pins the core package at version 1.0.335. Supply the core package at version
            # 1.0.1 only so the resolver cannot find the pinned version 1.0.335.
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
                } | Should -Throw -ExpectedMessage "*version*1.0.335*not found*"

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
                    -Version "1.0.335" `
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
                        -Version "1.0.335" `
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

    Context "Given_AmbientSchemaPackages_DoesNotInfluenceDirectStandardMode" {
        It "It_produces_the_same_core_workspace_regardless_of_ambient_SCHEMA_PACKAGES" {
            # Only -EnvironmentFile opts standard preparation into SCHEMA_PACKAGES-driven staging.
            # The ambient process variable must not alter the direct core-only fallback.
            $script:feedFolder = script:New-FixtureFeed

            $savedSchemaPackages = [System.Environment]::GetEnvironmentVariable("SCHEMA_PACKAGES")
            try {
                # Run with SCHEMA_PACKAGES pointing to something unrelated to the fixture feed.
                [System.Environment]::SetEnvironmentVariable(
                    "SCHEMA_PACKAGES",
                    "EdFi.DataStandard52.Homograph.ApiSchema:1.0.335"
                )

                script:Invoke-PrepareStandard -FeedFolder $script:feedFolder

                $manifest = script:Get-RootManifest

                # Direct standard mode stages core only; ambient SCHEMA_PACKAGES must not introduce an extension.
                $manifest.schema.selectionMode | Should -Be "Standard"
                $manifest.schema.selectedExtensions | Should -BeNullOrEmpty
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") |
                    Should -BeTrue
                Test-Path -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Homograph/ApiSchema.json") |
                    Should -BeFalse
            }
            finally {
                # Restore absence with Remove-Item: SetEnvironmentVariable coerces a $null saved
                # value to "", which newer pwsh/.NET on Unix stores as a present-but-blank variable.
                if ($null -eq $savedSchemaPackages) {
                    Remove-Item Env:SCHEMA_PACKAGES -ErrorAction SilentlyContinue
                }
                else {
                    [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", $savedSchemaPackages)
                }
            }
        }
    }

    Context "Given_ExtensionsParameterRemoved_StaticAssertion" {
        # The package-backed contract derives extensions from SCHEMA_PACKAGES rather than a named
        # -Extensions switch. Assert this from the parameter AST of every required surface (not a single regex
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
                Should -BeFalse -Because "$Name must derive package selection from the effective environment rather than expose -Extensions"
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

    Context "DMS-1283 automatic schema-tool acquisition" {
        BeforeAll {
            $script:resolverFixtureModuleName = 'bootstrap-schema-tool-DMS1283'
        }
        BeforeEach {
            $script:publishFixture = script:New-SchemaToolPublishFixture -RepoRoot $script:repo.RepoRoot
            $script:oldPathFallback = $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK
            $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = 'false'
            $exitVariable = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
            $script:hadLastExitCode = $null -ne $exitVariable
            $script:oldLastExitCode = if ($script:hadLastExitCode) { $exitVariable.Value } else { $null }
            $script:pathCommand = $null
            $script:dotnetLookupCount = 0
            script:Install-FakeSchemaToolPublisher
            $resolverPath = Join-Path $script:repo.DockerComposeRoot "$($script:resolverFixtureModuleName).psm1"
            Copy-Item -LiteralPath (Join-Path $script:repo.DockerComposeRoot 'bootstrap-schema-tool.psm1') -Destination $resolverPath
            Import-Module $resolverPath -Force
            Mock Get-Command { return $script:pathCommand } -ParameterFilter { $Name -eq 'api-schema-tools' } -ModuleName $script:resolverFixtureModuleName
            Mock Get-Command { $script:dotnetLookupCount++; return [pscustomobject]@{ Name = 'dotnet' } } -ParameterFilter { $Name -eq 'dotnet' } -ModuleName $script:resolverFixtureModuleName
        }

        AfterEach {
            if ($null -ne $script:oldDotnetScriptBlock) {
                Set-Item Function:\global:dotnet -Value $script:oldDotnetScriptBlock
            }
            else {
                Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
            }
            $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = $script:oldPathFallback
            if ($script:hadLastExitCode) { $global:LASTEXITCODE = $script:oldLastExitCode }
            else { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
            Get-Module | Where-Object { $_.Path -and $_.Path.StartsWith($script:repo.RepoRoot, [System.StringComparison]::OrdinalIgnoreCase) } | Remove-Module -Force
        }

        It 'It_DMS1283_publishes_once_and_reuses_the_exact_published_path' {
            $first = @(Resolve-DmsSchemaTool)
            $second = @(Resolve-DmsSchemaTool)
            $first.Count | Should -Be 1
            $first[0] | Should -Be $script:publishFixture.ExecutablePath
            $second | Should -Be $first
            $script:publishFixture.Calls.Count | Should -Be 1
            $script:publishFixture.Calls[0] | Should -Be @(
                'publish', $script:publishFixture.ProjectPath,
                '-c', 'Release', '-p:UseAppHost=true',
                '-o', $script:publishFixture.OutputDirectory
            )
        }

        It 'It_DMS1283_missing_source_fails_with_repair_guidance_without_publishing' {
            Remove-Item -LiteralPath $script:publishFixture.ProjectPath

            { Resolve-DmsSchemaTool } | Should -Throw -ExpectedMessage '*source project is missing*Restore the repository checkout*'
            $script:publishFixture.Calls.Count | Should -Be 0
        }

        It 'It_DMS1283_missing_dotnet_fails_with_sdk_guidance_without_publishing' {
            Mock Get-Command { return $null } -ParameterFilter { $Name -eq 'dotnet' } -ModuleName $script:resolverFixtureModuleName

            { Resolve-DmsSchemaTool } | Should -Throw -ExpectedMessage '*.NET SDK command*Install the .NET 10 SDK*'
            $script:publishFixture.Calls.Count | Should -Be 0
        }

        It 'It_DMS1283_nonzero_publish_streams_diagnostics_verbatim_and_reports_exit' {
            $script:publishFixture.ExitCode = 17
            $diagnostic = 'C:\repo\src\Foo.cs(12,5): error CS1002: ; expected [C:\repo\x.csproj]'
            $script:publishFixture.OutputLines = @('fixture restore output', $diagnostic)
            $script:hostOutput = [System.Collections.Generic.List[string]]::new()
            Mock Out-Host { $script:hostOutput.Add([string]$InputObject) } -ModuleName $script:resolverFixtureModuleName
            $failure = $null
            try { $null = Resolve-DmsSchemaTool } catch { $failure = $_.Exception.Message }

            $failure | Should -Match 'exited with code 17'
            $script:hostOutput | Should -Be @('fixture restore output', $diagnostic)
            $failure | Should -Match ([regex]::Escape($script:publishFixture.ProjectPath))
            $script:publishFixture.Calls.Count | Should -Be 1
            Test-Path -LiteralPath $script:publishFixture.ExecutablePath | Should -BeFalse
        }

        It 'It_DMS1283_handles_missing_session_variables_when_publish_<Mode>' -TestCases @(
            @{ Mode = 'succeeds'; FailInvocation = $false }
            @{ Mode = 'cannot_start'; FailInvocation = $true }
        ) {
            param($Mode, $FailInvocation)

            $childScript = Join-Path $script:repo.RepoRoot 'fresh-session.ps1'
            @'
param($ModulePath, $ExpectedPath, [int]$FailInvocation)
$ErrorActionPreference = 'Stop'
$env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = 'false'
Import-Module $ModulePath -Force
& (Get-Module ([System.IO.Path]::GetFileNameWithoutExtension($ModulePath))) {
    param($ExpectedPath, $FailInvocation)
    $script:expectedPath = $ExpectedPath
    $script:failInvocation = $FailInvocation
    function script:dotnet {
        if ($script:failInvocation) { throw 'fixture invocation failed before native completion' }
        Set-Content -LiteralPath $script:expectedPath -Value 'fixture executable'
        $global:LASTEXITCODE = 0
    }
} $ExpectedPath $FailInvocation
Remove-Variable PSNativeCommandUseErrorActionPreference, LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
try {
    $resolved = @(Resolve-DmsSchemaTool)
    [pscustomobject]@{ Resolved = $resolved; Failure = '' } | ConvertTo-Json -Compress
}
catch {
    [pscustomobject]@{ Resolved = @(); Failure = $_.Exception.Message } | ConvertTo-Json -Compress
}
'@ | Set-Content -LiteralPath $childScript

            $result = (& pwsh -NoLogo -NoProfile -File $childScript `
                (Join-Path $script:repo.DockerComposeRoot "$($script:resolverFixtureModuleName).psm1") `
                $script:publishFixture.ExecutablePath ([int]$FailInvocation) | Select-Object -Last 1) | ConvertFrom-Json

            if ($FailInvocation) {
                $result.Failure | Should -BeLike '*Unable to acquire api-schema-tools*invocation failed before native completion*Check the .NET 10 SDK*'
                $result.Resolved.Count | Should -Be 0
            }
            else {
                $result.Failure | Should -Be ''
                $result.Resolved | Should -Be @($script:publishFixture.ExecutablePath)
            }
        }

        It 'It_DMS1283_zero_exit_without_executable_fails_explicitly' {
            $script:publishFixture.CreateExecutable = $false

            { Resolve-DmsSchemaTool } | Should -Throw -ExpectedMessage '*without producing the expected executable*'
            $script:publishFixture.Calls.Count | Should -Be 1
        }

        It 'It_DMS1283_failure_after_executable_and_bin_creation_cleans_only_published_executable' {
            $script:publishFixture.ExitCode = 17
            $script:publishFixture.CreateBinExecutable = $true
            $sentinelPath = Join-Path $script:publishFixture.OutputDirectory 'existing-dependency.dll'
            Set-Content -LiteralPath $sentinelPath -Value 'keep these bytes' -NoNewline
            $failure = $null
            try { $null = Resolve-DmsSchemaTool } catch { $failure = $_.Exception.Message }
            $binPath = Join-Path -Path $script:repo.RepoRoot -ChildPath 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net10.0' -AdditionalChildPath $(if ($IsWindows) { 'api-schema-tools.exe' } else { 'api-schema-tools' })

            $failure | Should -Match 'exited with code 17'
            Test-Path -LiteralPath $script:publishFixture.ExecutablePath | Should -BeFalse
            Test-Path -LiteralPath $binPath | Should -BeTrue
            Get-Content -LiteralPath $sentinelPath -Raw | Should -Be 'keep these bytes'
            $script:publishFixture.Calls.Count | Should -Be 1
        }

        It 'It_DMS1283_cleanup_failure_retains_publish_error' {
            $script:publishFixture.ExitCode = 17
            Mock Remove-Item { throw 'fixture cleanup failure' } -ParameterFilter { $LiteralPath -eq $script:publishFixture.ExecutablePath } -ModuleName $script:resolverFixtureModuleName

            { Resolve-DmsSchemaTool } | Should -Throw -ExpectedMessage '*exited with code 17*Cleanup also failed*fixture cleanup failure*'
            Test-Path -LiteralPath $script:publishFixture.ExecutablePath | Should -BeTrue
        }

        It 'It_DMS1283_paths_and_noisy_diagnostics_preserve_paths_without_leaking_output' {
            $specialRoot = Join-Path $script:repo.RepoRoot 'special (DMS)#1283'
            Get-Module | Where-Object { $_.Path -and $_.Path.StartsWith($script:repo.RepoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) } | Remove-Module -Force
            New-Item -ItemType Directory -Path $specialRoot -Force | Out-Null
            Move-Item -LiteralPath (Join-Path $script:repo.RepoRoot 'eng'), (Join-Path $script:repo.RepoRoot 'src') -Destination $specialRoot
            $script:repo.DockerComposeRoot = Join-Path $specialRoot 'eng/docker-compose'
            $script:repo.BootstrapRoot = Join-Path $script:repo.DockerComposeRoot '.bootstrap'
            $script:publishFixture = script:New-SchemaToolPublishFixture -RepoRoot $specialRoot
            $resolverPath = Join-Path $script:repo.DockerComposeRoot "$($script:resolverFixtureModuleName).psm1"
            Copy-Item -LiteralPath (Join-Path $script:repo.DockerComposeRoot 'bootstrap-schema-tool.psm1') -Destination $resolverPath
            Import-Module $resolverPath -Force
            Mock Get-Command { return $script:pathCommand } -ParameterFilter { $Name -eq 'api-schema-tools' } -ModuleName $script:resolverFixtureModuleName
            Mock Get-Command { $script:dotnetLookupCount++; return [pscustomobject]@{ Name = 'dotnet' } } -ParameterFilter { $Name -eq 'dotnet' } -ModuleName $script:resolverFixtureModuleName
            $script:publishFixture.OutputLines = @('fixture restore output', 'fixture build output')
            $elsewhere = Join-Path $script:repo.RepoRoot 'elsewhere'
            New-Item -ItemType Directory -Path $elsewhere -Force | Out-Null
            Push-Location $elsewhere
            try {
                $allOutput = @(Resolve-DmsSchemaTool 6>&1)
                $resolved = @($allOutput | Where-Object { $_ -is [string] })
                $information = @($allOutput | Where-Object { $_ -is [System.Management.Automation.InformationRecord] })
                $resolved.Count | Should -Be 1
                $resolved[0] | Should -Be $script:publishFixture.ExecutablePath
                $script:publishFixture.Calls[0] | Should -Be @('publish', $script:publishFixture.ProjectPath, '-c', 'Release', '-p:UseAppHost=true', '-o', $script:publishFixture.OutputDirectory)
                $resolved -join '' | Should -Not -Match 'fixture restore output|fixture build output'
                ($information -join ' ') | Should -Match 'No eligible api-schema-tools executable was found'

                $script:publishFixture.ExitCode = 17
                Remove-Item -LiteralPath $script:publishFixture.ExecutablePath
                $failure = $null
                try { $null = Resolve-DmsSchemaTool } catch { $failure = $_.Exception.Message }
                $failure | Should -Match ([regex]::Escape($script:publishFixture.OutputDirectory))
                $failure | Should -Not -Match "`r|`n"
            }
            finally { Pop-Location }
        }

        It 'It_DMS1283_native_error_preferences_preserve_actionable_publish_failure' {
            & (Get-Module $script:resolverFixtureModuleName) {
                $script:ErrorActionPreference = 'Stop'
                $script:PSNativeCommandUseErrorActionPreference = $true
                function script:dotnet {
                    & pwsh -NoLogo -NoProfile -Command "[Console]::Error.WriteLine('native fixture diagnostics'); exit 17" 2>&1
                    $global:LASTEXITCODE = $LASTEXITCODE
                }
            }

            { Resolve-DmsSchemaTool } | Should -Throw -ExpectedMessage '*api-schema-tools*exited with code 17*Check the .NET 10 SDK*'
        }

        It 'It_DMS1283_disabled_ambient_path_tool_publishes_without_selecting_path' {
            $script:pathCommand = [pscustomobject]@{ Source = 'C:\ambient\api-schema-tools.exe' }

            $resolved = Resolve-DmsSchemaTool

            $resolved | Should -Be $script:publishFixture.ExecutablePath
            $script:publishFixture.Calls.Count | Should -Be 1
            $script:dotnetLookupCount | Should -Be 1
        }

        It 'It_DMS1283_explicit_path_beats_cache_and_invalid_explicit_path_fails_closed' {
            $explicit = Join-Path $script:repo.RepoRoot 'explicit/api-schema-tools.exe'
            New-Item -ItemType Directory -Path (Split-Path $explicit -Parent) -Force | Out-Null
            Set-Content -LiteralPath $explicit -Value 'explicit'
            Set-Content -LiteralPath $script:publishFixture.ExecutablePath -Value 'published'

            Resolve-DmsSchemaTool -RequestedPath $explicit | Should -Be ([System.IO.Path]::GetFullPath($explicit))
            { Resolve-DmsSchemaTool -RequestedPath (Join-Path $script:repo.RepoRoot 'missing/tool.exe') } | Should -Throw -ExpectedMessage '*configured api-schema-tools executable was not found*'
            $script:publishFixture.Calls.Count | Should -Be 0
            $script:dotnetLookupCount | Should -Be 0
        }

        It 'It_DMS1283_published_tool_beats_bin_and_skips_dotnet_lookup' {
            $bin = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net12.0'
            New-Item -ItemType Directory -Path $bin -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $bin $(if ($IsWindows) { 'api-schema-tools.exe' } else { 'api-schema-tools' })) -Value 'bin'
            Set-Content -LiteralPath $script:publishFixture.ExecutablePath -Value 'published'

            Resolve-DmsSchemaTool | Should -Be $script:publishFixture.ExecutablePath
            $script:publishFixture.Calls.Count | Should -Be 0
            $script:dotnetLookupCount | Should -Be 0
        }

        It 'It_DMS1283_framework_and_configuration_priority_is_preserved' {
            $releaseHigh = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net11.0'
            $debugLow = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Debug/net10.0'
            $releaseSame = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net10.0'
            foreach ($directory in @($releaseHigh, $debugLow, $releaseSame)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
            $toolName = if ($IsWindows) { 'api-schema-tools.exe' } else { 'api-schema-tools' }
            foreach ($directory in @($releaseHigh, $debugLow, $releaseSame)) { Set-Content -LiteralPath (Join-Path $directory $toolName) -Value $directory }

            Resolve-DmsSchemaTool | Should -Be (Join-Path $releaseHigh $toolName)
            Remove-Item -LiteralPath (Join-Path $releaseHigh $toolName)
            Resolve-DmsSchemaTool | Should -Be (Join-Path $debugLow $toolName)
            $script:publishFixture.Calls.Count | Should -Be 0
            $script:dotnetLookupCount | Should -Be 0
        }

        It 'It_DMS1283_cdc_debug_bin_is_selected_without_sdk_lookup' {
            Mock Get-Command { throw 'Unexpected dotnet lookup' } -ParameterFilter { $Name -eq 'dotnet' } -ModuleName $script:resolverFixtureModuleName
            $debug = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Debug/net10.0'
            $release = Join-Path $script:repo.RepoRoot 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net10.0'
            New-Item -ItemType Directory -Path $debug -Force | Out-Null
            New-Item -ItemType Directory -Path $release -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $debug 'api-schema-tools') -Value 'debug extensionless'
            Set-Content -LiteralPath (Join-Path $release 'api-schema-tools') -Value 'release extensionless'

            Resolve-DmsSchemaTool | Should -Be (Join-Path $debug 'api-schema-tools')
            $script:publishFixture.Calls.Count | Should -Be 0
            Should -Invoke Get-Command -ModuleName $script:resolverFixtureModuleName -Times 0 -ParameterFilter { $Name -eq 'dotnet' }
        }

        It 'It_DMS1283_opted_in_path_is_used_only_after_local_misses' {
            $script:pathCommand = [pscustomobject]@{ Source = 'C:\ambient\api-schema-tools.exe' }
            $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = 'true'

            Resolve-DmsSchemaTool | Should -Be 'C:\ambient\api-schema-tools.exe'
            $script:publishFixture.Calls.Count | Should -Be 0
            $script:dotnetLookupCount | Should -Be 0
        }

        It 'It_DMS1283_stale_dms_schema_location_is_ignored_and_publishing_is_selected' {
            $stale = Join-Path $script:repo.BootstrapRoot 'tools/dms-schema'
            New-Item -ItemType Directory -Path $stale -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $stale $(if ($IsWindows) { 'dms-schema.exe' } else { 'dms-schema' })) -Value 'stale'

            Resolve-DmsSchemaTool | Should -Be $script:publishFixture.ExecutablePath
            $script:publishFixture.Calls.Count | Should -Be 1
            $script:dotnetLookupCount | Should -Be 1
        }
    }

    Context "DMS-1283 prepare override forwarding" {
        BeforeEach {
            $script:feedFolder = script:New-FixtureFeed
            $script:oldToolPath = $env:DMS_SCHEMA_TOOL_PATH
            $script:oldFallback = $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK
            $dotnetFunction = Get-Item Function:\dotnet -ErrorAction SilentlyContinue
            $script:oldDotnetScriptBlock = if ($null -ne $dotnetFunction) { $dotnetFunction.ScriptBlock } else { $null }
            $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = 'false'
            Set-Item Function:\global:dotnet -Value { throw 'Unexpected schema-tool publish during prepare override test' }
            $publishedDirectory = Join-Path $script:repo.BootstrapRoot 'tools/api-schema-tools'
            New-Item -ItemType Directory -Path $publishedDirectory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $publishedDirectory $(if ($IsWindows) { 'api-schema-tools.exe' } else { 'api-schema-tools' })) -Value 'published cache sentinel'
        }

        AfterEach {
            $env:DMS_SCHEMA_TOOL_PATH = $script:oldToolPath
            $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = $script:oldFallback
            if ($null -ne $script:oldDotnetScriptBlock) { Set-Item Function:\global:dotnet -Value $script:oldDotnetScriptBlock }
            else { Remove-Item Function:\dotnet -ErrorAction SilentlyContinue }
        }

        It 'It_DMS1283_prepare_parameter_beats_environment_and_cache' {
            $parameterDirectory = Join-Path $script:repo.RepoRoot 'parameter-tool'
            $environmentDirectory = Join-Path $script:repo.RepoRoot 'environment-tool'
            New-Item -ItemType Directory -Path $parameterDirectory, $environmentDirectory -Force | Out-Null
            $parameterTool = script:New-FakeSchemaTool -Directory $parameterDirectory -Hash $script:hashA
            $environmentHash = '0000000000000000000000000000000000000000000000000000000000000002'
            $environmentTool = script:New-FakeSchemaTool -Directory $environmentDirectory -Hash $environmentHash
            $env:DMS_SCHEMA_TOOL_PATH = $environmentTool

            & $script:repo.PrepareSchemaScript -PackageFeedUrl $script:feedFolder -SchemaToolPath $parameterTool | Out-Null

            (script:Get-RootManifest).schema.effectiveSchemaHash | Should -Be $script:hashA
        }

        It 'It_DMS1283_prepare_environment_override_is_used_when_parameter_is_omitted' {
            $environmentHash = '0000000000000000000000000000000000000000000000000000000000000003'
            $environmentDirectory = Join-Path $script:repo.RepoRoot 'environment-tool'
            New-Item -ItemType Directory -Path $environmentDirectory -Force | Out-Null
            $env:DMS_SCHEMA_TOOL_PATH = script:New-FakeSchemaTool -Directory $environmentDirectory -Hash $environmentHash

            & $script:repo.PrepareSchemaScript -PackageFeedUrl $script:feedFolder | Out-Null

            (script:Get-RootManifest).schema.effectiveSchemaHash | Should -Be $environmentHash
        }

        It 'It_DMS1283_prepare_invalid_parameter_fails_closed_despite_valid_cache' {
            $env:DMS_SCHEMA_TOOL_PATH = script:New-FakeSchemaTool -Directory $script:repo.RepoRoot -Hash $script:hashA
            $missingParameter = Join-Path $script:repo.RepoRoot 'missing/parameter-api-schema-tools.exe'

            { & $script:repo.PrepareSchemaScript -PackageFeedUrl $script:feedFolder -SchemaToolPath $missingParameter } |
                Should -Throw -ExpectedMessage '*configured api-schema-tools executable was not found*'
        }

        It 'It_DMS1283_prepare_invalid_environment_fails_closed_despite_valid_cache' {
            $env:DMS_SCHEMA_TOOL_PATH = Join-Path $script:repo.RepoRoot 'missing/environment-api-schema-tools.exe'

            { & $script:repo.PrepareSchemaScript -PackageFeedUrl $script:feedFolder } |
                Should -Throw -ExpectedMessage '*configured api-schema-tools executable was not found*'
        }
    }

    Context "Given_ExplicitToolPathErrors" {
        It "It_Resolve-DmsSchemaTool_preserves_backslashes_in_a_missing_explicit_tool_path" {
            $resolverModulePath = Join-Path $script:repo.DockerComposeRoot 'bootstrap-schema-tool.psm1'
            Import-Module $resolverModulePath -Force
            $missingTool = Join-Path $script:repo.RepoRoot "tools\missing\api-schema-tools.exe"
            $expectedPath = [System.IO.Path]::GetFullPath($missingTool)

            { Resolve-DmsSchemaTool -RequestedPath $missingTool } |
                Should -Throw -ExpectedMessage "*$expectedPath*"
        }
    }

    Context "Given_PackageResolutionPathErrors" {
        It "It_Resolve-StandardSchemaPackage_preserves_backslashes_in_a_local_feed_path" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-package-resolver.psm1") -Force
            $feedDirectory = Join-Path $script:repo.RepoRoot "feeds\empty"
            $destinationRoot = Join-Path $script:repo.BootstrapRoot "package-resolution"
            New-Item -ItemType Directory -Path $feedDirectory -Force | Out-Null

            {
                Resolve-StandardSchemaPackage `
                    -FeedUrl $feedDirectory `
                    -PackageId "EdFi.Missing.ApiSchema" `
                    -Version "1.0.0" `
                    -DestinationRoot $destinationRoot
            } | Should -Throw -ExpectedMessage "*$feedDirectory*"
        }
    }
}
