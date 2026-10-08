# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Static contract for the MANUAL restore smoke (Invoke-BootstrapRestoreSmoke.ps1), mirroring
# the BootstrapDockerSmoke.Tests.ps1 idiom: the live script is never run by CI, but its
# surface and fail-closed invariants are pinned here so a syntax error or a weakened
# assertion becomes a PR failure instead of a silently broken manual tool. Everything below
# is raw-text/AST inspection - no Docker.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Test helper intentionally mirrors the established Get-DeclaredScriptParameters name used by the sibling smoke contract suites.')]
param()

Describe "Invoke-BootstrapRestoreSmoke static contract" {
    BeforeAll {
        $script:smokeScriptPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "Invoke-BootstrapRestoreSmoke.ps1")
        )

        function script:Get-DeclaredScriptParameters {
            param([string]$Path)

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) {
                throw "Failed to parse ${Path}: $($errors[0])"
            }

            return @(
                $ast.ParamBlock.Parameters |
                    ForEach-Object { $_.Name.VariablePath.UserPath } |
                    Select-Object -Unique
            )
        }

        $script:smokeContent = Get-Content -LiteralPath $script:smokeScriptPath -Raw
    }

    Context "Parameter surface" {
        It "declares the smoke parameters" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            foreach ($expected in @("EnvironmentFile", "DatabaseEngine", "Leg", "PackageVersion", "StandardVersion", "DataStandardVersion", "Wrapper", "SkipSourceSeed", "ResultsPath", "SkipTeardown", "ConfirmForeignStackRemoval", "ExploratoryPackage")) {
                $params | Should -Contain $expected
            }
        }

        It "defaults the leg matrix to the core set including every failure leg" {
            foreach ($legName in @("package-directory", "separate-config", "directory-feed", "tampered-package", "contaminated-package", "running-stack")) {
                $script:smokeContent | Should -Match ([regex]::Escape('"' + $legName + '"'))
            }
        }
    }

    Context "Fail-closed invariants" {
        It "registers an ephemeral dev-trust producer instead of any trust bypass" {
            $script:smokeContent.Contains("new-template-dev-trust.ps1") | Should -BeTrue
            # No bypass surface exists in the restore branch and none may be invented here.
            $script:smokeContent | Should -Not -Match '(?i)skip.?attestation|no.?trust|unsigned'
        }

        It "removes exactly the ephemeral producer from the local overlay in the finally block" {
            $script:smokeContent.Contains('$overlay.producers = @($overlay.producers | Where-Object { [string]$_.name -ne $script:SmokeProducerName })') | Should -BeTrue
        }

        It "the tampered-package leg proves the refusal happened before any Docker activity" {
            # The project label follows -Wrapper (dms-local or dms-published).
            $script:smokeContent.Contains('label=com.docker.compose.project=$($script:WrapperProfile.ComposeProject)') | Should -BeTrue
            $script:smokeContent.Contains("Tampered-package refusal happened AFTER Docker activity") | Should -BeTrue
        }

        It "the contaminated-package leg is PostgreSQL-only and asserts target absence, no generated databases, and no committed workspace" {
            $script:smokeContent.Contains("contaminated-package leg is PostgreSQL-only") | Should -BeTrue
            $script:smokeContent.Contains("exists after a failed scratch validation on a fresh volume") | Should -BeTrue
            $script:smokeContent.Contains("Generated restore databases remain after the failure") | Should -BeTrue
            $script:smokeContent.Contains("An active .bootstrap workspace exists after a pre-commit failure") | Should -BeTrue
            # The refusal must come from the DMS-only gate naming the injected schema.
            $script:smokeContent.Contains("smoke_intruder") | Should -BeTrue
        }

        It "the running-stack leg requires the stop proof's own refusal" {
            $script:smokeContent.Contains("still has running containers") | Should -BeTrue
        }

        It "the separate-config leg proves the dedicated CMS database survives via a pre-planted marker" {
            $script:smokeContent.Contains("restore_smoke_marker") | Should -BeTrue
            $script:smokeContent.Contains("edfi_configurationservice") | Should -BeTrue
        }

        It "the directory-feed leg drives feed resolution through the env keys, not -PackageDirectory" {
            $script:smokeContent.Contains("DATABASE_TEMPLATE_FEED_URL=") | Should -BeTrue
            $script:smokeContent.Contains("DATABASE_TEMPLATE_NUGET_VERSION=") | Should -BeTrue
        }

        It "tears down and cleans transient state in the finally block" {
            $script:smokeContent | Should -Match '(?s)finally \{.*Invoke-SmokeTeardown.*Remove-Item -LiteralPath \$script:WorkDirectory'
        }
    }

    Context "Foreign-stack preflight and provenance" {
        It "inventories foreign stacks and refuses before authorizing the first teardown" {
            $inventoryIndex = $script:smokeContent.IndexOf('$inventory = Get-RestoreSmokeForeignStackInventory')
            $assertIndex = $script:smokeContent.IndexOf('Assert-RestoreSmokeNoForeignStack -Inventory $inventory')
            $authorizeIndex = $script:smokeContent.IndexOf('$script:TeardownAuthorized = $true')
            $workDirectoryIndex = $script:smokeContent.IndexOf('$script:WorkDirectory = Join-Path')
            $firstTeardownCallIndex = $script:smokeContent.IndexOf('Invoke-SmokeTeardown -WrapperProfile (Get-RestoreSmokeWrapperProfile')

            $inventoryIndex | Should -BeGreaterThan 0
            $assertIndex | Should -BeGreaterThan $inventoryIndex
            $authorizeIndex | Should -BeGreaterThan $assertIndex
            $workDirectoryIndex | Should -BeGreaterThan $authorizeIndex
            $firstTeardownCallIndex | Should -BeGreaterThan $authorizeIndex
        }

        It "gates every teardown, including the failure teardown in the finally block, on the preflight authorization" {
            $script:smokeContent.Contains('if (-not $script:TeardownAuthorized) {') | Should -BeTrue
            $script:smokeContent | Should -Match '(?s)finally \{\s*if \(-not \$SkipTeardown -and \$exitCode -ne 0\) \{\s*if \(\$script:TeardownAuthorized\) \{'
        }

        It "runs the wrapper and teardown scripts selected by -Wrapper, never a hard-wired local script" {
            $script:smokeContent.Contains('$($script:WrapperProfile.BootstrapScriptName)') | Should -BeTrue
            $script:smokeContent.Contains('$($WrapperProfile.TeardownScriptName)') | Should -BeTrue
            $script:smokeContent | Should -Not -Match '&\s*"\$script:DockerComposeRoot/(start|bootstrap)-local-dms\.ps1"'
            $script:smokeContent.Contains('Get-RestoreSmokeWrapperArgumentSet') | Should -BeTrue
        }

        It "builds SchemaTools and the images in-run and writes the provenance classification to the results" {
            $script:smokeContent.Contains('Invoke-RestoreSmokeSchemaToolBuild') | Should -BeTrue
            $script:smokeContent.Contains('Invoke-RestoreSmokeImageBuild') | Should -BeTrue
            $script:smokeContent.Contains('Get-RestoreSmokeResultClassification -Provenance $script:Provenance') | Should -BeTrue
            $script:smokeContent | Should -Match 'Classification\s*=\s*\$classification'
        }
    }

    Context "In-run image provenance" {
        BeforeAll {
            $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
            $script:probesModulePath = Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1"

            function script:Get-ScriptAst {
                param([string]$Path)

                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) {
                    throw "Failed to parse ${Path}: $($errors[0])"
                }
                return $ast
            }
        }

        It "never runs a build script's DockerBuild, docker tag, or a forced image removal (<file>)" -ForEach @(
            @{ File = "Invoke-BootstrapRestoreSmoke.ps1" }
            @{ File = "RestoreSmokeProbes.psm1" }
        ) {
            $ast = Get-ScriptAst -Path (Join-Path $PSScriptRoot $File)
            $commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
            $commands.Count | Should -BeGreaterThan 0
            foreach ($command in $commands) {
                $elements = @($command.CommandElements | ForEach-Object { $_.Extent.Text })
                $elements | Should -Not -Contain "DockerBuild"
                ($elements -join " ") | Should -Not -Match 'build-(dms|config)\.ps1'
                if ($command.GetCommandName() -eq "docker") {
                    $elements[1] | Should -Not -BeIn @("tag", "rmi", "prune", "system")
                    $elements | Should -Not -Contain "-f"
                    $elements | Should -Not -Contain "--force"
                }
            }
        }

        It "builds <key> exactly as <script> DockerBuild does: source directory, Dockerfile, contexts, and the VERSION default" -ForEach @(
            @{ Key = "Dms"; Script = "build-dms.ps1"; VersionParameter = "DMSVersion"; SourceDirectory = "src/dms" }
            @{ Key = "Config"; Script = "build-config.ps1"; VersionParameter = "DmsCSVersion"; SourceDirectory = "src/config" }
        ) {
            # Drift guard: if a build script changes how it builds the image, the smoke's own
            # command (Get-RestoreSmokeImageBuildPlan) must change with it.
            $ast = Get-ScriptAst -Path (Join-Path $script:repoRoot $Script)
            $versionParameterAst = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $VersionParameter })
            $versionParameterAst.Count | Should -Be 1
            $versionParameterAst[0].DefaultValue | Should -BeOfType [System.Management.Automation.Language.StringConstantExpressionAst]
            $versionDefault = $versionParameterAst[0].DefaultValue.Value
            $versionDefault | Should -Match '^\d+\.\d+\.\d+$'

            $dockerBuild = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "DockerBuild" }, $true))
            $dockerBuild.Count | Should -Be 1
            $body = $dockerBuild[0].Body.Extent.Text
            $body | Should -Match ('Push-Location\s+' + [regex]::Escape("$SourceDirectory/"))
            $body | Should -Match 'docker buildx build\b[^\r\n]*-f Dockerfile \. --build-context parentdir=\.\./'
            $body | Should -Match ('"VERSION=\$' + $VersionParameter + '"')

            Import-Module $script:probesModulePath -Force
            $plan = @(Get-RestoreSmokeImageBuildPlan -RepoRoot $TestDrive -WorkDirectory $TestDrive -RunId "0123456789ab" -Wrapper local | Where-Object { $_.Key -eq $Key })
            $plan.Count | Should -Be 1
            $plan[0].SourceDirectory | Should -Be $SourceDirectory
            ($plan[0].Arguments -join " ") | Should -BeLike "buildx build --load --iidfile * -f Dockerfile . --build-context parentdir=../ --build-arg VERSION=$versionDefault"
        }

        It "records the image ledger before the build step and removes only owned run tags after the failure teardown" {
            $script:smokeContent | Should -Match 'Images\s+=\s+\(New-RestoreSmokeImageLedger\)'
            $script:smokeContent.Contains('Register-RestoreSmokeImageTagOwnership -Plan $imagePlan -Ledger $images') | Should -BeTrue
            $script:smokeContent | Should -Match '(?s)finally \{.*Invoke-SmokeTeardown.*Remove-RestoreSmokeOwnedImageTag -Ledger \$script:Provenance\.Images -Keep:\$SkipTeardown.*Remove-Item -LiteralPath \$script:WorkDirectory'
        }
    }
}
