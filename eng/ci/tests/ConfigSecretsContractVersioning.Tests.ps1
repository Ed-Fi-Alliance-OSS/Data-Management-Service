# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1552: the EdFi.Api.Secrets contract is versioned on its own public surface, independently of
# the Configuration Service release. Three build paths could stamp the release onto it, and each is
# exercised for real here rather than inferred from the csproj:
#
# - build-config.ps1 BuildAndPublish, whose Compile and PublishApi steps pass /p:AssemblyVersion and
#   /p:FileVersion as global properties. Compile is a direct solution build, so its output also
#   covers a plain `dotnet build` of the solution with a global override.
# - build-config.ps1 DockerBuild, which must no longer hand the image an assembly version at all.
# - dotnet pack, whose packed assembly must carry the version the package declares.
#
# Every stamping case has a positive control asserting that a Configuration Service assembly built
# by the same run did take the stamped value (or, for the image, the committed props value), so a
# run that never stamped anything cannot pass by accident.
#
# BuildAndPublish regenerates src/config/Directory.Build.props; this file restores the original
# bytes afterwards. DockerBuild writes the fixed local tag build-config.ps1 uses; this file puts back
# whatever image held that tag beforehand. Tagged Build and Docker so a caller can select or exclude
# the slow cases; neither skips itself when its prerequisite is missing, because a skipped proof is
# indistinguishable from an absent one.

Describe "EdFi.Api.Secrets contract versioning (DMS-1552)" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:configRoot = Join-Path $script:repoRoot "src/config"
        $script:assemblyName = "EdFi.DmsConfigurationService.Secrets"
        $script:packageId = "EdFi.Api.Secrets"
        $script:contractProjectRelative = "contracts/$($script:assemblyName)/$($script:assemblyName).csproj"
        $script:contractProject = Join-Path $script:configRoot $script:contractProjectRelative
        $script:solution = Join-Path $script:configRoot "EdFi.DmsConfigurationService.sln"
        $script:propsPath = Join-Path $script:configRoot "Directory.Build.props"

        # Values no committed file carries, so a match can only come from the stamping under test.
        $script:stampVersion = "9.8.7-dms1552.1"
        $script:stampAssemblyVersion = [version]"9.8.7.42"

        function Get-SingleDeclaredProperty {
            param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Name)

            $nodes = @(([xml](Get-Content -LiteralPath $Path -Raw)).SelectNodes("/Project/PropertyGroup/$Name"))
            if ($nodes.Count -ne 1) {
                throw "$Path declares $Name $($nodes.Count) time(s); exactly one is expected."
            }

            return $nodes[0].InnerText.Trim()
        }

        # A three-part package version and the four-part assembly version it corresponds to.
        function ConvertTo-FourPartVersion {
            param([Parameter(Mandatory)][string] $Version)

            $parsed = [version]$Version
            return [version]::new($parsed.Major, $parsed.Minor, [Math]::Max($parsed.Build, 0), [Math]::Max($parsed.Revision, 0))
        }

        function Get-AssemblyVersion {
            param([Parameter(Mandatory)][string] $Path)

            if (-not (Test-Path -LiteralPath $Path)) {
                throw "Expected assembly was not found: $Path"
            }

            return [System.Reflection.AssemblyName]::GetAssemblyName($Path).Version
        }

        $script:declaredVersion = Get-SingleDeclaredProperty -Path $script:contractProject -Name "Version"
        $script:expectedContractAssemblyVersion = ConvertTo-FourPartVersion -Version $script:declaredVersion
    }

    Context "The packed package" {
        BeforeAll {
            $packOutput = Join-Path $TestDrive "pack"
            $script:extracted = Join-Path $TestDrive "extracted"

            dotnet restore $script:contractProject --nologo | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "dotnet restore of the contract failed." }

            dotnet pack $script:contractProject -c Release --no-restore --nologo -o $packOutput | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "dotnet pack of the contract failed." }

            $packages = @(Get-ChildItem -LiteralPath $packOutput -Filter "*.nupkg")
            if ($packages.Count -ne 1) {
                throw "Expected exactly one package in $packOutput, found $($packages.Count)."
            }

            [System.IO.Compression.ZipFile]::ExtractToDirectory($packages[0].FullName, $script:extracted)

            $nuspecs = @(Get-ChildItem -LiteralPath $script:extracted -Filter "*.nuspec")
            if ($nuspecs.Count -ne 1) {
                throw "Expected exactly one nuspec in the package, found $($nuspecs.Count)."
            }

            $script:nuspecMetadata = ([xml](Get-Content -LiteralPath $nuspecs[0].FullName -Raw)).package.metadata
            $script:packedAssembly = Join-Path $script:extracted "lib/net10.0/$($script:assemblyName).dll"
        }

        It "has the package id EdFi.Api.Secrets" {
            $script:nuspecMetadata.id | Should -BeExactly $script:packageId
        }

        It "has the version the contract csproj declares" {
            $script:nuspecMetadata.version | Should -BeExactly $script:declaredVersion
        }

        It "carries an assembly named EdFi.DmsConfigurationService.Secrets" {
            [System.Reflection.AssemblyName]::GetAssemblyName($script:packedAssembly).Name |
                Should -BeExactly $script:assemblyName
        }

        It "carries an assembly whose AssemblyVersion equals the package version" {
            Get-AssemblyVersion -Path $script:packedAssembly |
                Should -Be (ConvertTo-FourPartVersion -Version $script:nuspecMetadata.version)
        }
    }

    Context "The Configuration Service solution" {
        It "contains the contract project" {
            $listed = @(dotnet sln $script:solution list) | ForEach-Object { $_.Trim().Replace("\", "/") }
            $LASTEXITCODE | Should -Be 0

            $listed | Should -Contain $script:contractProjectRelative
        }
    }

    # DMS-1555: the lanes that publish and promote the package. Read as text, the way
    # DmsPrereleaseImageTags.Tests.ps1 reads on-prerelease.yml, because what matters is which id,
    # which version source and which gate each job names, and those are literal in the file.
    Context "The prerelease and release lanes" {
        BeforeAll {
            $workflows = Join-Path $script:repoRoot ".github/workflows"
            $prerelease = Get-Content -LiteralPath (Join-Path $workflows "on-prerelease.yml") -Raw
            $release = Get-Content -LiteralPath (Join-Path $workflows "on-release.yml") -Raw

            function Get-WorkflowJob {
                param([Parameter(Mandatory)][string] $Workflow, [Parameter(Mandatory)][string] $Name)

                $job = [regex]::Match($Workflow, "(?ms)^  $([regex]::Escape($Name)):\r?\n.*?(?=^  \S|\z)").Value
                if ([string]::IsNullOrEmpty($job)) {
                    throw "Job $Name was not found."
                }
                return $job
            }

            $script:packSecrets = Get-WorkflowJob -Workflow $prerelease -Name "pack-secrets"
            $script:checkSecrets = Get-WorkflowJob -Workflow $prerelease -Name "check-secrets-contract"
            $script:publishSecrets = Get-WorkflowJob -Workflow $prerelease -Name "publish-package-secrets"
            $script:packCs = Get-WorkflowJob -Workflow $prerelease -Name "pack-cs"
            $script:publishCs = Get-WorkflowJob -Workflow $prerelease -Name "publish-package-cs"
            $script:sbomSecrets = Get-WorkflowJob -Workflow $prerelease -Name "sbom-create-secrets"
            $script:provenanceSecrets = Get-WorkflowJob -Workflow $prerelease -Name "provenance-create-secrets"
            $script:prereleaseWorkflow = $prerelease
            $script:promoteSecrets = [regex]::Match(
                $release,
                "(?ms)^      - name: Promote EdFi\.Api\.Secrets Package\r?\n.*?(?=^      - name: |^  \S|\z)"
            ).Value
        }

        It "names the package EdFi.Api.Secrets" {
            $script:prereleaseWorkflow | Should -Match '(?m)^  SECRETS_PACKAGE_NAME: "EdFi\.Api\.Secrets"$'
        }

        It "reads the package version from the contract's own declaration, not from the release tag" {
            $script:packSecrets | Should -Match '\$contractVersion = Get-SecretsContractVersion'
            $script:checkSecrets | Should -Match '-PackageVersion "\$\{\{ needs\.pack-secrets\.outputs\.contract-version \}\}"'
            $script:publishSecrets | Should -Match '-PackageVersion "\$\{\{ needs\.pack-secrets\.outputs\.contract-version \}\}"'
            $script:promoteSecrets | Should -Match '-Version \(Get-SecretsContractVersion\)'
        }

        It "packs what a release build with explicit versions produced, without rebuilding it" {
            $build = $script:packSecrets.IndexOf("build-config.ps1 -Command BuildAndPublish")
            $pack = $script:packSecrets.IndexOf("Invoke-SecretsConsumerCheck.ps1")

            $build | Should -BeGreaterThan -1
            $pack | Should -BeGreaterThan $build
            $script:packSecrets | Should -Match '-DmsCSVersion \$packageVersion'
            $script:packSecrets | Should -Match '(?m)^\s+-NoBuild `$'
        }

        # The publish check pushes a version the feed does not hold without opening it, so this step
        # is the only thing that reads what the first publication of each version contains.
        It "asserts the package contents before the artifact is uploaded for publication" {
            $pack = $script:packSecrets.IndexOf("Invoke-SecretsConsumerCheck.ps1")
            $assert = $script:packSecrets.IndexOf("./eng/verification/Assert-SecretsPackage.ps1")
            $upload = $script:packSecrets.IndexOf("- name: Upload Secrets Package as Artifact")

            $assert | Should -BeGreaterThan $pack
            $upload | Should -BeGreaterThan $assert
            $script:packSecrets | Should -Match '-ExpectedPackageVersion "\$\{\{ steps\.contract-version\.outputs\.contract-version \}\}"'
        }

        It "produces an SBOM and SLSA provenance for the packed artifact" {
            $script:packSecrets | Should -Match '(?m)^      hash-code: \$\{\{ steps\.hash-code\.outputs\.hash-code \}\}$'
            $script:sbomSecrets | Should -Match '(?m)^    needs: pack-secrets$'
            $script:provenanceSecrets | Should -Match '(?m)^    needs: pack-secrets$'
            $script:provenanceSecrets | Should -Match 'base64-subjects: \$\{\{ needs\.pack-secrets\.outputs\.hash-code \}\}'
        }

        It "decides against the feed by package id before anything is pushed" {
            $script:checkSecrets | Should -Match '-PackageId "\$\{\{ env\.SECRETS_PACKAGE_NAME \}\}"'
            $script:publishSecrets | Should -Match 'Invoke-ContractPublishCheck\.ps1'
            $script:publishSecrets | Should -Match "if: steps\.decide\.outputs\.should-push == 'true'"
        }

        # A Configuration Service release is a cs- tag. A dms- gate here would skip the contract on
        # every Configuration Service prerelease and run it on every DMS one instead.
        It "runs the pack and the decision on a cs- prerelease, and only on a dispatch otherwise" {
            $gate = '(?m)^    if: \$\{\{ github\.event_name == ''workflow_dispatch'' \|\| startsWith\(github\.event\.release\.tag_name, ''cs-''\) \}\}$'
            $script:packSecrets | Should -Match $gate
            $script:checkSecrets | Should -Match $gate
        }

        It "runs the pack and the decision on a dispatch and keeps the push off it" {
            $script:packSecrets | Should -Match "github\.event_name == 'workflow_dispatch'"
            $script:checkSecrets | Should -Match "github\.event_name == 'workflow_dispatch'"
            $script:publishSecrets | Should -Match "(?m)^    if: \$\{\{ github\.event_name != 'workflow_dispatch' \}\}$"
        }

        It "builds the Configuration Service package only after the contract check, and publishes it only after the contract" {
            $script:packCs | Should -Match '(?ms)^    needs:\r?\n      - check-secrets-contract$'
            $script:publishCs | Should -Match '(?m)^      - publish-package-secrets$'
        }

        It "promotes only on a final release tag" {
            $script:promoteSecrets | Should -Match '-PackageName "EdFi\.Api\.Secrets"'
            $script:promoteSecrets | Should -Match "if: \$\{\{ github\.ref_type == 'tag' && startsWith\(github\.ref_name, 'v'\) \}\}"
        }
    }

    Context "build-config.ps1 BuildAndPublish with explicit versions" -Tag "Build" {
        BeforeAll {
            $script:originalProps = [System.IO.File]::ReadAllBytes($script:propsPath)

            $frontend = Join-Path $script:configRoot "frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore"
            $script:compileContract = Join-Path $script:configRoot "contracts/$($script:assemblyName)/bin/Release/net10.0/$($script:assemblyName).dll"
            $script:compileFrontendCopy = Join-Path $frontend "bin/Release/net10.0/$($script:assemblyName).dll"
            $script:compileBackend = Join-Path $frontend "bin/Release/net10.0/EdFi.DmsConfigurationService.Backend.dll"
            $publishDirectory = Join-Path $frontend "publish"
            $script:publishContract = Join-Path $publishDirectory "$($script:assemblyName).dll"
            $script:publishFrontend = Join-Path $publishDirectory "EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"
            $script:publishPluginAssemblies = @(
                Join-Path $publishDirectory "EdFi.Api.Plugins.dll"
                Join-Path $publishDirectory "EdFi.Api.Plugins.Hosting.dll"
            )
            $script:expectedPluginsAssemblyVersion = ConvertTo-FourPartVersion -Version (
                Get-SingleDeclaredProperty -Path (Join-Path $script:repoRoot "src/plugins/Directory.Build.props") -Name "VersionPrefix"
            )

            # Git-ignored output from an earlier run would otherwise satisfy the publish assertions.
            if (Test-Path -LiteralPath $publishDirectory) {
                Remove-Item -LiteralPath $publishDirectory -Recurse -Force
            }

            $script:runStarted = [DateTime]::UtcNow

            Push-Location $script:repoRoot
            try {
                pwsh -NoProfile -File (Join-Path $script:repoRoot "build-config.ps1") BuildAndPublish `
                    -Configuration Release `
                    -DmsCSVersion $script:stampVersion `
                    -DmsCSAssemblyVersion $script:stampAssemblyVersion.ToString() | Out-Host
                $script:buildExitCode = $LASTEXITCODE

                # Then pack what that build produced, without building again and while the props
                # file still carries the release stamp: the prerelease lane's order, so the package
                # it publishes is the one this proves.
                $releasePackOutput = Join-Path $TestDrive "pack-after-release-build"
                $releaseExtracted = Join-Path $TestDrive "extracted-after-release-build"
                dotnet pack $script:contractProject -c Release --no-build --nologo -o $releasePackOutput | Out-Host
                $script:releasePackExitCode = $LASTEXITCODE

                $releasePackages = @(Get-ChildItem -LiteralPath $releasePackOutput -Filter "*.nupkg" -ErrorAction SilentlyContinue)
                if ($releasePackages.Count -eq 1) {
                    [System.IO.Compression.ZipFile]::ExtractToDirectory($releasePackages[0].FullName, $releaseExtracted)
                    $script:releasePackNuspec = ([xml](Get-Content -LiteralPath (@(Get-ChildItem -LiteralPath $releaseExtracted -Filter "*.nuspec")[0].FullName) -Raw)).package.metadata
                    $script:releasePackAssembly = Join-Path $releaseExtracted "lib/net10.0/$($script:assemblyName).dll"
                }
                $script:releasePackCount = $releasePackages.Count
            }
            finally {
                Pop-Location
            }
        }

        AfterAll {
            [System.IO.File]::WriteAllBytes($script:propsPath, $script:originalProps)
        }

        It "succeeds" {
            $script:buildExitCode | Should -Be 0
        }

        It "stamps a Configuration Service assembly in the Compile output, so the run was not vacuous" {
            (Get-Item -LiteralPath $script:compileBackend).LastWriteTimeUtc | Should -BeGreaterThan $script:runStarted
            Get-AssemblyVersion -Path $script:compileBackend | Should -Be $script:stampAssemblyVersion
        }

        It "stamps a Configuration Service assembly in the PublishApi output, so the run was not vacuous" {
            Get-AssemblyVersion -Path $script:publishFrontend | Should -Be $script:stampAssemblyVersion
        }

        It "leaves the contract's own Compile output at its declared version" {
            (Get-Item -LiteralPath $script:compileContract).LastWriteTimeUtc | Should -BeGreaterThan $script:runStarted
            Get-AssemblyVersion -Path $script:compileContract | Should -Be $script:expectedContractAssemblyVersion
        }

        It "leaves the contract copied into a referencing project's Compile output at its declared version" {
            Get-AssemblyVersion -Path $script:compileFrontendCopy | Should -Be $script:expectedContractAssemblyVersion
        }

        It "leaves the contract in the PublishApi output at its declared version" {
            Get-AssemblyVersion -Path $script:publishContract | Should -Be $script:expectedContractAssemblyVersion
        }

        It "packs the contract that build produced at its declared package version" {
            $script:releasePackExitCode | Should -Be 0
            $script:releasePackCount | Should -Be 1
            $script:releasePackNuspec.version | Should -BeExactly $script:declaredVersion
        }

        It "packs the contract that build produced with its declared AssemblyVersion" {
            Get-AssemblyVersion -Path $script:releasePackAssembly | Should -Be $script:expectedContractAssemblyVersion
        }

        # The frontend references the plugin contract tree, so the same global properties reach it.
        # The plugin loader's newer-plugin-on-older-host preflight compares these AssemblyVersions.
        It "leaves the plugin contract assemblies in the PublishApi output at their declared version" {
            foreach ($assembly in $script:publishPluginAssemblies) {
                Get-AssemblyVersion -Path $assembly | Should -Be $script:expectedPluginsAssemblyVersion -Because $assembly
            }
        }
    }

    Context "build-config.ps1 DockerBuild with an explicit assembly version" -Tag "Docker" {
        BeforeAll {
            $script:imageTag = "local/ed-fi-api-configuration-service"
            $script:previousImageId = docker image inspect --format "{{.Id}}" $script:imageTag 2>$null
            if ($LASTEXITCODE -ne 0) { $script:previousImageId = $null }

            # Read from the tree the image is built from, and required to differ from the stamp, so
            # the CMS assertion below distinguishes "props value" from "stamped value".
            $script:propsAssemblyVersion = ConvertTo-FourPartVersion -Version (
                Get-SingleDeclaredProperty -Path $script:propsPath -Name "AssemblyVersion"
            )
            if ($script:propsAssemblyVersion -eq $script:stampAssemblyVersion) {
                throw "Directory.Build.props already declares the stamp version $($script:stampAssemblyVersion); choose another."
            }

            Push-Location $script:repoRoot
            try {
                pwsh -NoProfile -File (Join-Path $script:repoRoot "build-config.ps1") DockerBuild `
                    -DmsCSVersion $script:stampVersion `
                    -DmsCSAssemblyVersion $script:stampAssemblyVersion.ToString() | Out-Host
                $script:buildExitCode = $LASTEXITCODE
            }
            finally {
                Pop-Location
            }

            $script:builtImageId = docker image inspect --format "{{.Id}}" $script:imageTag 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $script:builtImageId) {
                throw "DockerBuild left no local image tagged $($script:imageTag); the build output was not loaded into the image store."
            }

            $imageFiles = Join-Path $TestDrive "image"
            New-Item -ItemType Directory -Path $imageFiles -Force | Out-Null
            $script:imageContract = Join-Path $imageFiles "$($script:assemblyName).dll"
            $script:imageFrontend = Join-Path $imageFiles "EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"

            $container = docker create $script:imageTag
            if ($LASTEXITCODE -ne 0 -or -not $container) {
                throw "Could not create a container from $($script:imageTag)."
            }
            try {
                foreach ($copy in @(
                    @{ Source = "/app/$($script:assemblyName).dll"; Destination = $script:imageContract },
                    @{ Source = "/app/EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"; Destination = $script:imageFrontend }
                )) {
                    docker cp "${container}:$($copy.Source)" $copy.Destination | Out-Host
                    if ($LASTEXITCODE -ne 0) {
                        throw "Could not copy $($copy.Source) out of $($script:imageTag)."
                    }
                }
            }
            finally {
                docker rm $container | Out-Null
            }
        }

        AfterAll {
            if ($script:previousImageId) {
                docker tag $script:previousImageId $script:imageTag | Out-Null
            }
            else {
                docker rmi $script:imageTag | Out-Null
            }

            if ($script:builtImageId -and $script:builtImageId -ne $script:previousImageId) {
                docker rmi $script:builtImageId 2>$null | Out-Null
            }
        }

        It "succeeds" {
            $script:buildExitCode | Should -Be 0
        }

        It "leaves a Configuration Service assembly at the Directory.Build.props value rather than the passed assembly version" {
            Get-AssemblyVersion -Path $script:imageFrontend | Should -Be $script:propsAssemblyVersion
        }

        It "leaves the contract at its declared version" {
            Get-AssemblyVersion -Path $script:imageContract | Should -Be $script:expectedContractAssemblyVersion
        }
    }
}
