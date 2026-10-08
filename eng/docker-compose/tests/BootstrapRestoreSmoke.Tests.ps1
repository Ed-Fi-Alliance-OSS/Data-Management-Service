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
            foreach ($expected in @("EnvironmentFile", "DatabaseEngine", "Leg", "PackageVersion", "StandardVersion", "SkipSourceSeed", "ResultsPath", "SkipTeardown")) {
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
            $script:smokeContent.Contains('label=com.docker.compose.project=dms-local') | Should -BeTrue
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
}
