# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "DocumentCacheAdmin package target" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:buildScriptPath = Join-Path $script:repoRoot "build-dms.ps1"
        $script:smokeScriptPath = Join-Path $script:repoRoot "eng/ci/Invoke-DocumentCacheAdminPackageSmoke.ps1"

        $parseErrors = $null
        $script:buildScriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $script:buildScriptPath, [ref]$null, [ref]$parseErrors
        )

        if (@($parseErrors).Count -gt 0) {
            throw "Failed to parse '$script:buildScriptPath': $(@($parseErrors)[0].Message)"
        }

        function Get-ScriptFunctionAst {
            param(
                [string]
                $FunctionName
            )

            $functionAst = $script:buildScriptAst.FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $FunctionName },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$script:buildScriptPath'."
            }

            return $functionAst
        }

        function Get-InvokedCommandName {
            param(
                [System.Management.Automation.Language.Ast]
                $FunctionAst
            )

            return @(
                $FunctionAst.FindAll(
                    { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                    $true
                ) |
                    ForEach-Object { $_.GetCommandName() } |
                    Where-Object { $_ }
            )
        }

        function Get-BuildPackageSwitchCommandMap {
            $switchAst = (Get-ScriptFunctionAst -FunctionName "BuildPackage").FindAll(
                { param($node) $node -is [System.Management.Automation.Language.SwitchStatementAst] },
                $true
            ) | Select-Object -First 1

            if ($null -eq $switchAst) {
                throw "BuildPackage does not contain a switch statement."
            }

            $commandsByTarget = @{}

            foreach ($clause in $switchAst.Clauses) {
                $label = $clause.Item1.Value
                $commandsByTarget[$label] = @(
                    $clause.Item2.FindAll(
                        { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                        $true
                    ) |
                        ForEach-Object { $_.GetCommandName() } |
                        Where-Object { $_ }
                )
            }

            return $commandsByTarget
        }
    }

    # Three of the assertions below pin the whole PackageTarget surface rather than only this
    # fixture's own target: the ValidateSet list, the All dispatch order, and the single-target
    # dispatch map are exact-set comparisons. Every package target the build script gains therefore
    # lands here, whatever this file is named after.
    #
    # The single-target assertion earns that description only because it compares the switch's key
    # set. It previously checked each target key on its own, so a target added to both the
    # ValidateSet and the switch was invisible to it; the Identity target was added that way and only
    # the first two assertions caught it.
    It "recognizes DocumentCacheAdmin as a public PackageTarget value" {
        $packageTargetParameter = $script:buildScriptAst.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq "PackageTarget" } |
            Select-Object -First 1

        $packageTargetParameter | Should -Not -BeNullOrEmpty

        $validateSet = $packageTargetParameter.Attributes |
            Where-Object { $_.TypeName.Name -eq "ValidateSet" } |
            Select-Object -First 1

        $validateSet | Should -Not -BeNullOrEmpty

        $targetValues = @($validateSet.PositionalArguments | ForEach-Object { $_.Value })
        $targetValues | Should -Be @(
            "All",
            "Api",
            "SchemaTools",
            "CustomValidation",
            "DocumentCacheAdmin",
            "Plugins",
            "Identity"
        )
    }

    It "dispatches PackageTarget All to every package builder" {
        $commandsByTarget = Get-BuildPackageSwitchCommandMap

        $commandsByTarget["All"] | Should -Be @(
            "BuildApiPackage",
            "BuildSchemaToolsPackage",
            "BuildCustomValidationPackage",
            "BuildDocumentCacheAdminPackage",
            "BuildPluginsPackage",
            "BuildIdentityPackage"
        )
    }

    It "dispatches each single package target to exactly its package builder" {
        $commandsByTarget = Get-BuildPackageSwitchCommandMap

        $expectedByTarget = [ordered]@{
            "Api"                = @("BuildApiPackage")
            "SchemaTools"        = @("BuildSchemaToolsPackage")
            "CustomValidation"   = @("BuildCustomValidationPackage")
            "DocumentCacheAdmin" = @("BuildDocumentCacheAdminPackage")
            "Plugins"            = @("BuildPluginsPackage")
            "Identity"           = @("BuildIdentityPackage")
        }

        # The switch's own clause keys, compared as a set, so a target added to the script without
        # being named here fails rather than going unchecked. The parser reports the default clause
        # separately from Clauses, so "All" plus the single targets is the whole key set.
        @($commandsByTarget.Keys | Sort-Object) |
            Should -Be @(@("All") + @($expectedByTarget.Keys) | Sort-Object)

        foreach ($target in $expectedByTarget.Keys) {
            $commandsByTarget[$target] | Should -Be $expectedByTarget[$target]
        }
    }

    It "preserves the existing API package builder behavior" {
        $builder = Get-ScriptFunctionAst -FunctionName "BuildApiPackage"
        $builderCommands = Get-InvokedCommandName -FunctionAst $builder

        $builderCommands | Should -Contain "RunNuGetPack"
        $builderCommands | Should -Not -Contain "DotNetClean"
        $builderCommands | Should -Not -Contain "Restore"
        $builderCommands | Should -Not -Contain "Compile"
        $builderCommands | Should -Not -Contain "PublishApi"
        $builderCommands | Should -Not -Contain "PublishCliApiDownloader"
        $builder.Extent.Text | Should -Not -BeLike '*$schemaDownloaderProjectName/publish*'
    }

    It "preserves the existing SchemaTools no-build package builder behavior" {
        $builder = Get-ScriptFunctionAst -FunctionName "BuildSchemaToolsPackage"

        $builder.Extent.Text | Should -Not -BeLike '*dotnet restore*'
        $builder.Extent.Text | Should -BeLike '*dotnet pack $projectPath*'
        $builder.Extent.Text | Should -BeLike '*--no-build*'
        $builder.Extent.Text | Should -BeLike '*--no-restore*'
    }

    It "packs the DocumentCacheAdmin tool project as EdFi.Api.DocumentCacheAdmin" {
        $builder = Get-ScriptFunctionAst -FunctionName "BuildDocumentCacheAdminPackage"
        $builderCommands = Get-InvokedCommandName -FunctionAst $builder

        $builderCommands | Should -Contain "dotnet"
        $builder.Extent.Text | Should -BeLike '*$clisRoot/$documentCacheAdminProjectName/$documentCacheAdminProjectName.csproj*'
        $builder.Extent.Text | Should -BeLike '*$PSScriptRoot/$documentCacheAdminPackageName.$DMSVersion.nupkg*'
        $builder.Extent.Text | Should -BeLike '*dotnet restore $projectPath*'
        $builder.Extent.Text | Should -BeLike '*dotnet pack $projectPath*'
        $builder.Extent.Text | Should -BeLike '*-p:PackageVersion=$DMSVersion*'
    }

    It "passes the caller-selected package file through the existing Push command" {
        $pushPackage = Get-ScriptFunctionAst -FunctionName "PushPackage"

        $pushPackage.Extent.Text | Should -BeLike '*dotnet nuget push $PackageFile --api-key $NuGetApiKey --source $EdFiNuGetFeed*'
    }

    It "refuses to delete an existing smoke tool path without the package smoke marker" {
        $toolPath = Join-Path ([System.IO.Path]::GetTempPath()) "dms-document-cache-shared-$([Guid]::NewGuid().ToString('N'))"
        $keepPath = Join-Path $toolPath "keep.txt"

        New-Item -ItemType Directory -Path $toolPath -Force | Out-Null
        Set-Content -LiteralPath $keepPath -Value "keep" -Encoding utf8NoBOM

        try {
            { & $script:smokeScriptPath -SkipPackageBuild -ToolPath $toolPath } |
                Should -Throw -ExpectedMessage "*does not contain DocumentCacheAdmin package smoke marker*"

            Test-Path -LiteralPath $keepPath -PathType Leaf | Should -BeTrue
        }
        finally {
            Remove-Item -LiteralPath $toolPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Set DOCUMENT_CACHE_ADMIN_WORKFLOWS_DIRECTORY to a directory holding copies of on-prerelease.yml and
# on-release.yml to point the wiring assertions at them, for example to prove the prohibition on
# downstream needs fails when a job lists one of the DocumentCacheAdmin jobs.
Describe "DocumentCacheAdmin release workflow wiring" {
    BeforeAll {
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $workflows = $env:DOCUMENT_CACHE_ADMIN_WORKFLOWS_DIRECTORY
        if ([string]::IsNullOrWhiteSpace($workflows)) {
            $workflows = Join-Path $repoRoot ".github/workflows"
        }

        $script:prerelease = Get-Content -LiteralPath (Join-Path $workflows "on-prerelease.yml") -Raw
        $script:release = Get-Content -LiteralPath (Join-Path $workflows "on-release.yml") -Raw
        $script:pullRequest = Get-Content -LiteralPath (Join-Path $workflows "on-dms-pullrequest.yml") -Raw
        $script:documentCacheAdminJobs = @(
            "pack-document-cache-admin",
            "sbom-create-document-cache-admin",
            "provenance-create-document-cache-admin",
            "publish-package-document-cache-admin",
            "attach-document-cache-admin-artifacts-to-release"
        )

        # No -Force, for the reason package-helpers.psm1 gives.
        Import-Module (Join-Path $PSScriptRoot 'WorkflowText.psm1') -ErrorAction Stop

        function Get-WorkflowStep {
            param([Parameter(Mandatory)][string] $Workflow, [Parameter(Mandatory)][string] $Name)

            $step = [regex]::Match(
                $Workflow,
                "(?ms)^      - name: $([regex]::Escape($Name))\r?\n.*?(?=^      - name: |^      # |^  \S|\z)"
            ).Value
            if ([string]::IsNullOrEmpty($step)) {
                throw "Step $Name was not found."
            }
            return $step
        }

        function Get-JobIf {
            param([Parameter(Mandatory)][string] $Job)

            $match = [regex]::Match($Job, '(?m)^    if: (.+?)\s*$')
            if (-not $match.Success) { return $null }
            return $match.Groups[1].Value
        }

        function Get-JobName {
            param([Parameter(Mandatory)][string] $Workflow)

            $jobsKey = [regex]::Match($Workflow, '(?m)^jobs:[ \t]*\r?$')
            if (-not $jobsKey.Success) { throw "No jobs: key was found." }
            return @(
                [regex]::Matches($Workflow.Substring($jobsKey.Index), '(?m)^  (?<id>[A-Za-z_][A-Za-z0-9_-]*):[ \t]*\r?$') |
                    ForEach-Object { $_.Groups['id'].Value }
            )
        }
    }

    Context "Pull request workflow wiring" {
        It "packs, installs and runs DocumentCacheAdmin in the job that does so for SchemaTools, after it" {
            $job = Get-WorkflowJobText -Workflow $script:pullRequest -Id "verify-dms-packages"
            $names = @(
                [regex]::Matches($job, '(?m)^      - name: (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value }
            )

            $schemaToolsRun = $names.IndexOf("Install and Run SchemaTools Package")
            $create = $names.IndexOf("Create DocumentCacheAdmin NuGet Package")
            $run = $names.IndexOf("Install and Run DocumentCacheAdmin Package")

            $schemaToolsRun | Should -BeGreaterThan -1
            $create | Should -BeGreaterThan $schemaToolsRun
            $run | Should -BeGreaterThan $create
        }

        It "packs the DocumentCacheAdmin target and smokes the packed package without rebuilding it" {
            $job = Get-WorkflowJobText -Workflow $script:pullRequest -Id "verify-dms-packages"

            (Get-WorkflowStep -Workflow $job -Name "Create DocumentCacheAdmin NuGet Package") |
                Should -Match '-PackageTarget DocumentCacheAdmin'
            $run = Get-WorkflowStep -Workflow $job -Name "Install and Run DocumentCacheAdmin Package"
            $run | Should -Match 'Invoke-DocumentCacheAdminPackageSmoke\.ps1'
            $run | Should -Match '-SkipPackageBuild'
        }

        It "registers ReleaseAssets.Tests.ps1 in the Pester allowlist" {
            $script:pullRequest | Should -Match '(?m)^\s+"eng/ci/tests/ReleaseAssets\.Tests\.ps1",\s*$'
        }
    }

    Context "Release workflow wiring" {
        It "defines the DocumentCacheAdmin package name in the prerelease workflow env" {
            $script:prerelease | Should -Match '(?m)^  DOCUMENT_CACHE_ADMIN_PACKAGE_NAME: "EdFi\.Api\.DocumentCacheAdmin"\s*$'
        }

        It "gates pack-document-cache-admin exactly as pack-schema-tools is gated" {
            $pack = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-document-cache-admin"
            $sibling = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-schema-tools"

            @(Get-WorkflowJobNeed -Job $sibling) | Should -Not -BeNullOrEmpty
            @(Get-WorkflowJobNeed -Job $pack) | Sort-Object | Should -Be (@(Get-WorkflowJobNeed -Job $sibling) | Sort-Object)
            Get-JobIf -Job $sibling | Should -Not -BeNullOrEmpty
            Get-JobIf -Job $pack | Should -BeExactly (Get-JobIf -Job $sibling)
        }

        It "checks out the full history in pack-document-cache-admin" {
            $pack = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-document-cache-admin"

            $pack | Should -Match '(?ms)uses: actions/checkout@\S+[^\r\n]*\r?\n\s+with:\r?\n\s+fetch-depth: 0'
        }

        It "derives the version in pack-document-cache-admin from the dms prefix" {
            $pack = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-document-cache-admin"

            $pack | Should -Match 'Get-VersionNumber -projectPrefix "dms"'
        }

        It "outputs the hash and the DMS version from pack-document-cache-admin" {
            $pack = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-document-cache-admin"

            $pack | Should -Match '(?m)^      hash-code: \S'
            $pack | Should -Match '(?m)^      dms-version: \S'
        }

        It "builds, smokes, hashes and uploads in that order in pack-document-cache-admin" {
            $pack = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-document-cache-admin"
            $build = $pack.IndexOf("./build-dms.ps1 -Command BuildAndPublish")
            $smoke = $pack.IndexOf("eng/ci/Invoke-DocumentCacheAdminPackageSmoke.ps1")
            $hash = $pack.IndexOf("id: hash-code")
            $upload = $pack.IndexOf('name: "${{ env.DOCUMENT_CACHE_ADMIN_PACKAGE_NAME }}-NuGet"')

            $build | Should -BeGreaterThan -1
            $smoke | Should -BeGreaterThan $build
            $hash | Should -BeGreaterThan $smoke
            $upload | Should -BeGreaterThan $hash

            $buildText = $pack.Substring($build, $smoke - $build)
            $buildText | Should -Match '-DMSVersion\b'
            $buildText | Should -Match '-LockedMode\b'

            $smokeText = $pack.Substring($smoke, $hash - $smoke)
            $smokeText | Should -Match '-DMSVersion\b'
            $smokeText | Should -Match '-Configuration Release\b'

            $pack.Substring($upload) | Should -Match 'if-no-files-found: error'
            $pack.Substring($hash, $upload - $hash) | Should -Match 'uses: actions/upload-artifact@'
        }

        It "passes the package target and skips the rebuild in the smoke in pack-document-cache-admin" {
            $pack = Get-WorkflowJobText -Workflow $script:prerelease -Id "pack-document-cache-admin"
            $create = Get-WorkflowStep -Workflow $pack -Name "Create DocumentCacheAdmin NuGet Package"
            $smoke = Get-WorkflowStep -Workflow $pack -Name "Verify Installed DocumentCacheAdmin Package"

            $create | Should -Match '\./build-dms\.ps1(?:[^\r\n]|`\r?\n)*?-PackageTarget DocumentCacheAdmin(?![\w-])'
            $smoke | Should -Match 'Invoke-DocumentCacheAdminPackageSmoke\.ps1(?:[^\r\n]|`\r?\n)*?-SkipPackageBuild(?![\w-])'
        }

        It "creates the SBOM after pack-document-cache-admin and uploads it" {
            $sbom = Get-WorkflowJobText -Workflow $script:prerelease -Id "sbom-create-document-cache-admin"

            @(Get-WorkflowJobNeed -Job $sbom) | Should -Contain "pack-document-cache-admin"
            $sbom | Should -Match '\$\{\{ env\.DOCUMENT_CACHE_ADMIN_PACKAGE_NAME \}\}-SBOM'
            $sbom | Should -Match 'uses: actions/upload-artifact@'
        }

        It "gives the SBOM job read-only contents and checks each tool exit code" {
            $sbom = Get-WorkflowJobText -Workflow $script:prerelease -Id "sbom-create-document-cache-admin"

            $sbom | Should -Not -Match '(?m)^\s+contents:\s*write\b'
            $sbom | Should -Match '(?m)^      contents: read\s*$'
            $sbom | Should -Match '(?m)^      actions: read\s*$'
            $sbom | Should -Match '(?s)dotnet tool install --global Microsoft\.Sbom\.DotNetTool\r?\n\s*if \(\$LASTEXITCODE -ne 0\) \{\s*throw '
            $sbom | Should -Match '(?s)sbom-tool generate(?:[^\r\n]|`\r?\n)*\r?\n\s*if \(\$LASTEXITCODE -ne 0\) \{\s*throw '
        }

        It "creates provenance for the hash pack-document-cache-admin outputs" {
            $provenance = Get-WorkflowJobText -Workflow $script:prerelease -Id "provenance-create-document-cache-admin"

            $provenance | Should -Match 'uses: Ed-Fi-Alliance-OSS/slsa-github-generator/\.github/workflows/generator_generic_slsa3\.yml@v2\.0\.0'
            $provenance | Should -Match 'base64-subjects: \$\{\{ needs\.pack-document-cache-admin\.outputs\.hash-code \}\}'
            $provenance | Should -Match 'provenance-name: edfiApiDocumentCacheAdmin\.intoto\.jsonl'
        }

        It "orders and gates publish-package-document-cache-admin as publish-package-schema-tools is, plus the SBOM and provenance jobs" {
            $publish = Get-WorkflowJobText -Workflow $script:prerelease -Id "publish-package-document-cache-admin"
            $sibling = Get-WorkflowJobText -Workflow $script:prerelease -Id "publish-package-schema-tools"
            # Stricter than the sibling: the package is never pushed when either attestation failed.
            $expected = @(
                @(Get-WorkflowJobNeed -Job $sibling) | ForEach-Object { $_ -replace '^pack-schema-tools$', 'pack-document-cache-admin' }
                "sbom-create-document-cache-admin"
                "provenance-create-document-cache-admin"
            )

            $expected | Should -Not -BeNullOrEmpty
            @(Get-WorkflowJobNeed -Job $publish) | Sort-Object | Should -Be ($expected | Sort-Object)
            Get-JobIf -Job $sibling | Should -Not -BeNullOrEmpty
            Get-JobIf -Job $publish | Should -BeExactly (Get-JobIf -Job $sibling)
            $publish | Should -Match '(?m)^    environment: publishing\r?$'
        }

        It "pushes the package with build-dms.ps1 Push in publish-package-document-cache-admin" {
            $publish = Get-WorkflowJobText -Workflow $script:prerelease -Id "publish-package-document-cache-admin"
            $step = Get-WorkflowStep -Workflow $publish -Name "Push DocumentCacheAdmin Package to Azure Artifacts"

            $step | Should -Match '\./build-dms\.ps1 Push'
        }

        It "attaches the DocumentCacheAdmin artifacts once the release is classified" {
            $attach = Get-WorkflowJobText -Workflow $script:prerelease -Id "attach-document-cache-admin-artifacts-to-release"
            $sibling = Get-WorkflowJobText -Workflow $script:prerelease -Id "attach-dms-artifacts-to-release"

            @(Get-WorkflowJobNeed -Job $attach) | Sort-Object | Should -Be @(
                "classify-release",
                "pack-document-cache-admin",
                "provenance-create-document-cache-admin",
                "publish-package-document-cache-admin",
                "sbom-create-document-cache-admin"
            )
            Get-JobIf -Job $sibling | Should -Not -BeNullOrEmpty
            Get-JobIf -Job $attach | Should -BeExactly (Get-JobIf -Job $sibling)
            $attach | Should -Match 'EdFi\.Api\.DocumentCacheAdmin-SBOM\.zip'
            $attach | Should -Match 'edfiApiDocumentCacheAdmin\.intoto\.jsonl'
        }

        It "lets no other prerelease job wait on a DocumentCacheAdmin job" {
            $jobNames = @(Get-JobName -Workflow $script:prerelease)

            $jobNames | Should -Contain "attach-dms-artifacts-to-release"
            $jobNames | Should -Contain "publish-package-dms"
            $jobNames | Should -Contain "docker-publish-dms"
            foreach ($name in $jobNames | Where-Object { $_ -notin $script:documentCacheAdminJobs }) {
                $job = Get-WorkflowJobText -Workflow $script:prerelease -Id $name
                @(Get-WorkflowJobNeed -Job $job) | Where-Object { $_ -in $script:documentCacheAdminJobs } |
                    Should -BeNullOrEmpty -Because "$name must not wait on a DocumentCacheAdmin job"
            }
        }

        It "promotes EdFi.Api.DocumentCacheAdmin with Invoke-ContractPromotion at the release version" {
            $step = Get-WorkflowStep -Workflow $script:release -Name "Promote EdFi.Api.DocumentCacheAdmin Package"

            $step | Should -Match 'Invoke-ContractPromotion'
            $step | Should -Not -Match 'Invoke-Promote\b'
            $step | Should -Match '-PackageName "EdFi\.Api\.DocumentCacheAdmin"'
            $step | Should -Match '-Version \(\$env:RELEASE_REF -replace ''\^v'', ''''\)'
            $step | Should -Match '-ServiceIndexUrl "\$\{\{ env\.ARTIFACTS_FEED_URL \}\}"'
            $step | Should -Match '-ApiKey \$env:ARTIFACTS_API_KEY'
            ([regex]::Matches($step, '(?m)^\s+ARTIFACTS_USERNAME:')).Count | Should -Be 1
            ([regex]::Matches($step, '(?m)^\s+ARTIFACTS_API_KEY:')).Count | Should -Be 1
        }

        It "promotes DocumentCacheAdmin last among the promote steps" {
            $promoteJob = Get-WorkflowJobText -Workflow $script:release -Id "promote-azure-artifact"
            $ours = $promoteJob.IndexOf("- name: Promote EdFi.Api.DocumentCacheAdmin Package")
            $others = @(
                [regex]::Matches($promoteJob, '(?m)^      - name: Promote (?!EdFi\.Api\.DocumentCacheAdmin Package\s*$).+$') |
                    ForEach-Object { $_.Index }
            )

            $ours | Should -BeGreaterThan -1
            $others | Should -Not -BeNullOrEmpty
            $ours | Should -BeGreaterThan ($others | Measure-Object -Maximum).Maximum
        }
    }
}
