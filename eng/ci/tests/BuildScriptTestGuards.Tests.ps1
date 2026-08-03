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
        # These assertions are anchored on the parsed syntax tree of each script's RunTests, not on
        # raw file text. Text matching over the whole file is satisfied or broken by a comment that
        # merely mentions a name, and it cannot tell which function a match came from.
        BeforeAll {
            function Get-ScriptFunctionAst {
                param(
                    [string] $ScriptPath,
                    [string] $FunctionName
                )

                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $ScriptPath, [ref]$null, [ref]$parseErrors
                )

                # Report a parse failure as itself. Without this the FindAll below returns nothing and
                # the script reads as missing RunTests, which points at the wrong cause.
                if (@($parseErrors).Count -gt 0) {
                    throw "Failed to parse '$ScriptPath': $(@($parseErrors)[0].Message)"
                }

                $functionAst = $ast.FindAll(
                    { param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $FunctionName },
                    $true
                ) | Select-Object -First 1

                if ($null -eq $functionAst) {
                    throw "Function '$FunctionName' was not found in '$ScriptPath'."
                }

                return $functionAst
            }

            function Get-InvokedCommandName {
                param(
                    [System.Management.Automation.Language.Ast] $FunctionAst
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

            function Get-RunTestsAst {
                param(
                    [string] $ScriptFile
                )

                return Get-ScriptFunctionAst `
                    -ScriptPath (Join-Path $script:repoRoot $ScriptFile) `
                    -FunctionName "RunTests"
            }
        }

        It "<ScriptFile> resolves its test assemblies through Get-RequiredTestAssembly" -ForEach @(
            @{ ScriptFile = "build-dms.ps1" }
            @{ ScriptFile = "build-config.ps1" }
        ) {
            Get-InvokedCommandName -FunctionAst (Get-RunTestsAst -ScriptFile $ScriptFile) |
                Should -Contain "Get-RequiredTestAssembly"
        }

        It "<ScriptFile> keeps no private copy of the assembly discovery" -ForEach @(
            @{ ScriptFile = "build-dms.ps1" }
            @{ ScriptFile = "build-config.ps1" }
        ) {
            # Assembly discovery belongs to the helper. A Get-ChildItem back inside RunTests means a
            # local copy of the glob has returned, and with it the chance of a divergent guard.
            Get-InvokedCommandName -FunctionAst (Get-RunTestsAst -ScriptFile $ScriptFile) |
                Should -Not -Contain "Get-ChildItem"
        }

        It "<ScriptFile> no longer reports an empty assembly list as informational" -ForEach @(
            @{ ScriptFile = "build-dms.ps1" }
            @{ ScriptFile = "build-config.ps1" }
        ) {
            # Anchored on real Write-Output commands, so this catches the message in both literal and
            # interpolated form while ignoring any comment that quotes it.
            $writeOutputCalls = @(
                (Get-RunTestsAst -ScriptFile $ScriptFile).FindAll(
                    { param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq "Write-Output" },
                    $true
                )
            )

            @($writeOutputCalls | Where-Object { $_.Extent.Text -like "*no test assemblies found*" }) |
                Should -BeNullOrEmpty
        }
    }
}
