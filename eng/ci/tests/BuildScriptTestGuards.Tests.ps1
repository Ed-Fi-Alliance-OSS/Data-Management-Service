# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1301: a test target whose assembly glob matches nothing must not report success. The empty
# match used to be informational, so the run loop executed nothing and the target still exited 0,
# leaving no signal that separated "everything passed" from "nothing ran". The guard lives in
# eng/build-helpers.psm1 so build-dms.ps1 and build-config.ps1 share one copy, and so these specs
# can exercise it directly instead of running either build script.

Describe "Build script zero-test guards (DMS-1301)" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:helpersModule = Join-Path $script:repoRoot "eng/build-helpers.psm1"
        Import-Module $script:helpersModule -Force

        function Add-TestAssemblyFixture {
            param(
                [string] $SolutionRoot,
                [string] $ProjectGroup,
                [string] $ProjectName,
                [string] $Configuration
            )

            $binDirectory = Join-Path $SolutionRoot "$ProjectGroup/$ProjectName/bin/$Configuration"
            New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null

            $assemblyPath = Join-Path $binDirectory "$ProjectName.dll"
            Set-Content -LiteralPath $assemblyPath -Value "test assembly fixture"

            return $assemblyPath
        }
    }

    AfterAll {
        Remove-Module build-helpers -Force -ErrorAction SilentlyContinue
    }

    Context "Get-RequiredTestAssembly rejects an empty glob" {
        BeforeEach {
            $script:solutionRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString())
            New-Item -ItemType Directory -Path $script:solutionRoot -Force | Out-Null
        }

        It "throws when nothing matches, instead of reporting a zero-test run as success" {
            {
                Get-RequiredTestAssembly -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit" -Configuration "Release"
            } | Should -Throw -ExpectedMessage "*no test assemblies found*"
        }

        It "names the searched path and the configuration so the cause is diagnosable" {
            $thrown = $null
            try {
                Get-RequiredTestAssembly -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit" -Configuration "Release"
            }
            catch {
                $thrown = $_.Exception.Message
            }

            $thrown | Should -Not -BeNullOrEmpty
            $thrown | Should -BeLike "*$($script:solutionRoot)*"
            $thrown | Should -BeLike "*`*.Tests.Unit*"
            $thrown | Should -BeLike "*Release*"
        }

        It "throws when assemblies exist only under a different configuration" {
            Add-TestAssemblyFixture -SolutionRoot $script:solutionRoot -ProjectGroup "core" -ProjectName "EdFi.Fixture.Tests.Unit" -Configuration "Debug" | Out-Null

            {
                Get-RequiredTestAssembly -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit" -Configuration "Release"
            } | Should -Throw -ExpectedMessage "*no test assemblies found*"
        }

        It "returns the assembly without throwing when exactly one matches" {
            # Regression guard: the previous emptiness test read .Length on the pipeline output, and a
            # single match is a bare FileInfo whose .Length is the file size in bytes, not a count.
            Add-TestAssemblyFixture -SolutionRoot $script:solutionRoot -ProjectGroup "core" -ProjectName "EdFi.Fixture.Tests.Unit" -Configuration "Release" | Out-Null

            $found = @(Get-RequiredTestAssembly -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit" -Configuration "Release")

            $found.Count | Should -Be 1
            $found[0].Name | Should -Be "EdFi.Fixture.Tests.Unit.dll"
        }

        It "returns every match when several projects match the filter" {
            Add-TestAssemblyFixture -SolutionRoot $script:solutionRoot -ProjectGroup "core" -ProjectName "EdFi.Core.Tests.Unit" -Configuration "Release" | Out-Null
            Add-TestAssemblyFixture -SolutionRoot $script:solutionRoot -ProjectGroup "backend" -ProjectName "EdFi.Backend.Tests.Unit" -Configuration "Release" | Out-Null

            $found = @(Get-RequiredTestAssembly -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit" -Configuration "Release")

            @($found.Name) | Sort-Object | Should -Be @("EdFi.Backend.Tests.Unit.dll", "EdFi.Core.Tests.Unit.dll")
        }

        It "does not match a project whose assembly is outside the filter" {
            Add-TestAssemblyFixture -SolutionRoot $script:solutionRoot -ProjectGroup "core" -ProjectName "EdFi.Core.Tests.Integration" -Configuration "Release" | Out-Null

            {
                Get-RequiredTestAssembly -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit" -Configuration "Release"
            } | Should -Throw -ExpectedMessage "*no test assemblies found*"
        }
    }

    Context "Both build scripts route through the shared guard" {
        BeforeAll {
            $script:buildScripts = @{
                "build-dms.ps1"    = Get-Content -LiteralPath (Join-Path $script:repoRoot "build-dms.ps1") -Raw
                "build-config.ps1" = Get-Content -LiteralPath (Join-Path $script:repoRoot "build-config.ps1") -Raw
            }
        }

        It "<name> resolves its test assemblies through Get-RequiredTestAssembly" -ForEach @(
            @{ Name = "build-dms.ps1" }
            @{ Name = "build-config.ps1" }
        ) {
            $script:buildScripts[$name] | Should -BeLike "*Get-RequiredTestAssembly*"
        }

        It "<name> keeps no private copy of the assembly glob" -ForEach @(
            @{ Name = "build-dms.ps1" }
            @{ Name = "build-config.ps1" }
        ) {
            # The glob and its guard belong to the helper. A reintroduced local copy would drift.
            $script:buildScripts[$name] | Should -Not -BeLike "*Get-ChildItem -Path `$testAssemblyPath*"
        }

        It "<name> no longer treats an empty assembly list as informational" -ForEach @(
            @{ Name = "build-dms.ps1" }
            @{ Name = "build-config.ps1" }
        ) {
            $script:buildScripts[$name] | Should -Not -BeLike "*Write-Output `"no test assemblies found*"
        }
    }
}
