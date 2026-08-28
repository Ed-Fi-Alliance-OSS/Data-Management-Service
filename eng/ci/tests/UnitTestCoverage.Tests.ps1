# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Specs for the DMS unit-test coverage path. The threshold gate, the project discovery and the
# solution-filter contents are real functions in eng/build-helpers.psm1 and are exercised directly
# here. The two contexts at the end assert over build-dms.ps1's parsed syntax tree and over the
# project files themselves, because "the console driver is gone" and "every unit test project can
# actually report coverage" are invariants with no runtime seam short of a full build.

Describe "Unit test coverage threshold gate" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        Import-Module (Join-Path $script:repoRoot "eng/build-helpers.psm1") -Force

        function Add-CoberturaReportFixture {
            param(
                [string] $LineRate = "0.60",
                [string] $BranchRate = "0.60",
                [switch] $OmitLineRate,
                [switch] $OmitDoctype,
                [switch] $WrongRoot,
                [switch] $Malformed
            )

            $path = Join-Path $TestDrive ([guid]::NewGuid().ToString() + ".xml")

            if ($Malformed) {
                Set-Content -LiteralPath $path -Value "<coverage line-rate=`"0.9`""
                return $path
            }

            # The DOCTYPE is present by default because ReportGenerator writes one, and it is the
            # whole reason this helper cannot read the root through the dotted XML adapter. A fixture
            # without it passes against code that breaks on the real merged report.
            $doctype =
                if ($OmitDoctype) { "" }
                else { "<!DOCTYPE coverage SYSTEM `"http://cobertura.sourceforge.net/xml/coverage-04.dtd`">`n" }

            $lineAttribute = if ($OmitLineRate) { "" } else { " line-rate=`"$LineRate`"" }
            $rootName = if ($WrongRoot) { "report" } else { "coverage" }

            Set-Content -LiteralPath $path -Value (
                "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n" +
                $doctype +
                "<$rootName$lineAttribute branch-rate=`"$BranchRate`"><packages /></$rootName>"
            )

            return $path
        }
    }

    AfterAll {
        Remove-Module build-helpers -Force -ErrorAction SilentlyContinue
    }

    Context "Passing coverage" {
        It "passes when both rates are exactly at the threshold" {
            # 58 is the shipped threshold; an off-by-one here would tighten or loosen the gate for
            # every pull request.
            $path = Add-CoberturaReportFixture -LineRate "0.58" -BranchRate "0.58"

            $measured = Assert-CoverageThreshold -Path $path -Threshold 58

            $measured.LinePercentage | Should -Be 58
            $measured.BranchPercentage | Should -Be 58
            $measured.Threshold | Should -Be 58
        }

        It "passes when both rates are above the threshold" {
            $measured = Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate "0.9012" -BranchRate "0.7") -Threshold 58

            $measured.LinePercentage | Should -Be 90.12
            $measured.BranchPercentage | Should -Be 70
        }
    }

    Context "Failing coverage" {
        It "throws when the line rate is below the threshold" {
            {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate "0.5799" -BranchRate "0.99") -Threshold 58
            } | Should -Throw -ExpectedMessage "*line-rate*"
        }

        It "throws when the branch rate is below the threshold" {
            # The branch total is enforced independently of the line total, as coverlet's
            # --threshold-type line --threshold-type branch did.
            {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate "0.99" -BranchRate "0.10") -Threshold 58
            } | Should -Throw -ExpectedMessage "*branch-rate*"
        }

        It "reports both measured rates so the failure is diagnosable" {
            $thrown = $null
            try {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate "0.42" -BranchRate "0.31") -Threshold 58
            }
            catch {
                $thrown = $_.Exception.Message
            }

            $thrown | Should -BeLike "*42*"
            $thrown | Should -BeLike "*31*"
            $thrown | Should -BeLike "*58*"
        }
    }

    Context "The threshold boundary is exact in both directions" {
        It "throws on a <RateName> that is below the threshold but rounds to 58.00" -ForEach @(
            @{ RateName = 'line-rate'; Line = '0.57995'; Branch = '0.99' }
            @{ RateName = 'line-rate'; Line = '0.579999'; Branch = '0.99' }
            @{ RateName = 'branch-rate'; Line = '0.99'; Branch = '0.57995' }
            @{ RateName = 'branch-rate'; Line = '0.99'; Branch = '0.579999' }
        ) {
            # Comparing display percentages passes these: 57.995 and 57.9999 both round to 58.00, so
            # the gate would accept coverage it is meant to reject. The real merged report carries
            # rates at this precision - line-rate="0.8338407252748307" - so the window is reachable.
            {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate $Line -BranchRate $Branch) -Threshold 58
            } | Should -Throw -ExpectedMessage "*$RateName*"
        }

        It "still passes a rate that is exactly at the threshold" {
            # The other direction of the same boundary. Scaling to a percentage before comparing
            # fails this case, because 0.58 * 100 is 57.99999999999999 as a double.
            $measured = Assert-CoverageThreshold `
                -Path (Add-CoberturaReportFixture -LineRate "0.58" -BranchRate "0.58") -Threshold 58

            $measured.LinePercentage | Should -Be 58
            $measured.BranchPercentage | Should -Be 58
        }

        It "passes a rate a hair above the threshold" {
            $measured = Assert-CoverageThreshold `
                -Path (Add-CoberturaReportFixture -LineRate "0.580001" -BranchRate "0.580001") -Threshold 58

            $measured.LinePercentage | Should -Be 58
        }
    }

    Context "Reports that cannot be evaluated fail rather than pass" {
        It "throws when the report was never produced" {
            # A silently missing report is how a coverage gate stops gating without anyone noticing.
            {
                Assert-CoverageThreshold -Path (Join-Path $TestDrive "never-written.xml") -Threshold 58
            } | Should -Throw -ExpectedMessage "*was not produced*"
        }

        It "throws when the report is not valid XML" {
            {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -Malformed) -Threshold 58
            } | Should -Throw
        }

        It "throws when the report declares no line rate" {
            {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -OmitLineRate) -Threshold 58
            } | Should -Throw -ExpectedMessage "*line-rate*"
        }
    }

    Context "The report shape ReportGenerator actually writes" {
        It "reads the rates from a report carrying a DOCTYPE declaration" {
            # ReportGenerator's merged Cobertura opens with <!DOCTYPE coverage SYSTEM ...>. With that
            # present the dotted adapter returns the DocumentType node alongside the root element, so
            # reading through it threw "[System.String] does not contain a method named GetAttribute"
            # against every real merged report while a DOCTYPE-less fixture passed.
            $measured = Add-CoberturaReportFixture -LineRate "0.8338" -BranchRate "0.7617" |
                ForEach-Object { Assert-CoverageThreshold -Path $_ -Threshold 58 }

            $measured.LinePercentage | Should -Be 83.38
            $measured.BranchPercentage | Should -Be 76.17
        }

        It "also reads a report with no DOCTYPE" {
            # The collector's own per-project reports have none.
            $measured = Add-CoberturaReportFixture -LineRate "0.71" -BranchRate "0.66" -OmitDoctype |
                ForEach-Object { Assert-CoverageThreshold -Path $_ -Threshold 58 }

            $measured.LinePercentage | Should -Be 71
        }

        It "throws when the root element is not <coverage>" {
            {
                Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -WrongRoot) -Threshold 58
            } | Should -Throw -ExpectedMessage "*no <coverage> root element*"
        }
    }

    Context "Rates are parsed independently of the machine's culture" {
        It "still measures 61.5% under a comma-decimal culture" {
            # Culture-sensitive parsing reads "0.615" as 615 on a de-DE machine, and the gate then
            # passes no matter what was measured.
            $originalCulture = [System.Threading.Thread]::CurrentThread.CurrentCulture
            try {
                [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::new('de-DE')

                $measured = Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate "0.615" -BranchRate "0.615") -Threshold 58

                $measured.LinePercentage | Should -Be 61.5
            }
            finally {
                [System.Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
            }
        }

        It "still fails a below-threshold report under a comma-decimal culture" {
            $originalCulture = [System.Threading.Thread]::CurrentThread.CurrentCulture
            try {
                [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::new('de-DE')

                {
                    Assert-CoverageThreshold -Path (Add-CoberturaReportFixture -LineRate "0.10" -BranchRate "0.10") -Threshold 58
                } | Should -Throw
            }
            finally {
                [System.Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
            }
        }
    }
}

Describe "Unit test project discovery and solution filter" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        Import-Module (Join-Path $script:repoRoot "eng/build-helpers.psm1") -Force

        function Add-ProjectFixture {
            param([string] $SolutionRoot, [string] $Group, [string] $ProjectName)

            $directory = Join-Path $SolutionRoot "$Group/$ProjectName"
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $projectPath = Join-Path $directory "$ProjectName.csproj"
            Set-Content -LiteralPath $projectPath -Value "<Project />"

            return $projectPath
        }
    }

    AfterAll {
        Remove-Module build-helpers -Force -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $script:solutionRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $script:solutionRoot -Force | Out-Null
    }

    Context "Get-RequiredUnitTestProject" {
        It "throws when nothing matches, instead of reporting a zero-test run as success" {
            # Same rule Get-RequiredTestAssembly enforces: an empty glob must not look like a pass.
            {
                Get-RequiredUnitTestProject -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit"
            } | Should -Throw -ExpectedMessage "*no test projects found*"
        }

        It "names the searched path so the cause is diagnosable" {
            $thrown = $null
            try {
                Get-RequiredUnitTestProject -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit"
            }
            catch {
                $thrown = $_.Exception.Message
            }

            $thrown | Should -BeLike "*$($script:solutionRoot)*"
        }

        It "finds every matching project across solution groups" {
            Add-ProjectFixture -SolutionRoot $script:solutionRoot -Group "core" -ProjectName "EdFi.Core.Tests.Unit" | Out-Null
            Add-ProjectFixture -SolutionRoot $script:solutionRoot -Group "backend" -ProjectName "EdFi.Backend.Tests.Unit" | Out-Null
            Add-ProjectFixture -SolutionRoot $script:solutionRoot -Group "clis" -ProjectName "EdFi.Cli.Tests.Unit" | Out-Null

            $found = @(Get-RequiredUnitTestProject -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit")

            @($found.Name) | Sort-Object |
                Should -Be @("EdFi.Backend.Tests.Unit.csproj", "EdFi.Cli.Tests.Unit.csproj", "EdFi.Core.Tests.Unit.csproj")
        }

        It "does not pick up integration test projects" {
            Add-ProjectFixture -SolutionRoot $script:solutionRoot -Group "core" -ProjectName "EdFi.Core.Tests.Unit" | Out-Null
            Add-ProjectFixture -SolutionRoot $script:solutionRoot -Group "backend" -ProjectName "EdFi.Backend.Tests.Integration" | Out-Null

            $found = @(Get-RequiredUnitTestProject -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit")

            @($found.Name) | Should -Be @("EdFi.Core.Tests.Unit.csproj")
        }

        It "returns a single match as a collection, not a bare file" {
            Add-ProjectFixture -SolutionRoot $script:solutionRoot -Group "core" -ProjectName "EdFi.Core.Tests.Unit" | Out-Null

            @(Get-RequiredUnitTestProject -SolutionRoot $script:solutionRoot -Filter "*.Tests.Unit").Count | Should -Be 1
        }
    }

    Context "ConvertTo-SolutionFilterContent" {
        It "produces a filter naming the solution and every project" {
            $content = ConvertTo-SolutionFilterContent `
                -SolutionPath "..\src\dms\EdFi.DataManagementService.sln" `
                -ProjectPath @("core\A\A.csproj", "backend\B\B.csproj")

            $parsed = $content | ConvertFrom-Json

            $parsed.solution.path | Should -Be "..\src\dms\EdFi.DataManagementService.sln"
            @($parsed.solution.projects) | Should -Be @("core\A\A.csproj", "backend\B\B.csproj")
        }

        It "keeps a single project as a JSON array" {
            # A bare string here would make the filter invalid and dotnet test would run nothing.
            # Asserted against the JSON text: @() around a parsed scalar would hide exactly the
            # single-element collapse this is guarding against.
            $content = ConvertTo-SolutionFilterContent -SolutionPath "x.sln" -ProjectPath @("core\A\A.csproj")

            $content | Should -Match '"projects"\s*:\s*\['
            @(($content | ConvertFrom-Json).solution.projects).Count | Should -Be 1
        }
    }
}

Describe "build-dms.ps1 unit test coverage wiring" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:buildScript = Join-Path $script:repoRoot "build-dms.ps1"

        $parseErrors = $null
        $script:buildAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $script:buildScript, [ref]$null, [ref]$parseErrors
        )

        if (@($parseErrors).Count -gt 0) {
            throw "Failed to parse '$script:buildScript': $(@($parseErrors)[0].Message)"
        }

        function Get-FunctionAst {
            param([Parameter(Mandatory)] [string] $FunctionName)

            $functionAst = $script:buildAst.FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $FunctionName },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$script:buildScript'."
            }

            return $functionAst
        }

        function Get-InvokedCommandName {
            param([System.Management.Automation.Language.Ast] $FunctionAst)

            return @(
                $FunctionAst.FindAll(
                    { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                    $true
                ) |
                    ForEach-Object { $_.GetCommandName() } |
                    Where-Object { $_ }
            )
        }

        function Get-CommandExtentText {
            param(
                [System.Management.Automation.Language.Ast] $FunctionAst,

                [Parameter(Mandatory)]
                [string] $CommandName
            )

            $commands = $FunctionAst.FindAll(
                { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                $true
            )

            $matching = @($commands | Where-Object { $_.GetCommandName() -eq $CommandName })

            if ($matching.Count -eq 0) {
                throw "No '$CommandName' command was found in the function under test."
            }

            return @($matching | ForEach-Object { $_.Extent.Text })
        }
    }

    Context "The coverlet console driver is gone" {
        It "no function in the build script invokes the coverlet tool" {
            # Anchored on real command invocations rather than file text, so a comment mentioning
            # coverlet does not satisfy or break this.
            $coverletInvocations = @(
                $script:buildAst.FindAll(
                    { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                    $true
                ) |
                    Where-Object { $_.Extent.Text -match '(?m)^\s*dotnet tool run coverlet\b' }
            )

            $coverletInvocations | Should -BeNullOrEmpty
        }

        It "the unit test path no longer decides coverage from whichever assembly sorted last" {
            Get-InvokedCommandName -FunctionAst (Get-FunctionAst 'RunUnitTestsWithCoverage') |
                Should -Not -Contain 'Get-RequiredTestAssembly'
        }

        It "resolves its projects through the shared zero-test guard" {
            # The DMS-1301 invariant carried onto the new path: a raw Get-ChildItem here would let an
            # empty glob report a zero-test run as success, which is what that guard exists to stop.
            $invoked = Get-InvokedCommandName -FunctionAst (Get-FunctionAst 'RunUnitTestsWithCoverage')

            $invoked | Should -Contain 'Get-RequiredUnitTestProject'
            $invoked | Should -Not -Contain 'Get-ChildItem'
        }
    }

    Context "The unit test path uses the collector, merges, and enforces the threshold" {
        It "collects XPlat Code Coverage with the repository runsettings" {
            $dotnetCalls = Get-CommandExtentText -FunctionAst (Get-FunctionAst 'RunUnitTestsWithCoverage') -CommandName 'dotnet'
            $testCall = @($dotnetCalls | Where-Object { $_ -match '\bdotnet test\b' })

            $testCall.Count | Should -Be 1
            $testCall[0] | Should -Match 'XPlat Code Coverage'
            $testCall[0] | Should -Match 'coverlet\.runsettings'
        }

        It "keeps console and trx logging for CI feedback" {
            $dotnetCalls = Get-CommandExtentText -FunctionAst (Get-FunctionAst 'RunUnitTestsWithCoverage') -CommandName 'dotnet'
            $testCall = @($dotnetCalls | Where-Object { $_ -match '\bdotnet test\b' })[0]

            $testCall | Should -Match '"trx"'
            $testCall | Should -Match '"console"'
        }

        It "merges the per-project reports with reportgenerator" {
            $extents = Get-CommandExtentText -FunctionAst (Get-FunctionAst 'RunUnitTestsWithCoverage') -CommandName 'dotnet'

            @($extents | Where-Object { $_ -match 'reportgenerator' -and $_ -match 'reporttypes:Cobertura' }).Count |
                Should -Be 1
        }

        It "enforces the threshold through the shared helper" {
            Get-InvokedCommandName -FunctionAst (Get-FunctionAst 'RunUnitTestsWithCoverage') |
                Should -Contain 'Assert-CoverageThreshold'
        }

        It "still routes the unit filter through the coverage path" {
            Get-InvokedCommandName -FunctionAst (Get-FunctionAst 'RunTests') |
                Should -Contain 'RunUnitTestsWithCoverage'
        }
    }

    Context "ReportGenerator receives its switches as whole arguments" {
        It "<FunctionName> passes no dangling switch to reportgenerator" -ForEach @(
            @{ FunctionName = 'RunUnitTestsWithCoverage' }
            @{ FunctionName = 'Invoke-Coverage' }
        ) {
            # `dotnet tool run <tool> --` requires a `--`, and after one PowerShell emits a
            # `-name:"value"` argument as two arguments: `-name:` and the value on its own.
            # ReportGenerator then reports "No report files specified" and writes nothing, for an
            # invocation that reads correctly in the source. The signature of the broken form is an
            # argument whose text is just the switch, ending in a colon; the whole `-name:value`
            # token has to be quoted together. Verified against ReportGenerator 5.5.11 under both
            # $PSNativeCommandArgumentPassing modes.
            $reportGeneratorCalls = @(
                (Get-FunctionAst $FunctionName).FindAll(
                    { param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                        $node.Extent.Text -match 'reportgenerator' },
                    $true
                )
            )

            $reportGeneratorCalls.Count | Should -BeGreaterThan 0

            $danglingSwitches = @(
                $reportGeneratorCalls |
                    ForEach-Object { $_.CommandElements } |
                    Where-Object { $_.Extent.Text -match '^-{1,2}[a-zA-Z]+:$' } |
                    ForEach-Object { $_.Extent.Text }
            )

            $danglingSwitches | Should -BeNullOrEmpty
        }

        It "Invoke-Coverage fails the command when reportgenerator fails" {
            # Unwrapped, a non-zero exit was swallowed and the script still reported success, so a
            # coverage report that was never written looked exactly like one nobody opened.
            Get-InvokedCommandName -FunctionAst (Get-FunctionAst 'Invoke-Coverage') |
                Should -Contain 'Invoke-Execute'
        }
    }

    Context "A failed run cannot leave the previous run's report behind" {
        It "clears the root coverage report before collecting" {
            # The workflow gates its report step on hashFiles('coverage.cobertura.xml'). A stale file
            # there would be published as this run's coverage after a run that never got to merge.
            $removals = @(
                (Get-FunctionAst 'RunUnitTestsWithCoverage').FindAll(
                    { param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq 'Remove-Item' },
                    $true
                ) | ForEach-Object { $_.Extent.Text }
            )

            @($removals | Where-Object { $_ -match '\$coverageOutputFile' }) | Should -Not -BeNullOrEmpty
        }
    }

    Context "The shipped threshold is unchanged" {
        It "still enforces 58" {
            $assignment = $script:buildAst.FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left.Extent.Text -eq '$thresholdCoverage' },
                $true
            ) | Select-Object -First 1

            $assignment | Should -Not -BeNullOrEmpty
            $assignment.Right.Extent.Text | Should -Be '58'
        }

        It "still writes the root coverage report the workflow and Coverage command look for" {
            $assignment = $script:buildAst.FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left.Extent.Text -eq '$coverageOutputFile' },
                $true
            ) | Select-Object -First 1

            $assignment.Right.Extent.Text | Should -Be '"coverage.cobertura.xml"'
        }
    }
}

Describe "Unit test projects can report coverage" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:unitTestProject = @(
            Get-ChildItem -Path (Join-Path $script:repoRoot "src/dms/*/*.Tests.Unit/*.Tests.Unit.csproj")
        )
    }

    It "finds the DMS unit test projects" {
        $script:unitTestProject.Count | Should -BeGreaterThan 0
    }

    It "every unit test project references coverlet.collector" {
        # The collector is loaded from the test assembly's own output. A unit test project without
        # this reference runs its tests and contributes nothing to coverage - silently moving the
        # measured total that the 58% gate is applied to.
        $missing = @(
            $script:unitTestProject | Where-Object {
                [xml] $project = Get-Content -LiteralPath $_.FullName -Raw

                $null -eq (
                    $project.Project.ItemGroup.PackageReference |
                        Where-Object { $_.Include -eq 'coverlet.collector' }
                )
            } | ForEach-Object { $_.Name }
        )

        $missing | Should -BeNullOrEmpty
    }
}
