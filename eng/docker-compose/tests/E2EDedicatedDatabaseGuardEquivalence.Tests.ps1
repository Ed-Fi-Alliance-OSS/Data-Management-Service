# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The dedicated-E2E guard decides whether an E2E database name and a protected database name are the
# same database through a private comparer in database-safety.psm1: it removes trailing characters
# from two explicit engine sets, then compares with -ieq. The comparer is not exported, so these tests
# call it through InModuleScope.
#
# Rows a bare -ieq comparison already decides the same way are preservation cases. Rows it gets wrong
# are regression cases, and each asserts that -ieq alone returns false for it, so the row fails if the
# comparer is reduced to -ieq. Characters are written as code points; no literal non-ASCII text.

Describe "Dedicated E2E database name comparer" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:databaseSafetyModulePath = Join-Path $script:dockerComposeRoot "database-safety.psm1"
        Import-Module $script:databaseSafetyModulePath -Force

        function Test-ComparerCollision {
            param(
                [AllowEmptyString()] [string]$E2EDatabaseName,
                [AllowEmptyString()] [string]$ProtectedDatabaseName
            )

            InModuleScope database-safety -Parameters @{ E2E = $E2EDatabaseName; Protected = $ProtectedDatabaseName } {
                param($E2E, $Protected)
                Test-E2EDatabaseNameCollidesWithProtectedName -E2EDatabaseName $E2E -ProtectedDatabaseName $Protected
            }
        }
    }

    Context "trailing characters a bare -ieq comparison misses" {
        It "treats protected 'edfi_dms' followed by <Label> as the E2E database 'edfi_dms'" -ForEach @(
            @{ Label = "U+0020"; Suffix = [string][char]0x0020 }
            @{ Label = "U+3000"; Suffix = [string][char]0x3000 }
            @{ Label = "TAB"; Suffix = "`t" }
            @{ Label = "LF"; Suffix = "`n" }
            @{ Label = "CR"; Suffix = "`r" }
            @{ Label = "FF"; Suffix = "`f" }
            @{ Label = "two U+0020"; Suffix = "  " }
            @{ Label = "U+0020 then TAB"; Suffix = " `t" }
            @{ Label = "U+3000 then U+0020"; Suffix = "$([char]0x3000) " }
            @{ Label = "CR then LF"; Suffix = "`r`n" }
            @{ Label = "U+0020, U+3000 then FF"; Suffix = " $([char]0x3000)`f" }
            @{ Label = "all six characters"; Suffix = " $([char]0x3000)`t`n`r`f" }
        ) {
            $protectedDatabaseName = "edfi_dms$Suffix"

            ("edfi_dms" -ieq $protectedDatabaseName) | Should -BeFalse -Because "this row is a regression case only if -ieq alone calls the names distinct"
            Test-ComparerCollision -E2EDatabaseName "edfi_dms" -ProtectedDatabaseName $protectedDatabaseName | Should -BeTrue
        }

        It "treats protected <Label> as a case variant of the E2E database 'edfi_dms'" -ForEach @(
            @{ Label = "'EDFI_DMS' then U+0020"; ProtectedDatabaseName = "EDFI_DMS " }
            @{ Label = "'EDFI_Dms' then U+3000"; ProtectedDatabaseName = "EDFI_Dms$([char]0x3000)" }
            @{ Label = "'Edfi_Dms' then TAB, LF"; ProtectedDatabaseName = "Edfi_Dms`t`n" }
            @{ Label = "'eDfI_dMs' then U+3000, U+0020, FF"; ProtectedDatabaseName = "eDfI_dMs$([char]0x3000) `f" }
        ) {
            ("edfi_dms" -ieq $ProtectedDatabaseName) | Should -BeFalse -Because "this row is a regression case only if -ieq alone calls the names distinct"
            Test-ComparerCollision -E2EDatabaseName "edfi_dms" -ProtectedDatabaseName $ProtectedDatabaseName | Should -BeTrue
        }

        It "treats an E2E name with the one trailing LF Assert-SafeDatabaseName admits as protected <Label>" -ForEach @(
            @{ Label = "'edfi_dms'"; ProtectedDatabaseName = "edfi_dms" }
            @{ Label = "'EDFI_DMS' then U+3000"; ProtectedDatabaseName = "EDFI_DMS$([char]0x3000)" }
        ) {
            $e2eDatabaseName = "edfi_dms`n"
            { Assert-SafeDatabaseName -DatabaseName $e2eDatabaseName } | Should -Not -Throw -Because "the E2E side of this row must be a name the guard's callers can actually pass"

            ($e2eDatabaseName -ieq $ProtectedDatabaseName) | Should -BeFalse -Because "this row is a regression case only if -ieq alone calls the names distinct"
            Test-ComparerCollision -E2EDatabaseName $e2eDatabaseName -ProtectedDatabaseName $ProtectedDatabaseName | Should -BeTrue
        }
    }

    Context "verdicts a bare -ieq comparison already gives" {
        It "treats protected '<ProtectedDatabaseName>' as the E2E database 'edfi_dms'" -ForEach @(
            @{ ProtectedDatabaseName = "edfi_dms" }
            @{ ProtectedDatabaseName = "EDFI_DMS" }
            @{ ProtectedDatabaseName = "Edfi_Dms" }
        ) {
            Test-ComparerCollision -E2EDatabaseName "edfi_dms" -ProtectedDatabaseName $ProtectedDatabaseName | Should -BeTrue
        }

        It "keeps E2E '<E2EDatabaseName>' distinct from protected '<ProtectedDatabaseName>'" -ForEach @(
            @{ E2EDatabaseName = "edfi_dms_e2e"; ProtectedDatabaseName = "edfi_dms" }
            @{ E2EDatabaseName = "edfi_dms"; ProtectedDatabaseName = "edfi_dms_e2e" }
            @{ E2EDatabaseName = "edfi_dms"; ProtectedDatabaseName = "edfi _dms" }
            @{ E2EDatabaseName = "edfi_dms"; ProtectedDatabaseName = "edfi_dms x" }
        ) {
            Test-ComparerCollision -E2EDatabaseName $E2EDatabaseName -ProtectedDatabaseName $ProtectedDatabaseName | Should -BeFalse
        }

        # A parameterless Trim/TrimEnd, or any test based on char.IsWhiteSpace, would remove these and
        # report a collision. They are outside both explicit sets, so the comparer keeps them.
        It "keeps protected 'edfi_dms' followed by <Label> distinct from the E2E database 'edfi_dms'" -ForEach @(
            @{ Label = "U+00A0"; Suffix = [string][char]0x00A0 }
            @{ Label = "VT"; Suffix = [string][char]0x000B }
            @{ Label = "U+0020 then U+00A0"; Suffix = " $([char]0x00A0)" }
        ) {
            Test-ComparerCollision -E2EDatabaseName "edfi_dms" -ProtectedDatabaseName "edfi_dms$Suffix" | Should -BeFalse
        }

        # Only trailing characters are removed; a Trim in place of TrimEnd would report these as collisions.
        It "keeps protected '<Label>' distinct from the E2E database 'edfi_dms'" -ForEach @(
            @{ Label = "U+0020 then edfi_dms"; ProtectedDatabaseName = " edfi_dms" }
            @{ Label = "U+3000 then edfi_dms"; ProtectedDatabaseName = "$([char]0x3000)edfi_dms" }
            @{ Label = "TAB then edfi_dms"; ProtectedDatabaseName = "`tedfi_dms" }
        ) {
            Test-ComparerCollision -E2EDatabaseName "edfi_dms" -ProtectedDatabaseName $ProtectedDatabaseName | Should -BeFalse
        }

        It "returns false for an empty protected name (<Label>)" -ForEach @(
            @{ Label = "empty"; ProtectedDatabaseName = "" }
            @{ Label = "only removable characters"; ProtectedDatabaseName = " $([char]0x3000)`t`n`r`f" }
        ) {
            Test-ComparerCollision -E2EDatabaseName "edfi_dms" -ProtectedDatabaseName $ProtectedDatabaseName | Should -BeFalse
        }
    }

    Context "module surface and source" {
        BeforeAll {
            $parseErrors = $null
            $moduleAst = [System.Management.Automation.Language.Parser]::ParseFile($script:databaseSafetyModulePath, [ref]$null, [ref]$parseErrors)
            $script:moduleParseErrors = @($parseErrors)
            $script:comparerAst = $moduleAst.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq "Test-E2EDatabaseNameCollidesWithProtectedName"
                }, $true)
        }

        It "does not export the comparer" {
            Get-Command -Module database-safety -Name Test-E2EDatabaseNameCollidesWithProtectedName -ErrorAction SilentlyContinue |
                Should -BeNullOrEmpty
        }

        It "removes characters only with an explicit set, never a parameterless Trim, TrimEnd or TrimStart" {
            $script:moduleParseErrors | Should -BeNullOrEmpty
            $script:comparerAst | Should -Not -BeNullOrEmpty

            $trimCalls = @($script:comparerAst.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                        $node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                        $node.Member.Value -in @("Trim", "TrimEnd", "TrimStart")
                    }, $true))

            $trimCalls.Count | Should -BeGreaterThan 0 -Because "the scan is meaningful only if it finds the comparer's trim calls"
            foreach ($trimCall in $trimCalls) {
                ($null -ne $trimCall.Arguments -and $trimCall.Arguments.Count -gt 0) | Should -BeTrue -Because "'$($trimCall.Extent.Text)' must name the characters it removes"
            }
        }
    }
}
