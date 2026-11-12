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
# Each row is one of four kinds. The baseline is the guard as it was before the behaviors these tests
# cover: it compared with bare -ieq, had no domain or length check, ran the whole resolved connection
# string and then each value parsed from it through the dotenv conversion again, and its provisioning
# reader kept an 'export ' prefix in the key.
# - Regression: the row fails if one named behavior is reverted to that baseline. Contexts say which:
#   the comparer reduced to -ieq (the context named for what -ieq misses), the domain check removed,
#   the length bound removed, the whole-string or per-value conversion restored, or the export prefix
#   kept.
# - Preservation: the baseline already rejected the configuration, and the row keeps that verdict
#   (the contexts named for verdicts the guard already gives).
# - Positive control: an accepted configuration. It passes with or without the fix and is not
#   regression evidence; it shows that a regression row's rejection is specific to its input.
# - Harness check: module surface, ambient isolation and child-process plumbing; not regression
#   evidence either.
# Characters are written as code points; no literal non-ASCII text.
#
# Comparison-domain rows are classified per key kind by the verdict the guard gives with only the
# domain check removed: a spelling it would accept is a regression row whose only rejection is the
# domain refusal; a spelling the collision check already rejects is a preservation row that accepts
# either the collision or the domain diagnostic for its key. E2E-name domain rows are classified the
# same way.

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

    Context "module surface and source (harness check)" {
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

        It "does not export <Name>" -ForEach @(
            @{ Name = "Test-E2EDatabaseNameCollidesWithProtectedName" }
            @{ Name = "Test-E2EGuardComparisonDomain" }
        ) {
            Get-Command -Module database-safety -Name $Name -ErrorAction SilentlyContinue |
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

# The domain predicate removes nothing, so these rows reach it directly; through the guard a trailing
# LF or U+0020 would already be gone.
Describe "Dedicated E2E guard comparison domain" {
    BeforeAll {
        Import-Module (Join-Path ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))) "database-safety.psm1") -Force

        function Test-ComparisonDomain {
            param([AllowEmptyString()] [string]$DatabaseName)

            InModuleScope database-safety -Parameters @{ Name = $DatabaseName } {
                param($Name)
                Test-E2EGuardComparisonDomain -DatabaseName $Name
            }
        }
    }

    It "accepts '<DatabaseName>'" -ForEach @(
        @{ DatabaseName = "edfi_dms" }
        @{ DatabaseName = "EDFI_DMS" }
        @{ DatabaseName = "Az09_zA" }
        @{ DatabaseName = "_" }
    ) {
        Test-ComparisonDomain -DatabaseName $DatabaseName | Should -BeTrue
    }

    It "refuses <Label>" -ForEach @(
        @{ Label = "an empty name"; DatabaseName = "" }
        @{ Label = "'edfi_dms' + LF, which a '`$'-anchored pattern admits"; DatabaseName = "edfi_dms`n" }
        @{ Label = "'edfi' + LF + 'dms'"; DatabaseName = "edfi`ndms" }
        @{ Label = "'edfi_dms' + LF + 'x'"; DatabaseName = "edfi_dms`nx" }
        @{ Label = "'edfi_dms' + U+0020"; DatabaseName = "edfi_dms " }
        @{ Label = "U+0020 + 'edfi_dms'"; DatabaseName = " edfi_dms" }
    ) {
        Test-ComparisonDomain -DatabaseName $DatabaseName | Should -BeFalse
    }

    # The code points on either side of each range, and characters a case-insensitive or culture-aware
    # match would place inside it.
    It "refuses 'edfi' + U+<CodePoint> + 'dms'" -ForEach @(
        foreach ($codePoint in @(0x002F, 0x003A, 0x0040, 0x005B, 0x0060, 0x007B, 0x002D, 0x002E, 0x0020, 0x007F, 0x0001, 0x00A0, 0x200B, 0x200D, 0x2060, 0xFEFF, 0x212A, 0x0130, 0x0131, 0xFF45, 0xFB01)) {
            @{ CodePoint = $codePoint.ToString("X4"); Character = [string][char]$codePoint }
        }
    ) {
        Test-ComparisonDomain -DatabaseName "edfi$($Character)dms" | Should -BeFalse
    }

    It "refuses 'edfi_dms' + U+1F600, a surrogate pair" {
        Test-ComparisonDomain -DatabaseName "edfi_dms$([char]::ConvertFromUtf32(0x1F600))" | Should -BeFalse
    }
}

BeforeDiscovery {
    $script:directDatabaseNameKeys = @("MSSQL_DB_NAME", "POSTGRES_DB_NAME")
    $script:connectionStringKeys = @("DATABASE_CONNECTION_STRING_ADMIN", "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING")

    # Spellings of the protected name 'edfi_dms' that differ only by case and a trailing run from the
    # explicit sets. A bare -ieq calls every one of them distinct from 'edfi_dms'.
    $script:suffixedSpellings = @(
        @{ Label = "edfi_dms + U+0020"; Name = "edfi_dms " }
        @{ Label = "edfi_dms + U+3000"; Name = "edfi_dms$([char]0x3000)" }
        @{ Label = "edfi_dms + TAB"; Name = "edfi_dms`t" }
        @{ Label = "edfi_dms + LF"; Name = "edfi_dms`n" }
        @{ Label = "edfi_dms + CR"; Name = "edfi_dms`r" }
        @{ Label = "edfi_dms + FF"; Name = "edfi_dms`f" }
        @{ Label = "EDFI_DMS + U+0020"; Name = "EDFI_DMS " }
        @{ Label = "Edfi_Dms + U+3000, U+0020, TAB"; Name = "Edfi_Dms$([char]0x3000) `t" }
    )
    # A dotenv line cannot carry LF or CR, so file-reader rows use the subset a line can hold.
    $script:fileSpellings = @($script:suffixedSpellings | Where-Object { $_.Name -notmatch "[`r`n]" })

    $script:quotedRawMapRows = foreach ($key in $script:directDatabaseNameKeys) {
        foreach ($quote in @("'", '"')) {
            foreach ($spelling in $script:suffixedSpellings) {
                @{ Key = $key; Quote = $quote; Label = $spelling.Label; RawValue = "$quote$($spelling.Name)$quote" }
            }
        }
    }

    $script:ambientRows = foreach ($key in $script:directDatabaseNameKeys) {
        foreach ($spelling in $script:suffixedSpellings) {
            @{ Key = $key; Label = $spelling.Label; AmbientValue = $spelling.Name }
        }
    }

    $script:fileReaderRows = foreach ($reader in @("ReadValuesFromEnvFile", "Get-EnvironmentValueMap")) {
        foreach ($key in $script:directDatabaseNameKeys) {
            foreach ($quote in @("'", '"')) {
                foreach ($spelling in $script:fileSpellings) {
                    @{ Reader = $reader; Key = $key; Quote = $quote; Label = $spelling.Label; Line = "$key=$quote$($spelling.Name)$quote" }
                }
            }
        }
    }

    # Spellings outside the comparison domain, with the verdict the guard gives each with only the
    # domain check removed, measured separately for direct keys (a double-quoted value) and
    # connection-string keys (a provider-quoted Database). The extractor returns the parsed value
    # unchanged, so a trailing U+00A0 or a leading U+0020 reaches the domain check on both.
    $script:outOfDomainSpellings = @(
        @{ Label = "full-width U+FF45 for the first letter"; Name = "$([char]0xFF45)dfi_dms"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi_dms + U+1F600"; Name = "edfi_dms$([char]::ConvertFromUtf32(0x1F600))"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi_dms + U+00A0"; Name = "edfi_dms$([char]0x00A0)"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "U+0020 + edfi_dms"; Name = " edfi_dms"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi-dms"; Name = "edfi-dms"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi.dms"; Name = "edfi.dms"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi + U+0020 + dms"; Name = "edfi dms"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "ed + U+FB01 + _dms"; Name = "ed$([char]0xFB01)_dms"; Direct = "Collision"; ConnectionString = "Collision" }
        @{ Label = "edfi + U+200D + _dms"; Name = "edfi$([char]0x200D)_dms"; Direct = "Collision"; ConnectionString = "Collision" }
        @{ Label = "edfi + U+2060 + _dms"; Name = "edfi$([char]0x2060)_dms"; Direct = "Collision"; ConnectionString = "Collision" }
        @{ Label = "edfi + U+FEFF + _dms"; Name = "edfi$([char]0xFEFF)_dms"; Direct = "Collision"; ConnectionString = "Collision" }
        @{ Label = "edfi + U+0001 + _dms"; Name = "edfi$([char]0x0001)_dms"; Direct = "Collision"; ConnectionString = "Collision" }
        @{ Label = "edfi_dms + U+200B"; Name = "edfi_dms$([char]0x200B)"; Direct = "Collision"; ConnectionString = "Collision" }
    )

    $script:outOfDomainRows = foreach ($spelling in $script:outOfDomainSpellings) {
        foreach ($key in $script:directDatabaseNameKeys) {
            @{ Key = $key; Label = $spelling.Label; Value = "`"$($spelling.Name)`""; Verdict = $spelling.Direct }
        }
        foreach ($key in $script:connectionStringKeys) {
            @{ Key = $key; Label = $spelling.Label; Value = "Server=s;Database=`"$($spelling.Name)`";User Id=sa"; Verdict = $spelling.ConnectionString }
        }
    }
    $script:domainRefusalRows = @($script:outOfDomainRows | Where-Object { $_.Verdict -eq "Accepted" })
    $script:alreadyRejectedRows = @($script:outOfDomainRows | Where-Object { $_.Verdict -eq "Collision" })

    # Connection-string stages: the raw env value, the value the generic DbConnectionStringBuilder
    # parses from the Compose-resolved string, the candidate the extractor returns, and the guard's
    # verdict against the E2E database 'edfi_dms'. The extractor returns the parsed value unchanged,
    # so Parsed and Candidates agree on every row. The pinned SqlClient 6.1.4 and Npgsql 8.0.4
    # builders were measured outside CI (the lane has no restored NuGet cache) to trim an unquoted
    # value and keep a quoted one verbatim, as the generic parse does in the whitespace rows; for two
    # keywords SqlClient keeps the later Initial Catalog and Npgsql the later DB.
    #
    # Kind follows the file header, judged with only the per-value dotenv conversion restored. With it,
    # the guard accepted the two regression rows, because the conversion turned 'edfi$dms' into 'edfi'
    # and 'other #x' into 'other'; it still rejected every preservation row, some with another
    # diagnostic, and those rows then fail only their Candidates assertion. The full-width U+FF45 row
    # is judged against the domain check instead: the conversion leaves it unchanged, -ieq calls it
    # distinct from 'edfi_dms', and only the domain check rejects it. The in-domain rows are 63
    # characters long, the longest name the guard accepts.
    $inDomainName = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"
    $script:connectionStringStageSpellings = @(
        @{ Label = "unquoted Database 'edfi_dms' + U+0020"; Raw = "Server=s;Database=edfi_dms ;User Id=sa"; Parsed = @("edfi_dms"); Verdict = "Collision"; Kind = "preservation" }
        @{ Label = "double-quoted Database 'edfi_dms' + U+0020"; Raw = "Server=s;Database=`"edfi_dms `";User Id=sa"; Parsed = @("edfi_dms "); Verdict = "Collision"; Kind = "preservation" }
        @{ Label = "single-quoted Database 'edfi_dms' + U+0020"; Raw = "Server=s;Database='edfi_dms ';User Id=sa"; Parsed = @("edfi_dms "); Verdict = "Collision"; Kind = "preservation" }
        @{ Label = "double-quoted Database 'edfi_dms' + U+3000"; Raw = "Server=s;Database=`"edfi_dms$([char]0x3000)`";User Id=sa"; Parsed = @("edfi_dms$([char]0x3000)"); Verdict = "Collision"; Kind = "preservation" }
        @{ Label = "double-quoted Database 'edfi_dms' + TAB"; Raw = "Server=s;Database=`"edfi_dms`t`";User Id=sa"; Parsed = @("edfi_dms`t"); Verdict = "Collision"; Kind = "preservation" }
        @{ Label = "double-quoted Database 'edfi_dms' + U+00A0"; Raw = "Server=s;Database=`"edfi_dms$([char]0x00A0)`";User Id=sa"; Parsed = @("edfi_dms$([char]0x00A0)"); Verdict = "Domain"; Kind = "preservation" }
        @{ Label = "double-quoted Database U+0020 + 'edfi_dms'"; Raw = "Server=s;Database=`" edfi_dms`";User Id=sa"; Parsed = @(" edfi_dms"); Verdict = "Domain"; Kind = "preservation" }
        @{ Label = "Database with quotes inside the value"; Raw = "Server=s;Database=`"'edfi_dms'`";User Id=sa"; Parsed = @("'edfi_dms'"); Verdict = "Domain"; Kind = "preservation" }
        @{ Label = "Database 'edfi_dms #x' kept by single outer quotes"; Raw = "'Server=s;Database=`"edfi_dms #x`";User Id=sa'"; Parsed = @("edfi_dms #x"); Verdict = "Domain"; Kind = "preservation" }
        @{ Label = "Database literal `${GUARD_STAGE_NAME} with GUARD_STAGE_NAME = 'edfi_dms'"; Raw = "Server=s;Database=`$`${GUARD_STAGE_NAME};User Id=sa"; StageName = "edfi_dms"; Parsed = @("`${GUARD_STAGE_NAME}"); Verdict = "Dollar"; Kind = "preservation" }
        @{ Label = "Database literal `${GUARD_STAGE_NAME} with GUARD_STAGE_NAME undefined"; Raw = "Server=s;Database=`$`${GUARD_STAGE_NAME};User Id=sa"; Parsed = @("`${GUARD_STAGE_NAME}"); Verdict = "Dollar"; Kind = "preservation" }
        @{ Label = "Database with a '`$' inside single quotes"; Raw = "Server=s;Database=`"'edfi`$`$x'`";User Id=sa"; Parsed = @("'edfi`$x'"); Verdict = "Dollar"; Kind = "preservation" }
        @{ Label = "Database 'edfi`$dms'"; Raw = "Server=s;Database=edfi`$`$dms;User Id=sa"; Parsed = @("edfi`$dms"); Verdict = "Dollar"; Kind = "regression: per-value conversion" }
        @{ Label = "Database 'other #x' kept by single outer quotes"; Raw = "'Server=s;Database=other #x;User Id=sa'"; Parsed = @("other #x"); Verdict = "Domain"; Kind = "regression: per-value conversion" }
        @{ Label = "unquoted Database of every in-domain character"; Raw = "Server=s;Database=$inDomainName;User Id=sa"; Parsed = @($inDomainName); Verdict = "Accepted"; Kind = "positive control" }
        @{ Label = "double-quoted DB of every in-domain character"; Raw = "Host=h;DB=`"$inDomainName`";Username=u"; Parsed = @($inDomainName); Verdict = "Accepted"; Kind = "positive control" }
        @{ Label = "single-quoted Initial Catalog of every in-domain character"; Raw = "Server=s;Initial Catalog='$inDomainName';User Id=sa"; Parsed = @($inDomainName); Verdict = "Accepted"; Kind = "positive control" }
        @{ Label = "double-quoted Database with full-width U+FF45"; Raw = "Server=s;Database=`"$([char]0xFF45)dfi_dms`";User Id=sa"; Parsed = @("$([char]0xFF45)dfi_dms"); Verdict = "Domain"; Kind = "regression: domain check removed" }
        @{ Label = "Database 'first' then Initial Catalog 'edfi_dms', which SqlClient uses"; Raw = "Server=s;Database=first;Initial Catalog=edfi_dms;User Id=sa"; Parsed = @("first", "edfi_dms"); Verdict = "Collision"; Kind = "preservation" }
        @{ Label = "Database 'edfi_dms' then DB 'other', which Npgsql uses"; Raw = "Host=h;Database=edfi_dms;DB=other;Username=u"; Parsed = @("edfi_dms", "other"); Verdict = "Collision"; Kind = "preservation" }
    )
    $script:connectionStringStageRows = foreach ($spelling in $script:connectionStringStageSpellings) {
        foreach ($key in $script:connectionStringKeys) {
            $row = $spelling.Clone()
            if (-not $row.ContainsKey("StageName")) {
                $row["StageName"] = $null
            }
            $row["Key"] = $key
            $row
        }
    }
}

# Guard-level matrix for Assert-E2EDatabaseIsDedicated. Every row runs through Invoke-IsolatedGuard,
# which clears the four protected keys and any helper variable the row names from the process
# environment and restores them in finally, so an ambient value on the machine or a row's own ambient
# value never leaks into another row. Rejections assert the full, unchanged diagnostic.
Describe "Dedicated E2E database guard" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        # Dot-source provision (its dot-source guard returns before any provisioning) for its
        # Get-EnvironmentValueMap reader; the script also imports env-utility.psm1, which provides
        # ReadValuesFromEnvFile, and database-safety.psm1.
        . (Join-Path $script:dockerComposeRoot "provision-e2e-database.ps1")

        $script:protectedKeys = @(
            "POSTGRES_DB_NAME",
            "MSSQL_DB_NAME",
            "DATABASE_CONNECTION_STRING_ADMIN",
            "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"
        )
        $script:environmentFileLabel = ".env.guard-matrix"

        # PowerShell passes $null to a .NET string parameter as "", so SetEnvironmentVariable($name,
        # $null) leaves the variable defined and empty, which the guard reads as a configured blank key.
        # Absence is therefore recorded and restored with Remove-Item.
        function Save-AmbientVariable {
            param([string]$Name)
            @{ Name = $Name; Exists = (Test-Path -LiteralPath "Env:$Name"); Value = [System.Environment]::GetEnvironmentVariable($Name) }
        }

        function Restore-AmbientVariable {
            param([hashtable]$Saved)
            if ($Saved.Exists) {
                [System.Environment]::SetEnvironmentVariable($Saved.Name, $Saved.Value)
            }
            else {
                Remove-Item -LiteralPath "Env:$($Saved.Name)" -ErrorAction SilentlyContinue
            }
        }

        function Invoke-IsolatedGuard {
            param(
                [hashtable]$EnvironmentValues = @{},
                [hashtable]$AmbientValues = @{},
                [string[]]$HelperVariables = @(),
                [string]$E2EDatabaseName = "edfi_dms"
            )

            $isolatedNames = @($script:protectedKeys + $HelperVariables + @($AmbientValues.Keys)) | Select-Object -Unique
            $savedVariables = foreach ($name in $isolatedNames) {
                Save-AmbientVariable -Name $name
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }

            try {
                foreach ($name in $AmbientValues.Keys) {
                    [System.Environment]::SetEnvironmentVariable($name, $AmbientValues[$name])
                }

                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues $EnvironmentValues `
                    -EnvironmentFilePath $script:environmentFileLabel `
                    -E2EDatabaseName $E2EDatabaseName
            }
            finally {
                foreach ($savedVariable in $savedVariables) {
                    Restore-AmbientVariable -Saved $savedVariable
                }
            }
        }

        function Get-DedicatedDiagnostic {
            param([string]$Key, [string]$E2EDatabaseName = "edfi_dms")
            "E2E database '$E2EDatabaseName' in '$script:environmentFileLabel' must be dedicated and cannot match $Key."
        }

        function Get-SeparateDiagnostic {
            param([string]$Key, [string]$E2EDatabaseName = "edfi_dms")
            "E2E database '$E2EDatabaseName' in '$script:environmentFileLabel' must stay separate from $Key."
        }

        function Get-CollisionDiagnostic {
            param([string]$Key)
            if ($Key -in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) {
                Get-DedicatedDiagnostic -Key $Key
            }
            else {
                Get-SeparateDiagnostic -Key $Key
            }
        }

        $script:removedTrailingCharacters = "U+0020, U+3000, U+0009, U+000A, U+000D and U+000C"

        function Get-ProtectedNameSource {
            param([string]$Key)
            if ($Key -in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) { "the protected setting $Key" } else { "the connection-string database name from $Key" }
        }

        function Get-DomainDiagnostic {
            param([string]$Key)
            "E2E database safety check refuses $(Get-ProtectedNameSource -Key $Key) in '$script:environmentFileLabel' as an unsupported protected database name: after trailing $script:removedTrailingCharacters are removed, a protected name must be 1 to 63 ASCII letters, digits or underscores, and this one contains another character; refusing a destructive reset. The value is withheld."
        }

        function Get-E2EDomainDiagnostic {
            param([string]$E2EDatabaseName, [string]$EnvironmentFilePath = $script:environmentFileLabel)
            "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' cannot be proven dedicated: after trailing $script:removedTrailingCharacters are removed it contains characters outside the guard's supported comparison domain (ASCII letters, digits, underscore); refusing a destructive reset."
        }

        function Get-LengthDiagnostic {
            param([string]$Key)
            "E2E database safety check refuses $(Get-ProtectedNameSource -Key $Key) in '$script:environmentFileLabel' as an unsupported protected database name: after trailing $script:removedTrailingCharacters are removed, a protected name must be 1 to 63 ASCII letters, digits or underscores, and this one is longer than 63 characters (PostgreSQL keeps only the first 63 bytes of a database name); refusing a destructive reset. The value is withheld."
        }

        function Get-E2ELengthDiagnostic {
            param([string]$E2EDatabaseName)
            "E2E database '$E2EDatabaseName' in '$script:environmentFileLabel' cannot be proven dedicated: after trailing $script:removedTrailingCharacters are removed it is longer than 63 characters, and PostgreSQL keeps only the first 63 bytes of a database name; refusing a destructive reset."
        }

        function Get-GuardRejectionMessage {
            param([hashtable]$EnvironmentValues, [string]$E2EDatabaseName = "edfi_dms")
            try {
                Invoke-IsolatedGuard -EnvironmentValues $EnvironmentValues -E2EDatabaseName $E2EDatabaseName
            }
            catch {
                return $_.Exception.Message
            }
            throw "The guard accepted a configuration it must reject."
        }

        # Resolves the key the way the guard does, then returns the extractor's candidates, with the
        # protected keys cleared from the process environment and restored afterward.
        function Get-IsolatedConnectionStringDatabaseName {
            param([string]$Key, [string]$RawValue)

            $savedVariables = foreach ($name in $script:protectedKeys) {
                Save-AmbientVariable -Name $name
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }

            try {
                $environmentValues = @{ $Key = $RawValue }
                $connectionString = Get-ComposeResolvedEnvValue -EnvironmentValues $environmentValues -Name $Key
                Get-DatabaseNameFromConnectionString -ConnectionString $connectionString
            }
            finally {
                foreach ($savedVariable in $savedVariables) {
                    Restore-AmbientVariable -Saved $savedVariable
                }
            }
        }

        # Returns the database-name values the generic DbConnectionStringBuilder parses from the
        # resolved string, in key order, beside the extractor's candidates, with the protected keys
        # and the row's helper variables isolated from the process environment.
        function Get-IsolatedConnectionStringStage {
            param([string]$Key, [hashtable]$EnvironmentValues, [string[]]$HelperVariables = @())

            $savedVariables = foreach ($name in @($script:protectedKeys + $HelperVariables)) {
                Save-AmbientVariable -Name $name
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }

            try {
                $connectionString = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $Key
                $connectionStringBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
                $connectionStringBuilder.PSBase.ConnectionString = $connectionString
                [pscustomobject]@{
                    Parsed = @(
                        foreach ($builderKey in $connectionStringBuilder.PSBase.Keys) {
                            if ($builderKey -imatch '^(database|db|initial catalog)$') {
                                [string]$connectionStringBuilder.PSBase.get_Item($builderKey)
                            }
                        }
                    )
                    Candidates = @(Get-DatabaseNameFromConnectionString -ConnectionString $connectionString)
                }
            }
            finally {
                foreach ($savedVariable in $savedVariables) {
                    Restore-AmbientVariable -Saved $savedVariable
                }
            }
        }
    }

    Context "direct keys: trailing runs a bare -ieq comparison misses" {
        It "rejects <Key> = <Quote><Label><Quote> from a raw map" -ForEach $script:quotedRawMapRows {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $RawValue } } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key)
        }

        It "rejects <Key> = <Quote><Label><Quote> read from a file by <Reader>" -ForEach $script:fileReaderRows {
            $environmentFile = Join-Path $TestDrive "guard-$([guid]::NewGuid().ToString('N')).env"
            [System.IO.File]::WriteAllText($environmentFile, "# guard matrix row`n$Line`n", [System.Text.UTF8Encoding]::new($false))

            $environmentValues = if ($Reader -eq "ReadValuesFromEnvFile") {
                ReadValuesFromEnvFile -EnvironmentFile $environmentFile
            }
            else {
                Get-EnvironmentValueMap -EnvironmentFilePath $environmentFile
            }

            { Invoke-IsolatedGuard -EnvironmentValues $environmentValues } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key)
        }

        It "rejects <Key> = <Label> supplied by the ambient environment" -ForEach $script:ambientRows {
            { Invoke-IsolatedGuard -AmbientValues @{ $Key = $AmbientValue } } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key)
        }

        It "rejects an ambient <Key> = 'edfi_dms' + U+0020 that overrides a distinct file value" -ForEach @(
            @{ Key = "MSSQL_DB_NAME" }
            @{ Key = "POSTGRES_DB_NAME" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "edfi_dms_file" } -AmbientValues @{ $Key = "edfi_dms " } } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key)
        }

        It "rejects <Key> whose double-quoted value appends U+3000 to a reference resolved <Source>" -ForEach @(
            @{ Key = "MSSQL_DB_NAME"; Source = "from the file" }
            @{ Key = "POSTGRES_DB_NAME"; Source = "from the file" }
            @{ Key = "MSSQL_DB_NAME"; Source = "from the ambient environment" }
            @{ Key = "POSTGRES_DB_NAME"; Source = "from the ambient environment" }
        ) {
            $environmentValues = @{ $Key = "`"`${GUARD_MATRIX_BASE_NAME}$([char]0x3000)`"" }
            $ambientValues = @{}
            if ($Source -eq "from the file") {
                $environmentValues["GUARD_MATRIX_BASE_NAME"] = "edfi_dms"
            }
            else {
                $ambientValues["GUARD_MATRIX_BASE_NAME"] = "edfi_dms"
            }

            { Invoke-IsolatedGuard -EnvironmentValues $environmentValues -AmbientValues $ambientValues -HelperVariables @("GUARD_MATRIX_BASE_NAME") } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key)
        }

        It "rejects the E2E name with the one trailing LF Assert-SafeDatabaseName admits against <Key> = 'edfi_dms'" -ForEach @(
            @{ Key = "MSSQL_DB_NAME" }
            @{ Key = "POSTGRES_DB_NAME" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "edfi_dms" } -E2EDatabaseName "edfi_dms`n" } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key -E2EDatabaseName "edfi_dms`n")
        }
    }

    Context "direct keys: verdicts the guard already gives" {
        It "rejects <Key> = <Label> unquoted, which the resolver trims" -ForEach @(
            foreach ($key in @("MSSQL_DB_NAME", "POSTGRES_DB_NAME")) {
                @{ Key = $key; Label = "edfi_dms"; RawValue = "edfi_dms" }
                @{ Key = $key; Label = "EDFI_DMS"; RawValue = "EDFI_DMS" }
                @{ Key = $key; Label = "edfi_dms + U+0020"; RawValue = "edfi_dms " }
                @{ Key = $key; Label = "edfi_dms + U+3000"; RawValue = "edfi_dms$([char]0x3000)" }
                @{ Key = $key; Label = "edfi_dms + TAB"; RawValue = "edfi_dms`t" }
                @{ Key = $key; Label = "edfi_dms + FF"; RawValue = "edfi_dms`f" }
                @{ Key = $key; Label = "'EDFI_DMS' (single-quoted case variant)"; RawValue = "'EDFI_DMS'" }
            }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $RawValue } } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key $Key)
        }

        It "fails closed on a configured <Key> that resolves blank or unresolved (<Label>)" -ForEach @(
            foreach ($key in @("MSSQL_DB_NAME", "POSTGRES_DB_NAME")) {
                @{ Key = $key; Label = "empty"; RawValue = "" }
                @{ Key = $key; Label = "quoted empty"; RawValue = "''" }
                @{ Key = $key; Label = "quoted U+0020 only"; RawValue = "' '" }
                @{ Key = $key; Label = "quoted U+3000 only"; RawValue = "`"$([char]0x3000)`"" }
                @{ Key = $key; Label = "undefined reference"; RawValue = "`${GUARD_MATRIX_UNDEFINED}" }
                @{ Key = $key; Label = "single-quoted literal reference"; RawValue = "'`${GUARD_MATRIX_UNDEFINED}'" }
            }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $RawValue } -HelperVariables @("GUARD_MATRIX_UNDEFINED") } |
                Should -Throw -ExpectedMessage "E2E database safety check could not resolve $Key in '$script:environmentFileLabel' to a comparable database name (it is blank, or contains '`$': a literal '`$', or an unresolved or cyclic reference); refusing a destructive reset that cannot be proven dedicated."
        }

        It "accepts a dedicated E2E name when no protected key is configured" {
            { Invoke-IsolatedGuard } | Should -Not -Throw
        }

        It "accepts <Key> = <Label>, a distinct name with a trailing run" -ForEach @(
            foreach ($key in @("MSSQL_DB_NAME", "POSTGRES_DB_NAME")) {
                @{ Key = $key; Label = "'edfi_dms_primary' + U+0020, single-quoted"; RawValue = "'edfi_dms_primary '"; Ambient = $false }
                @{ Key = $key; Label = "'edfi_dms_cms' + U+3000, double-quoted"; RawValue = "`"edfi_dms_cms$([char]0x3000)`""; Ambient = $false }
                @{ Key = $key; Label = "'EDFI_OTHER' + TAB, LF, ambient"; RawValue = "EDFI_OTHER`t`n"; Ambient = $true }
            }
        ) {
            if ($Ambient) {
                { Invoke-IsolatedGuard -AmbientValues @{ $Key = $RawValue } } | Should -Not -Throw
            }
            else {
                { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $RawValue } } | Should -Not -Throw
            }
        }

        It "accepts the E2E name 'edfi_dms_e2e' beside <Key> = 'edfi_dms' + U+0020, double-quoted" -ForEach @(
            @{ Key = "MSSQL_DB_NAME" }
            @{ Key = "POSTGRES_DB_NAME" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "`"edfi_dms `"" } -E2EDatabaseName "edfi_dms_e2e" } | Should -Not -Throw
        }
    }

    # postgresql.compose.yml gives the container edfi_datamanagementservice when POSTGRES_DB_NAME is absent from
    # the file and the shell. The refusals are regression rows: without the default the guard skips the
    # absent key and accepts. The file and shell overrides and the configured blank are preservation rows.
    Context "absent POSTGRES_DB_NAME: the postgresql.compose.yml default (regression: default unprotected)" {
        BeforeAll {
            function Get-PostgresDefaultDiagnostic {
                param([string]$E2EDatabaseName)
                "E2E database '$E2EDatabaseName' in '$script:environmentFileLabel' must be dedicated and cannot match edfi_datamanagementservice, the database postgresql.compose.yml uses when POSTGRES_DB_NAME is absent."
            }
        }

        It "rejects the E2E name '<E2EDatabaseName>' when <Label>" -ForEach @(
            @{ E2EDatabaseName = "edfi_datamanagementservice"; Label = "no protected key is configured"; EnvironmentValues = @{} }
            @{ E2EDatabaseName = "EDFI_DataManagementService"; Label = "no protected key is configured"; EnvironmentValues = @{} }
            @{ E2EDatabaseName = "edfi_datamanagementservice"; Label = "only MSSQL_DB_NAME is configured"; EnvironmentValues = @{ MSSQL_DB_NAME = "edfi_dms_mssql" } }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues $EnvironmentValues -E2EDatabaseName $E2EDatabaseName } |
                Should -Throw -ExpectedMessage (Get-PostgresDefaultDiagnostic -E2EDatabaseName $E2EDatabaseName)
        }

        It "accepts the E2E name 'edfi_datamanagementservice' when the <Source> sets POSTGRES_DB_NAME = 'edfi_dms_primary'" -ForEach @(
            @{ Source = "environment file"; EnvironmentValues = @{ POSTGRES_DB_NAME = "edfi_dms_primary" }; AmbientValues = @{} }
            @{ Source = "shell"; EnvironmentValues = @{}; AmbientValues = @{ POSTGRES_DB_NAME = "edfi_dms_primary" } }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues $EnvironmentValues -AmbientValues $AmbientValues -E2EDatabaseName "edfi_datamanagementservice" } |
                Should -Not -Throw
        }

        It "fails closed on a configured blank POSTGRES_DB_NAME instead of comparing the default" {
            { Invoke-IsolatedGuard -EnvironmentValues @{ POSTGRES_DB_NAME = "''" } -E2EDatabaseName "edfi_datamanagementservice" } |
                Should -Throw -ExpectedMessage "E2E database safety check could not resolve POSTGRES_DB_NAME in '$script:environmentFileLabel' to a comparable database name*"
        }
    }

    # The parser trims the unquoted Database value, so the E2E name is the only side with a trailing
    # character.
    Context "connection-string keys: an E2E name a bare -ieq comparison misses" {
        It "rejects the E2E name with the one trailing LF Assert-SafeDatabaseName admits against <Key> naming 'edfi_dms'" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "Server=s;Database=edfi_dms;User Id=sa" } -E2EDatabaseName "edfi_dms`n" } |
                Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key -E2EDatabaseName "edfi_dms`n")
        }
    }

    # Compose keeps a ' #' inside a quoted value, so the resolved string still carries every keyword
    # after it. The extractor parses that string as given; converting it again as a dotenv value would
    # read ' #' as a comment and drop the later Initial Catalog or DB the provider uses.
    # The two "keeps" tests are regression rows for the whole-string conversion: restored, it ends the
    # string at ' #', only 'x' or 'safe' remains, and the guard accepts. With only the per-value
    # conversion restored, the 'Database=x #y' rows still reject but fail their candidate assertion
    # ('x' for 'x #y'), and the password rows pass. The "accepts" tests and the unquoted rejection are
    # positive controls and a preservation row; the password control also fails its candidate
    # assertion under the whole-string conversion, while its verdict holds.
    Context "connection-string keys: a ' #' that Compose keeps inside a quoted value" {
        It "keeps the later <Alias> = 'edfi_dms' after 'Database=x #y' in <Key>, <Quote>-quoted" -ForEach @(
            foreach ($key in @("DATABASE_CONNECTION_STRING_ADMIN", "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING")) {
                foreach ($quote in @("'", '"')) {
                    @{ Key = $key; Quote = $quote; Alias = "Initial Catalog"; ConnectionString = "Server=s;Database=x #y;Initial Catalog=edfi_dms;User Id=sa" }
                    @{ Key = $key; Quote = $quote; Alias = "DB"; ConnectionString = "Host=h;Database=x #y;DB=edfi_dms;Username=u" }
                }
            }
        ) {
            $rawValue = "$Quote$ConnectionString$Quote"

            (@(Get-IsolatedConnectionStringDatabaseName -Key $Key -RawValue $rawValue) -join "|") | Should -BeExactly "x #y|edfi_dms"
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $rawValue } } |
                Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
        }

        It "keeps the Initial Catalog = 'edfi_dms' after a password containing ' #' in <Key>, <Quote>-quoted" -ForEach @(
            foreach ($key in @("DATABASE_CONNECTION_STRING_ADMIN", "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING")) {
                foreach ($quote in @("'", '"')) {
                    @{ Key = $key; Quote = $quote }
                }
            }
        ) {
            $rawValue = "$($Quote)Server=s;Database=safe;Password=a #b;Initial Catalog=edfi_dms;User Id=sa$Quote"

            (@(Get-IsolatedConnectionStringDatabaseName -Key $Key -RawValue $rawValue) -join "|") | Should -BeExactly "safe|edfi_dms"
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $rawValue } } |
                Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
        }

        It "accepts <Key> naming only distinct databases around a password containing ' #'" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            $rawValue = "'Server=s;Database=safe;Password=a #b;Initial Catalog=edfi_dms_other;User Id=sa'"

            (@(Get-IsolatedConnectionStringDatabaseName -Key $Key -RawValue $rawValue) -join "|") | Should -BeExactly "safe|edfi_dms_other"
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $rawValue } } | Should -Not -Throw
        }

        # Unquoted, Compose itself strips ' #' and the rest of the line, so the provider never receives
        # the later keywords and the guard compares only what remains.
        It "accepts unquoted <Key> whose ' #' comment Compose strips with the Initial Catalog after it" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            $rawValue = "Server=s;Database=x #y;Initial Catalog=edfi_dms;User Id=sa"

            (@(Get-IsolatedConnectionStringDatabaseName -Key $Key -RawValue $rawValue) -join "|") | Should -BeExactly "x"
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $rawValue } } | Should -Not -Throw
        }

        It "rejects unquoted <Key> naming 'edfi_dms' before a trailing comment Compose strips" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            $rawValue = "Server=s;Database=edfi_dms;User Id=sa #note"

            (@(Get-IsolatedConnectionStringDatabaseName -Key $Key -RawValue $rawValue) -join "|") | Should -BeExactly "edfi_dms"
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $rawValue } } |
                Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
        }
    }

    # The parsed value stands for what the provider receives and the candidate is what the guard
    # compares; asserting both keeps a row from passing on the wrong stage.
    Context "connection-string keys: parsed value, extractor candidate and verdict" {
        It "reads <Label> in <Key> (<Kind>)" -ForEach $script:connectionStringStageRows {
            $environmentValues = @{ $Key = $Raw }
            if ($null -ne $StageName) {
                $environmentValues["GUARD_STAGE_NAME"] = $StageName
            }
            # GUARD_STAGE_NAME and dms are names a '$' in these rows could expand. They are cleared
            # from the process environment for the extraction and the guard run, then restored, so
            # an ambient value cannot change a row's outcome.
            $helperVariables = @("GUARD_STAGE_NAME", "dms")

            $stage = Get-IsolatedConnectionStringStage -Key $Key -EnvironmentValues $environmentValues -HelperVariables $helperVariables

            $stage.Parsed.Count | Should -Be $Parsed.Count
            ($stage.Parsed -join "|") | Should -BeExactly ($Parsed -join "|")
            $stage.Candidates.Count | Should -Be $Parsed.Count
            ($stage.Candidates -join "|") | Should -BeExactly ($Parsed -join "|") -Because "the extractor returns the parsed value without a second dotenv conversion"

            $guard = { Invoke-IsolatedGuard -EnvironmentValues $environmentValues -HelperVariables $helperVariables }
            switch ($Verdict) {
                "Collision" { $guard | Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key) }
                "Domain" { $guard | Should -Throw -ExpectedMessage (Get-DomainDiagnostic -Key $Key) }
                "Dollar" { $guard | Should -Throw -ExpectedMessage "E2E database safety check could not fully resolve the database name from $Key in '$script:environmentFileLabel' (it contains '`$': a literal '`$', or an unresolved or cyclic reference)*" }
                "Accepted" { $guard | Should -Not -Throw }
                default { throw "Unknown verdict '$Verdict'." }
            }
        }
    }

    Context "connection-string keys: verdicts the guard already gives" {
        It "rejects <Key> = <Label>" -ForEach @(
            foreach ($key in @("DATABASE_CONNECTION_STRING_ADMIN", "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING")) {
                @{ Key = $key; Label = "double-quoted whole value, Database single-quoted with trailing U+0020"; Value = "`"Server=s;Database='edfi_dms ';User Id=sa`""; Ambient = $false }
                @{ Key = $key; Label = "Database quoted with trailing U+0020"; Value = "Server=s;Database=`"edfi_dms `";User Id=sa"; Ambient = $false }
                @{ Key = $key; Label = "Initial Catalog quoted, case variant with trailing U+3000"; Value = "'Server=s;Initial Catalog=`"EDFI_DMS$([char]0x3000)`";User Id=sa'"; Ambient = $false }
                @{ Key = $key; Label = "Database unquoted with trailing TAB"; Value = "Host=h;Database=edfi_dms`t;Username=u"; Ambient = $false }
                @{ Key = $key; Label = "DB single-quoted with trailing FF"; Value = "Host=h;DB='edfi_dms`f';Username=u"; Ambient = $false }
                @{ Key = $key; Label = "a decoy Database before a quoted Initial Catalog with trailing U+0020"; Value = "Server=s;Database=other;Initial Catalog=`"edfi_dms `";User Id=sa"; Ambient = $false }
                @{ Key = $key; Label = "Database quoted with trailing U+3000, ambient"; Value = "Host=h;Database=`"edfi_dms$([char]0x3000)`";Username=u"; Ambient = $true }
            }
        ) {
            if ($Ambient) {
                { Invoke-IsolatedGuard -AmbientValues @{ $Key = $Value } } |
                    Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
            }
            else {
                { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $Value } } |
                    Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
            }
        }

        It "fails closed on a configured blank <Key>" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "" } } |
                Should -Throw -ExpectedMessage "E2E database safety check could not resolve $Key in '$script:environmentFileLabel'; refusing*"
        }

        It "accepts <Key> naming a distinct database with a quoted trailing run" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "Server=s;Database=`"edfi_dms_admin $([char]0x3000)`";User Id=sa" } } |
                Should -Not -Throw
        }
    }

    Context "comparison domain: candidates the guard accepted before the domain check" {
        It "refuses <Key> = <Label> with the domain diagnostic" -ForEach $script:domainRefusalRows {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $Value } } |
                Should -Throw -ExpectedMessage (Get-DomainDiagnostic -Key $Key)
        }

        It "withholds the refused <Key> value from the domain diagnostic" -ForEach @(
            @{ Key = "POSTGRES_DB_NAME"; Value = "edfi-zqmarker" }
            @{ Key = "MSSQL_DB_NAME"; Value = "edfi-zqmarker" }
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN"; Value = "Server=s;Database=edfi-zqmarker;User Id=sa" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Value = "Server=s;Database=edfi-zqmarker;User Id=sa" }
        ) {
            $message = Get-GuardRejectionMessage -EnvironmentValues @{ $Key = $Value }

            $message | Should -Be (Get-DomainDiagnostic -Key $Key)
            $message | Should -Not -Match "zqmarker"
        }
    }

    Context "comparison domain: verdicts the guard already gives" {
        It "rejects <Key> = <Label> with the collision or the domain diagnostic" -ForEach $script:alreadyRejectedRows {
            Get-GuardRejectionMessage -EnvironmentValues @{ $Key = $Value } |
                Should -BeIn @((Get-CollisionDiagnostic -Key $Key), (Get-DomainDiagnostic -Key $Key))
        }

        # The domain check runs only after every key's collision check, so these configurations keep
        # the collision diagnostic although a candidate checked earlier is outside the domain.
        It "reports the MSSQL_DB_NAME collision although POSTGRES_DB_NAME, checked first, is outside the domain" {
            { Invoke-IsolatedGuard -EnvironmentValues @{ POSTGRES_DB_NAME = "edfi-other"; MSSQL_DB_NAME = "EDFI_DMS" } } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key "MSSQL_DB_NAME")
        }

        It "reports the <Key> collision although POSTGRES_DB_NAME is outside the domain" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ POSTGRES_DB_NAME = "edfi-other"; $Key = "Server=s;Database=edfi_dms;User Id=sa" } } |
                Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
        }

        It "reports the <Key> Initial Catalog collision although its Database is outside the domain" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = "Server=s;Database=edfi-other;Initial Catalog=edfi_dms;User Id=sa" } } |
                Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key)
        }

        It "accepts <Key> naming the in-domain distinct database '<Name>'" -ForEach @(
            foreach ($name in @("edfi_dms_other", "EDFI_ODS_2")) {
                @{ Key = "POSTGRES_DB_NAME"; Name = $name; Value = $name }
                @{ Key = "MSSQL_DB_NAME"; Name = $name; Value = "`"$name`"" }
                @{ Key = "DATABASE_CONNECTION_STRING_ADMIN"; Name = $name; Value = "Server=s;Database=$name;User Id=sa" }
                @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Name = $name; Value = "Host=h;DB='$name';Username=u" }
            }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $Value } } | Should -Not -Throw
        }
    }

    # Assert-SafeDatabaseName's case-insensitive match admits the Kelvin sign (U+212A), so these E2E
    # names reach the comparison. With no protected name that -ieq folds them to, the guard accepted
    # them before the E2E domain check.
    Context "comparison domain: E2E names the guard accepted before the E2E domain check" {
        It "refuses the E2E name 'edfi_dms_' + U+212A <Label>" -ForEach @(
            @{ Label = "with no protected key configured"; EnvironmentValues = @{} }
            @{ Label = "beside a distinct MSSQL_DB_NAME"; EnvironmentValues = @{ MSSQL_DB_NAME = "edfi_dms_primary" } }
            @{ Label = "beside a distinct POSTGRES_DB_NAME"; EnvironmentValues = @{ POSTGRES_DB_NAME = "edfi_dms_primary" } }
            @{ Label = "beside a distinct DATABASE_CONNECTION_STRING_ADMIN"; EnvironmentValues = @{ DATABASE_CONNECTION_STRING_ADMIN = "Server=s;Database=edfi_dms_primary;User Id=sa" } }
        ) {
            $e2eDatabaseName = "edfi_dms_$([char]0x212A)"
            { Assert-SafeDatabaseName -DatabaseName $e2eDatabaseName } | Should -Not -Throw -Because "the name must reach the guard's comparison"

            { Invoke-IsolatedGuard -EnvironmentValues $EnvironmentValues -E2EDatabaseName $e2eDatabaseName } |
                Should -Throw -ExpectedMessage (Get-E2EDomainDiagnostic -E2EDatabaseName $e2eDatabaseName)
        }
    }

    Context "comparison domain: E2E verdicts the guard already gives" {
        It "accepts the E2E name 'edfi_dms_e2e' with the one trailing LF Assert-SafeDatabaseName admits beside <Key> = 'edfi_dms'" -ForEach @(
            @{ Key = "MSSQL_DB_NAME"; Value = "edfi_dms" }
            @{ Key = "POSTGRES_DB_NAME"; Value = "edfi_dms" }
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN"; Value = "Server=s;Database=edfi_dms;User Id=sa" }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Value = "Host=h;Database=edfi_dms;Username=u" }
        ) {
            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $Value } -E2EDatabaseName "edfi_dms_e2e`n" } | Should -Not -Throw
        }
    }

    # PostgreSQL keeps only the first 63 bytes of a database name, so two names that share them are one
    # database although -ieq calls them distinct. Without the length bound the guard accepts every row
    # here.
    Context "length: names PostgreSQL truncates to one database (regression: length bound removed)" {
        BeforeAll {
            $script:prefix63 = "edfi_guard_" + ("a" * 52)
        }

        It "refuses the <Length>-character E2E name beside <Key> naming its 63-character prefix" -ForEach @(
            @{ Length = 67; Suffix = "_e2e"; Key = "POSTGRES_DB_NAME"; Value = "edfi_guard_$("a" * 52)" }
            @{ Length = 64; Suffix = "e"; Key = "DATABASE_CONNECTION_STRING_ADMIN"; Value = "Server=s;Database=edfi_guard_$("a" * 52);User Id=sa" }
        ) {
            $e2eDatabaseName = "$script:prefix63$Suffix"
            $e2eDatabaseName.Length | Should -Be $Length
            { Assert-SafeDatabaseName -DatabaseName $e2eDatabaseName } | Should -Not -Throw -Because "the name must reach the guard's comparison"

            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $Value } -E2EDatabaseName $e2eDatabaseName } |
                Should -Throw -ExpectedMessage (Get-E2ELengthDiagnostic -E2EDatabaseName $e2eDatabaseName)
        }

        It "refuses <Key> naming a <Length>-character database that starts with the 63-character E2E name, withholding it" -ForEach @(
            @{ Length = 64; Key = "MSSQL_DB_NAME"; Value = "`"edfi_guard_$("a" * 52)x`"" }
            @{ Length = 71; Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Value = "Host=h;DB='edfi_guard_$("a" * 52)_primary';Username=u" }
        ) {
            $message = Get-GuardRejectionMessage -EnvironmentValues @{ $Key = $Value } -E2EDatabaseName $script:prefix63

            $message | Should -BeExactly (Get-LengthDiagnostic -Key $Key)
        }

        It "refuses a 64-character E2E name with no protected key configured" {
            $e2eDatabaseName = "$($script:prefix63)e"

            { Invoke-IsolatedGuard -E2EDatabaseName $e2eDatabaseName } |
                Should -Throw -ExpectedMessage (Get-E2ELengthDiagnostic -E2EDatabaseName $e2eDatabaseName)
        }
    }

    # The bound applies after the trailing characters are removed, so a 63-character name with a
    # removable suffix is accepted on either side.
    Context "length: names of at most 63 characters (positive control)" {
        It "accepts a <Length>-character E2E name beside <Key> naming a distinct <Length>-character database<Note>" -ForEach @(
            @{ Length = 63; Key = "POSTGRES_DB_NAME"; Note = ""; E2ESuffix = ""; Value = "edfi_guard_$("b" * 52)" }
            @{ Length = 63; Key = "MSSQL_DB_NAME"; Note = " followed by U+3000, double-quoted"; E2ESuffix = ""; Value = "`"edfi_guard_$("b" * 52)$([char]0x3000)`"" }
            @{ Length = 63; Key = "DATABASE_CONNECTION_STRING_ADMIN"; Note = ", the E2E name followed by LF"; E2ESuffix = "`n"; Value = "Server=s;Database=edfi_guard_$("b" * 52);User Id=sa" }
            @{ Length = 63; Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Note = ""; E2ESuffix = ""; Value = "Host=h;DB='edfi_guard_$("b" * 52)';Username=u" }
            @{ Length = 62; Key = "POSTGRES_DB_NAME"; Note = ""; E2ESuffix = ""; Value = "edfi_guard_$("b" * 51)" }
            @{ Length = 62; Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Note = ""; E2ESuffix = ""; Value = "Server=s;Database=edfi_guard_$("b" * 51);User Id=sa" }
        ) {
            $e2eDatabaseName = "edfi_guard_$("a" * ($Length - 11))$E2ESuffix"

            { Invoke-IsolatedGuard -EnvironmentValues @{ $Key = $Value } -E2EDatabaseName $e2eDatabaseName } | Should -Not -Throw
        }
    }

    # Get-EnvironmentValueMap is provision-e2e-database.ps1's reader. Compose accepts an 'export '
    # prefix; a reader that kept it in the key left the protected key unset, and the guard accepted.
    Context "provisioning reader: exported assignments (regression: export prefix kept)" {
        It "rejects <Key> exported with <Separator> and read by Get-EnvironmentValueMap" -ForEach @(
            @{ Key = "MSSQL_DB_NAME"; Separator = "a space"; RawValue = "'edfi_dms '"; Prefix = "export " }
            @{ Key = "POSTGRES_DB_NAME"; Separator = "a TAB"; RawValue = "EDFI_DMS"; Prefix = "export`t" }
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN"; Separator = "two spaces"; RawValue = "'Host=h;Database=edfi_dms;Username=u'"; Prefix = "export  " }
            @{ Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; Separator = "a space"; RawValue = "`"Server=s;Database=edfi_dms;User Id=sa`""; Prefix = "export " }
        ) {
            $environmentFile = Join-Path $TestDrive "export-$([guid]::NewGuid().ToString('N')).env"
            [System.IO.File]::WriteAllText($environmentFile, "$Prefix$Key=$RawValue`n", [System.Text.UTF8Encoding]::new($false))

            $environmentValues = Get-EnvironmentValueMap -EnvironmentFilePath $environmentFile

            @($environmentValues.Keys) | Should -Be @($Key)
            $environmentValues[$Key] | Should -BeExactly $RawValue -Because "the reader stores the raw value for the Compose resolver"
            { Invoke-IsolatedGuard -EnvironmentValues $environmentValues } |
                Should -Throw -ExpectedMessage (Get-CollisionDiagnostic -Key $Key)
        }

        It "lets the last MSSQL_DB_NAME declaration win when <Label>" -ForEach @(
            @{ Label = "an exported 'EDFI_DMS' follows a distinct plain value (regression)"; Lines = @("MSSQL_DB_NAME=edfi_dms_primary", "export MSSQL_DB_NAME=EDFI_DMS"); Rejected = $true }
            @{ Label = "a distinct plain value follows an exported 'EDFI_DMS' (positive control)"; Lines = @("export MSSQL_DB_NAME=EDFI_DMS", "MSSQL_DB_NAME=edfi_dms_primary"); Rejected = $false }
        ) {
            $environmentFile = Join-Path $TestDrive "export-$([guid]::NewGuid().ToString('N')).env"
            [System.IO.File]::WriteAllLines($environmentFile, [string[]]$Lines, [System.Text.UTF8Encoding]::new($false))

            $environmentValues = Get-EnvironmentValueMap -EnvironmentFilePath $environmentFile

            if ($Rejected) {
                { Invoke-IsolatedGuard -EnvironmentValues $environmentValues } |
                    Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key "MSSQL_DB_NAME")
            }
            else {
                { Invoke-IsolatedGuard -EnvironmentValues $environmentValues } | Should -Not -Throw
            }
        }
    }

    # Get-EnvironmentValueMap keeps the last value of each name, compared case-insensitively, while
    # Compose resolves the file line by line with case-sensitive names. Each refused file below is one
    # the guard accepts from that map, so the refusals are regression rows for
    # Assert-UnambiguousProtectedDeclaration: the repeated declarations for its duplicate check, the
    # forward, case-variant and lowercase-only rows for its value-agreement check. The accepted files
    # are positive controls.
    Context "provisioning reader: repeated or case-variant declarations (regression: ambiguity check removed)" {
        BeforeAll {
            $script:declarationHelperNames = @("PRIMARY_NAME", "LOG_LEVEL", "DB_ALIAS", "db_alias", "DB_PASSWORD")

            function Write-DeclarationFile {
                param([string[]]$Lines)
                $environmentFile = Join-Path $TestDrive "declarations-$([guid]::NewGuid().ToString('N')).env"
                [System.IO.File]::WriteAllLines($environmentFile, $Lines, [System.Text.UTF8Encoding]::new($false))
                return $environmentFile
            }

            function Invoke-IsolatedDeclarationCheck {
                param([string]$EnvironmentFile, [hashtable]$AmbientValues = @{})

                $isolatedNames = @($script:protectedKeys + $script:declarationHelperNames + @($AmbientValues.Keys)) | Select-Object -Unique
                $savedVariables = foreach ($name in $isolatedNames) {
                    Save-AmbientVariable -Name $name
                    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                }

                try {
                    foreach ($name in $AmbientValues.Keys) {
                        [System.Environment]::SetEnvironmentVariable($name, $AmbientValues[$name])
                    }

                    Assert-UnambiguousProtectedDeclaration `
                        -EnvironmentFilePath $EnvironmentFile `
                        -EnvironmentValues (Get-EnvironmentValueMap -EnvironmentFilePath $EnvironmentFile)
                }
                finally {
                    foreach ($savedVariable in $savedVariables) {
                        Restore-AmbientVariable -Saved $savedVariable
                    }
                }
            }
        }

        It "refuses a file where <Label>" -ForEach @(
            @{
                Label = "a reference freezes 'EDFI_DMS' before an exported MSSQL_DB_NAME redeclares it"
                Lines = @('MSSQL_DB_NAME=EDFI_DMS', 'DATABASE_CONNECTION_STRING_ADMIN=Server=s;Database=${MSSQL_DB_NAME};User Id=sa', 'export MSSQL_DB_NAME=edfi_dms_primary')
                Name = "MSSQL_DB_NAME"
                Locations = "MSSQL_DB_NAME on line 1, MSSQL_DB_NAME on line 3"
            }
            @{
                Label = "a lowercase postgres_db_name follows POSTGRES_DB_NAME 'edfi_dms'"
                Lines = @('POSTGRES_DB_NAME=edfi_dms', 'postgres_db_name=edfi_dms_primary')
                Name = "POSTGRES_DB_NAME"
                Locations = "POSTGRES_DB_NAME on line 1, postgres_db_name on line 2"
            }
            @{
                Label = "a name a connection string references is declared twice"
                Lines = @('PRIMARY_NAME=edfi_dms', 'CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING=host=h;database=${PRIMARY_NAME};username=u', 'PRIMARY_NAME=edfi_dms_primary')
                Name = "PRIMARY_NAME"
                Locations = "PRIMARY_NAME on line 1, PRIMARY_NAME on line 3"
            }
        ) {
            $environmentFile = Write-DeclarationFile -Lines $Lines

            { Invoke-IsolatedGuard -EnvironmentValues (Get-EnvironmentValueMap -EnvironmentFilePath $environmentFile) -HelperVariables $script:declarationHelperNames } |
                Should -Not -Throw -Because "the guard reads the last value from the collapsed map"
            { Invoke-IsolatedDeclarationCheck -EnvironmentFile $environmentFile } |
                Should -Throw -ExpectedMessage "E2E database safety check: '$environmentFile' declares $Name more than once or in more than one letter case ($Locations), and a protected database setting depends on it. Docker Compose resolves the file line by line with case-sensitive names, so the value it uses can differ from the one this check compares; refusing a destructive reset. Keep a single declaration."
        }

        It "accepts a file where <Label>" -ForEach @(
            @{
                Label = "each protected name is declared once, exported and quoted"
                Lines = @("export POSTGRES_DB_NAME='edfi_dms_primary '", 'DATABASE_CONNECTION_STRING_ADMIN="host=h;database=${POSTGRES_DB_NAME};username=u"', 'MSSQL_DB_NAME="edfi_dms_mssql"')
                AmbientValues = @{}
            }
            @{
                Label = "only a name no protected setting depends on is declared twice"
                Lines = @('LOG_LEVEL=Debug', 'POSTGRES_DB_NAME=edfi_dms_primary', 'LOG_LEVEL=Information')
                AmbientValues = @{}
            }
            @{
                Label = "the ambient environment supplies the repeated MSSQL_DB_NAME"
                Lines = @('MSSQL_DB_NAME=EDFI_DMS', 'DATABASE_CONNECTION_STRING_ADMIN=Server=s;Database=${MSSQL_DB_NAME};User Id=sa', 'MSSQL_DB_NAME=edfi_dms_primary')
                AmbientValues = @{ MSSQL_DB_NAME = "edfi_dms_ambient" }
            }
            @{
                Label = "MSSQL_DB_NAME references DB_ALIAS after its declaration"
                Lines = @('DB_ALIAS=edfi_other', 'MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}')
                AmbientValues = @{}
            }
            @{
                Label = "the ambient environment supplies DB_ALIAS, which MSSQL_DB_NAME references before its declaration"
                Lines = @('MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}', 'DB_ALIAS=edfi_other')
                AmbientValues = @{ DB_ALIAS = "edfi_dms_ambient" }
            }
            @{
                Label = "the ambient environment supplies MSSQL_DB_NAME, whose declaration references DB_ALIAS before its declaration"
                Lines = @('MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}', 'DB_ALIAS=edfi_other')
                AmbientValues = @{ MSSQL_DB_NAME = "edfi_dms_ambient" }
            }
            @{
                Label = "a connection string references a later name only outside its database name"
                Lines = @('CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING=host=h;database=edfi_dms_primary;username=u;password=${DB_PASSWORD}', 'DB_PASSWORD=guard-matrix-pass')
                AmbientValues = @{}
            }
        ) {
            $environmentFile = Write-DeclarationFile -Lines $Lines

            { Invoke-IsolatedDeclarationCheck -EnvironmentFile $environmentFile -AmbientValues $AmbientValues } | Should -Not -Throw
        }

        It "refuses a file where <Label>, which the guard accepts against the E2E name '<E2EDatabaseName>'" -ForEach @(
            @{
                Label = "MSSQL_DB_NAME references DB_ALIAS before its declaration"
                Lines = @('MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}', 'DB_ALIAS=edfi_other')
                Key = "MSSQL_DB_NAME"; E2EDatabaseName = "edfi_dms"
            }
            @{
                Label = "DATABASE_CONNECTION_STRING_ADMIN references DB_ALIAS before its declaration"
                Lines = @('DATABASE_CONNECTION_STRING_ADMIN=Server=s;Database=${DB_ALIAS:-edfi_dms};User Id=sa', 'DB_ALIAS=edfi_other')
                Key = "DATABASE_CONNECTION_STRING_ADMIN"; E2EDatabaseName = "edfi_dms"
            }
            @{
                Label = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING references DB_ALIAS before its declaration"
                Lines = @('CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING=host=h;database=${DB_ALIAS:-edfi_dms};username=u', 'DB_ALIAS=edfi_other')
                Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; E2EDatabaseName = "edfi_dms"
            }
            @{
                Label = "MSSQL_DB_NAME references db_alias, declared only as DB_ALIAS"
                Lines = @('DB_ALIAS=edfi_other', 'MSSQL_DB_NAME=${db_alias:-edfi_dms}')
                Key = "MSSQL_DB_NAME"; E2EDatabaseName = "edfi_dms"
            }
            @{
                Label = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING references db_alias, declared only as DB_ALIAS"
                Lines = @('DB_ALIAS=edfi_other', 'CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING=host=h;database=${db_alias:-edfi_dms};username=u')
                Key = "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"; E2EDatabaseName = "edfi_dms"
            }
            @{
                Label = "POSTGRES_DB_NAME is declared only as postgres_db_name"
                Lines = @('postgres_db_name=edfi_other')
                Key = "POSTGRES_DB_NAME"; E2EDatabaseName = "edfi_datamanagementservice"
            }
            @{
                Label = "DATABASE_CONNECTION_STRING_ADMIN is declared only in lowercase"
                Lines = @('database_connection_string_admin=Server=s;Database=edfi_other;User Id=sa')
                Key = "DATABASE_CONNECTION_STRING_ADMIN"; E2EDatabaseName = "edfi_dms"
            }
        ) {
            $environmentFile = Write-DeclarationFile -Lines $Lines

            { Invoke-IsolatedGuard -EnvironmentValues (Get-EnvironmentValueMap -EnvironmentFilePath $environmentFile) -HelperVariables $script:declarationHelperNames -E2EDatabaseName $E2EDatabaseName } |
                Should -Not -Throw -Because "the guard compares the map's value, not the one Compose uses"
            { Invoke-IsolatedDeclarationCheck -EnvironmentFile $environmentFile } |
                Should -Throw -ExpectedMessage "E2E database safety check: '$environmentFile' gives $Key a different value or presence under Docker Compose's line-by-line, case-sensitive resolution than the one this check compares. A reference sees only earlier declarations of the same letter case, and a declaration in another letter case is a different name; refusing a destructive reset. Declare each name once, in one letter case, before any line that references it. The values are withheld."
        }

        It "leaves the guard comparing the value Compose uses once the file is accepted (<Label>)" -ForEach @(
            @{
                Label = "a preceding DB_ALIAS"
                Lines = @('DB_ALIAS=edfi_other', 'MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}')
                AmbientValues = @{}; E2EDatabaseName = "edfi_other"
            }
            @{
                Label = "an ambient MSSQL_DB_NAME over an inert forward reference"
                Lines = @('MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}', 'DB_ALIAS=edfi_other')
                AmbientValues = @{ MSSQL_DB_NAME = "edfi_dms_ambient" }; E2EDatabaseName = "edfi_dms_ambient"
            }
        ) {
            $environmentFile = Write-DeclarationFile -Lines $Lines

            { Invoke-IsolatedDeclarationCheck -EnvironmentFile $environmentFile -AmbientValues $AmbientValues } | Should -Not -Throw
            { Invoke-IsolatedGuard -EnvironmentValues (Get-EnvironmentValueMap -EnvironmentFilePath $environmentFile) -AmbientValues $AmbientValues -HelperVariables $script:declarationHelperNames -E2EDatabaseName $E2EDatabaseName } |
                Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key "MSSQL_DB_NAME" -E2EDatabaseName $E2EDatabaseName)
        }
    }

    # -ieq folds U+212A to k only when the process has culture data. The globalization mode is fixed
    # when the runtime starts, so each row runs the guard in a child pwsh launched with
    # DOTNET_SYSTEM_GLOBALIZATION_INVARIANT set (or removed) beforehand. The child records whether -ieq
    # folded the pair, so a row cannot pass under the wrong mode. The invariant U+212A row is a
    # regression row for the E2E domain check, and the 'edfi_k' + LF row one for a comparer reduced to
    # -ieq; the folding row is preservation and the ASCII row a positive control.
    Context "comparison domain: E2E names under each globalization mode" {
        BeforeAll {
            $script:globalizationChildScript = Join-Path $TestDrive "invoke-guard-globalization.ps1"
            @'
param([string]$InputPath, [string]$RecordPath)

$inputRecord = Get-Content -LiteralPath $InputPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
foreach ($name in @(
        "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "DATABASE_CONNECTION_STRING_ADMIN",
        "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING"
    )) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}
Import-Module -Name $inputRecord.ModulePath -Force

$outcome = "completed"
$message = $null
try {
    Assert-E2EDatabaseIsDedicated `
        -EnvironmentValues $inputRecord.EnvironmentValues `
        -EnvironmentFilePath $inputRecord.EnvironmentFilePath `
        -E2EDatabaseName $inputRecord.E2EDatabaseName
}
catch {
    $outcome = "threw"
    $message = $_.Exception.Message
}

[ordered]@{
    Outcome = $outcome
    Message = $message
    IeqFolds = ("edfi_$([char]0x212A)" -ieq "edfi_k")
} | ConvertTo-Json | Set-Content -LiteralPath $RecordPath -Encoding utf8
'@ | Set-Content -LiteralPath $script:globalizationChildScript -Encoding utf8

            $script:globalizationPwshPath = (Get-Process -Id $PID).Path

            function Invoke-GuardInGlobalizationMode {
                param([bool]$Invariant, [string]$E2EDatabaseName, [hashtable]$EnvironmentValues)

                $runDirectory = Join-Path $TestDrive "globalization-$([guid]::NewGuid().ToString('N'))"
                New-Item -ItemType Directory -Path $runDirectory | Out-Null
                $inputPath = Join-Path $runDirectory "input.json"
                $recordPath = Join-Path $runDirectory "record.json"
                @{
                    ModulePath = Join-Path $script:dockerComposeRoot "database-safety.psm1"
                    EnvironmentFilePath = $script:environmentFileLabel
                    E2EDatabaseName = $E2EDatabaseName
                    EnvironmentValues = $EnvironmentValues
                } | ConvertTo-Json | Set-Content -LiteralPath $inputPath -Encoding utf8

                $savedVariable = Save-AmbientVariable -Name "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"
                try {
                    if ($Invariant) {
                        [System.Environment]::SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1")
                    }
                    else {
                        Remove-Item -LiteralPath "Env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" -ErrorAction SilentlyContinue
                    }

                    $childOutput = & $script:globalizationPwshPath -NoProfile -NonInteractive -File $script:globalizationChildScript `
                        -InputPath $inputPath `
                        -RecordPath $recordPath 2>&1
                }
                finally {
                    Restore-AmbientVariable -Saved $savedVariable
                }

                if (-not (Test-Path -LiteralPath $recordPath)) {
                    throw "The globalization child wrote no record. Output:`n$($childOutput -join "`n")"
                }

                return Get-Content -LiteralPath $recordPath -Raw -Encoding utf8 | ConvertFrom-Json
            }

            $script:kelvinE2EDatabaseName = "edfi_$([char]0x212A)"
        }

        It "refuses the E2E name 'edfi_' + U+212A beside MSSQL_DB_NAME = 'edfi_k' when -ieq does not fold it" {
            $record = Invoke-GuardInGlobalizationMode -Invariant $true -E2EDatabaseName $script:kelvinE2EDatabaseName -EnvironmentValues @{ MSSQL_DB_NAME = "edfi_k" }

            $record.IeqFolds | Should -BeFalse -Because "invariant globalization mode leaves U+212A unfolded"
            $record.Outcome | Should -Be "threw"
            $record.Message | Should -BeExactly (Get-E2EDomainDiagnostic -E2EDatabaseName $script:kelvinE2EDatabaseName)
        }

        It "keeps the MSSQL_DB_NAME collision diagnostic for the same pair when -ieq folds it" {
            $record = Invoke-GuardInGlobalizationMode -Invariant $false -E2EDatabaseName $script:kelvinE2EDatabaseName -EnvironmentValues @{ MSSQL_DB_NAME = "edfi_k" }

            $record.IeqFolds | Should -BeTrue -Because "culture data folds U+212A to k"
            $record.Outcome | Should -Be "threw"
            $record.Message | Should -BeExactly (Get-DedicatedDiagnostic -Key "MSSQL_DB_NAME" -E2EDatabaseName $script:kelvinE2EDatabaseName)
        }

        It "keeps the MSSQL_DB_NAME collision diagnostic for the E2E name 'edfi_k' + LF in invariant mode" {
            $record = Invoke-GuardInGlobalizationMode -Invariant $true -E2EDatabaseName "edfi_k`n" -EnvironmentValues @{ MSSQL_DB_NAME = "edfi_k" }

            $record.IeqFolds | Should -BeFalse
            $record.Outcome | Should -Be "threw"
            $record.Message | Should -BeExactly (Get-DedicatedDiagnostic -Key "MSSQL_DB_NAME" -E2EDatabaseName "edfi_k`n")
        }

        It "accepts the ASCII E2E name 'edfi_dms_e2e' beside MSSQL_DB_NAME = 'edfi_k' in invariant mode" {
            $record = Invoke-GuardInGlobalizationMode -Invariant $true -E2EDatabaseName "edfi_dms_e2e" -EnvironmentValues @{ MSSQL_DB_NAME = "edfi_k" }

            $record.IeqFolds | Should -BeFalse
            $record.Outcome | Should -Be "completed"
        }
    }

    Context "ambient isolation (harness check)" {
        It "restores a protected key's prior ambient value after a row sets it" {
            $savedVariable = Save-AmbientVariable -Name "MSSQL_DB_NAME"
            try {
                [System.Environment]::SetEnvironmentVariable("MSSQL_DB_NAME", "guard_matrix_sentinel")

                { Invoke-IsolatedGuard -AmbientValues @{ MSSQL_DB_NAME = "EDFI_DMS" } } |
                    Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key "MSSQL_DB_NAME")
                [System.Environment]::GetEnvironmentVariable("MSSQL_DB_NAME") | Should -Be "guard_matrix_sentinel"
            }
            finally {
                Restore-AmbientVariable -Saved $savedVariable
            }
        }

        It "leaves a protected key absent after a row sets it when it was absent before" {
            $savedVariable = Save-AmbientVariable -Name "POSTGRES_DB_NAME"
            try {
                Remove-Item -LiteralPath "Env:POSTGRES_DB_NAME" -ErrorAction SilentlyContinue

                { Invoke-IsolatedGuard -AmbientValues @{ POSTGRES_DB_NAME = "EDFI_DMS" } } |
                    Should -Throw -ExpectedMessage (Get-DedicatedDiagnostic -Key "POSTGRES_DB_NAME")
                Test-Path -LiteralPath "Env:POSTGRES_DB_NAME" | Should -BeFalse
            }
            finally {
                Restore-AmbientVariable -Saved $savedVariable
            }
        }

        It "hides a protected value already set in the session from a row that does not set it" {
            $savedVariable = Save-AmbientVariable -Name "POSTGRES_DB_NAME"
            try {
                [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "edfi_dms")

                { Invoke-IsolatedGuard } | Should -Not -Throw
            }
            finally {
                Restore-AmbientVariable -Saved $savedVariable
            }
        }
    }
}

# Behavioral evidence that the guard runs before provision-e2e-database.ps1 touches a database. Each
# run executes the real script and database-safety.psm1 from an isolated copy of the eng tree in a
# child pwsh, so stub modules, docker/dotnet functions and environment changes never reach this
# session. The child clears the keys the script resolves, points the stubs at per-run logs and writes
# a JSON record of the outcome and both logs, which these tests assert on:
# - env-utility.psm1 (stub): Resolve-DatabaseEngineEnvironmentFile returns the base file;
#   Test-NativeCommandWithTimeout logs the readiness probe and succeeds, so readiness ends on the
#   first probe. The real Assert-UnambiguousProtectedDeclaration and the env-utility functions it
#   calls are copied in.
# - schema-package-utility.psm1 (stub): Resolve-SchemaFilesFromEnvironmentFile logs and throws a
#   sentinel, so a control run stops before any reset.
# - docker and dotnet: child functions that log to the destructive log and throw.
# The collision runs are regression rows. Every one fails if the guard runs after readiness. The
# plain runs and the exported PostgreSQL run fail if the guard compares with -ieq alone; the exported
# SQL Server 'EDFI_DMS' differs from 'edfi_dms' only by case, which -ieq already rejects. The exported
# runs fail if Get-EnvironmentValueMap keeps the export prefix. The ambiguous-declaration and
# disagreeing-value runs are regression rows for Assert-UnambiguousProtectedDeclaration: without it the
# guard reads a distinct value from the map and the run reaches schema resolution. The control runs
# are positive controls; they show the harness reaches readiness and schema resolution.
Describe "Dedicated E2E database guard in the provisioning flow" {
    BeforeAll {
        $sourceDockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:flowRoot = Join-Path ([System.IO.Path]::GetTempPath()) "e2e-guard-flow-$([Guid]::NewGuid().ToString('N'))"
        $flowDockerComposeRoot = Join-Path $script:flowRoot "eng/docker-compose"
        New-Item -ItemType Directory -Path $flowDockerComposeRoot -Force | Out-Null

        foreach ($fileName in @("provision-e2e-database.ps1", "database-safety.psm1")) {
            Copy-Item -LiteralPath (Join-Path $sourceDockerComposeRoot $fileName) -Destination (Join-Path $flowDockerComposeRoot $fileName)
        }
        $script:provisionScript = Join-Path $flowDockerComposeRoot "provision-e2e-database.ps1"

        # The script's declaration check and the env-utility functions it calls, so the stub carries
        # the real definitions rather than stand-ins.
        $environmentUtilityAst = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $sourceDockerComposeRoot "env-utility.psm1"), [ref]$null, [ref]$null)
        $realEnvironmentFunctions = @(
            $environmentUtilityAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -in @(
                            "Get-DotenvLastDeclaration",
                            "Get-DotenvDependencyClosure",
                            "Get-SequentialEffectiveValue",
                            "Assert-UnambiguousProtectedDeclaration"
                        )
                }, $false) | ForEach-Object { $_.Extent.Text }
        ) -join "`n`n"

        @'
function Resolve-DatabaseEngineEnvironmentFile {
    param([string]$DatabaseEngine, [string]$BaseEnvironmentFile, [string]$DockerComposeRoot)
    return $BaseEnvironmentFile
}

function Test-NativeCommandWithTimeout {
    param([string]$FilePath, [string[]]$ArgumentList, [int]$TimeoutSeconds)
    Add-Content -LiteralPath $env:GUARD_FLOW_BOUNDARY_LOG -Value "readiness|$FilePath|$($ArgumentList[0])"
    return $true
}

#REAL-FUNCTIONS#

Export-ModuleMember -Function Resolve-DatabaseEngineEnvironmentFile, Test-NativeCommandWithTimeout, Get-DotenvLastDeclaration, Get-DotenvDependencyClosure, Get-SequentialEffectiveValue, Assert-UnambiguousProtectedDeclaration
'@.Replace("#REAL-FUNCTIONS#", $realEnvironmentFunctions) |
            Set-Content -LiteralPath (Join-Path $flowDockerComposeRoot "env-utility.psm1") -Encoding utf8

        @'
function Resolve-SchemaFilesFromEnvironmentFile {
    param(
        [string]$EnvironmentFilePath,
        [string]$Configuration,
        [string]$RepoRoot,
        [string]$SchemaDirectory,
        [string[]]$DownloaderDotnetRunArgs
    )
    Add-Content -LiteralPath $env:GUARD_FLOW_BOUNDARY_LOG -Value "schema-resolution|$([System.IO.Path]::GetFileName($EnvironmentFilePath))"
    throw "guard-flow sentinel: schema resolution reached"
}

Export-ModuleMember -Function Resolve-SchemaFilesFromEnvironmentFile
'@ | Set-Content -LiteralPath (Join-Path $script:flowRoot "eng/schema-package-utility.psm1") -Encoding utf8

        $script:childScript = Join-Path $script:flowRoot "invoke-provision.ps1"
        @'
param(
    [string]$ProvisionScript,
    [string]$EnvironmentFile,
    [string]$DatabaseEngine,
    [string]$DatabaseName,
    [string]$RunDirectory
)

$ErrorActionPreference = "Stop"
foreach ($name in @(
        "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "DATABASE_CONNECTION_STRING_ADMIN",
        "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING", "E2E_DATABASE_NAME", "POSTGRES_PORT",
        "POSTGRES_PASSWORD", "POSTGRES_USER", "MSSQL_PORT", "MSSQL_SA_PASSWORD", "DB_ALIAS"
    )) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}

$boundaryLog = Join-Path $RunDirectory "boundary.log"
$destructiveLog = Join-Path $RunDirectory "destructive.log"
$env:GUARD_FLOW_BOUNDARY_LOG = $boundaryLog
$env:GUARD_FLOW_DESTRUCTIVE_LOG = $destructiveLog

function global:docker {
    Add-Content -LiteralPath $env:GUARD_FLOW_DESTRUCTIVE_LOG -Value "docker|$($args -join ' ')"
    throw "guard-flow: docker must not run"
}

function global:dotnet {
    Add-Content -LiteralPath $env:GUARD_FLOW_DESTRUCTIVE_LOG -Value "dotnet|$($args -join ' ')"
    throw "guard-flow: dotnet must not run"
}

$outcome = "completed"
$message = $null
try {
    & $ProvisionScript -EnvironmentFile $EnvironmentFile -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName 6>$null
}
catch {
    $outcome = "threw"
    $message = $_.Exception.Message
}

[ordered]@{
    Outcome = $outcome
    Message = $message
    BoundaryLogExists = Test-Path -LiteralPath $boundaryLog
    BoundaryLog = @(if (Test-Path -LiteralPath $boundaryLog) { Get-Content -LiteralPath $boundaryLog })
    DestructiveLogExists = Test-Path -LiteralPath $destructiveLog
    DestructiveLog = @(if (Test-Path -LiteralPath $destructiveLog) { Get-Content -LiteralPath $destructiveLog })
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $RunDirectory "record.json") -Encoding utf8
'@ | Set-Content -LiteralPath $script:childScript -Encoding utf8

        $script:pwshPath = (Get-Process -Id $PID).Path

        function Invoke-ProvisionFlow {
            param(
                [string]$Name,
                [string]$DatabaseEngine,
                [string[]]$EnvironmentLines
            )

            $runDirectory = Join-Path $script:flowRoot "runs/$Name"
            New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
            $environmentFile = Join-Path $runDirectory ".env.flow"
            [System.IO.File]::WriteAllLines($environmentFile, $EnvironmentLines, [System.Text.UTF8Encoding]::new($false))

            $childOutput = & $script:pwshPath -NoProfile -NonInteractive -File $script:childScript `
                -ProvisionScript $script:provisionScript `
                -EnvironmentFile $environmentFile `
                -DatabaseEngine $DatabaseEngine `
                -DatabaseName "edfi_dms" `
                -RunDirectory $runDirectory 2>&1

            $recordPath = Join-Path $runDirectory "record.json"
            if (-not (Test-Path -LiteralPath $recordPath)) {
                throw "The provisioning child wrote no record. Output:`n$($childOutput -join "`n")"
            }

            $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            $record | Add-Member -NotePropertyName EnvironmentFile -NotePropertyValue $environmentFile
            return $record
        }
    }

    AfterAll {
        if ($script:flowRoot -and (Test-Path -LiteralPath $script:flowRoot)) {
            Remove-Item -LiteralPath $script:flowRoot -Recurse -Force
        }
    }

    It "stops a <Engine> run at the <Spelling> <Key> collision before any readiness probe, schema resolution or reset" -ForEach @(
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Spelling = "plain"; Lines = @("POSTGRES_PORT=5432", "POSTGRES_PASSWORD=guard-flow-pass", "POSTGRES_DB_NAME='edfi_dms '") }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Spelling = "plain"; Lines = @("MSSQL_PORT=1433", "MSSQL_SA_PASSWORD=guard-flow-pass", "MSSQL_DB_NAME=`"EDFI_DMS$([char]0x3000)`"") }
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Spelling = "exported"; Lines = @("POSTGRES_PORT=5432", "POSTGRES_PASSWORD=guard-flow-pass", "export POSTGRES_DB_NAME='edfi_dms '") }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Spelling = "exported"; Lines = @("MSSQL_PORT=1433", "MSSQL_SA_PASSWORD=guard-flow-pass", "export MSSQL_DB_NAME=EDFI_DMS") }
    ) {
        $record = Invoke-ProvisionFlow -Name "collision-$Spelling-$Engine" -DatabaseEngine $Engine -EnvironmentLines $Lines

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "E2E database 'edfi_dms' in '$($record.EnvironmentFile)' must be dedicated and cannot match $Key."
        $record.BoundaryLogExists | Should -BeFalse -Because "the guard must reject before the readiness probe and schema resolution"
        $record.DestructiveLogExists | Should -BeFalse -Because "no docker or dotnet command may run after a rejection"
    }

    It "stops a <Engine> run whose <Name> is <Ambiguity> before any readiness probe, schema resolution or reset" -ForEach @(
        @{
            Engine = "mssql"; Name = "MSSQL_DB_NAME"; Ambiguity = "redeclared by an export after a reference froze 'EDFI_DMS'"
            Lines = @('MSSQL_PORT=1433', 'MSSQL_SA_PASSWORD=guard-flow-pass', 'MSSQL_DB_NAME=EDFI_DMS', 'DATABASE_CONNECTION_STRING_ADMIN=Server=s;Database=${MSSQL_DB_NAME};User Id=sa', 'export MSSQL_DB_NAME=edfi_dms_primary')
            Locations = "MSSQL_DB_NAME on line 3, MSSQL_DB_NAME on line 5"
        }
        @{
            Engine = "postgresql"; Name = "POSTGRES_DB_NAME"; Ambiguity = "redeclared in lowercase after 'edfi_dms'"
            Lines = @('POSTGRES_PORT=5432', 'POSTGRES_PASSWORD=guard-flow-pass', 'POSTGRES_DB_NAME=edfi_dms', 'postgres_db_name=edfi_dms_primary')
            Locations = "POSTGRES_DB_NAME on line 3, postgres_db_name on line 4"
        }
    ) {
        $record = Invoke-ProvisionFlow -Name "ambiguous-$Engine" -DatabaseEngine $Engine -EnvironmentLines $Lines

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "E2E database safety check: '$($record.EnvironmentFile)' declares $Name more than once or in more than one letter case ($Locations), and a protected database setting depends on it. Docker Compose resolves the file line by line with case-sensitive names, so the value it uses can differ from the one this check compares; refusing a destructive reset. Keep a single declaration."
        $record.BoundaryLogExists | Should -BeFalse -Because "the refusal must come before the readiness probe and schema resolution"
        $record.DestructiveLogExists | Should -BeFalse -Because "no docker or dotnet command may run after a refusal"
    }

    It "stops a <Engine> run whose <Key> <Disagreement> before any readiness probe, schema resolution or reset" -ForEach @(
        @{
            Engine = "mssql"; Key = "MSSQL_DB_NAME"; Disagreement = "references DB_ALIAS before its declaration"
            Lines = @('MSSQL_PORT=1433', 'MSSQL_SA_PASSWORD=guard-flow-pass', 'MSSQL_DB_NAME=${DB_ALIAS:-edfi_dms}', 'DB_ALIAS=edfi_other')
        }
        @{
            Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Disagreement = "is declared only in lowercase"
            Lines = @('POSTGRES_PORT=5432', 'POSTGRES_PASSWORD=guard-flow-pass', 'postgres_db_name=edfi_other')
        }
    ) {
        $record = Invoke-ProvisionFlow -Name "disagreeing-$Engine" -DatabaseEngine $Engine -EnvironmentLines $Lines

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "E2E database safety check: '$($record.EnvironmentFile)' gives $Key a different value or presence under Docker Compose's line-by-line, case-sensitive resolution than the one this check compares. A reference sees only earlier declarations of the same letter case, and a declaration in another letter case is a different name; refusing a destructive reset. Declare each name once, in one letter case, before any line that references it. The values are withheld."
        $record.BoundaryLogExists | Should -BeFalse -Because "the refusal must come before the readiness probe and schema resolution"
        $record.DestructiveLogExists | Should -BeFalse -Because "no docker or dotnet command may run after a refusal"
    }

    It "lets a <Engine> run with a distinct <Spelling> <Key> reach readiness and schema resolution, then stop before reset" -ForEach @(
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Spelling = "plain"; Lines = @("POSTGRES_PORT=5432", "POSTGRES_PASSWORD=guard-flow-pass", "POSTGRES_DB_NAME='edfi_dms_primary '") }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Spelling = "plain"; Lines = @("MSSQL_PORT=1433", "MSSQL_SA_PASSWORD=guard-flow-pass", "MSSQL_DB_NAME=`"EDFI_DMS_PRIMARY$([char]0x3000)`"") }
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Spelling = "exported"; Lines = @("POSTGRES_PORT=5432", "POSTGRES_PASSWORD=guard-flow-pass", "export POSTGRES_DB_NAME='edfi_dms_primary '") }
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Spelling = "referenced"; Lines = @('LOG_LEVEL=Debug', 'POSTGRES_PORT=5432', 'POSTGRES_PASSWORD=guard-flow-pass', 'POSTGRES_DB_NAME=edfi_dms_primary', 'DATABASE_CONNECTION_STRING_ADMIN=host=h;database=${POSTGRES_DB_NAME};username=u', 'LOG_LEVEL=Information') }
    ) {
        $record = Invoke-ProvisionFlow -Name "control-$Spelling-$Engine" -DatabaseEngine $Engine -EnvironmentLines $Lines

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "guard-flow sentinel: schema resolution reached"
        $events = @($record.BoundaryLog | ForEach-Object { ($_ -split "\|")[0] })
        $events | Should -Contain "readiness"
        $events[-1] | Should -Be "schema-resolution" -Because "schema resolution follows readiness and its sentinel ends the run"
        $record.BoundaryLog[-1] | Should -BeExactly "schema-resolution|.env.flow" -Because "schema resolution reads the run's environment file"
        $record.DestructiveLogExists | Should -BeFalse -Because "the sentinel stops the run before Reset-* and SchemaTools"
    }
}

# Behavioral evidence that the Instance Management setup validates all three route-context names before
# it starts infrastructure or provisions a database. The real setup-local-dms.ps1, env-utility.psm1 and
# database-safety.psm1 run from an isolated tree at their real relative depth, in a child pwsh invoked
# with -ResolvedEnvironmentFile so no overlay composition runs. Stubs in that tree record each call to a
# per-run log:
# - dms-schema-environment.psm1: Invoke-WithDmsEnvironmentFileSchemaAuthority records the phase and
#   runs the action.
# - start-local-dms.ps1: records its arguments and throws a sentinel, so a control run stops at the
#   first -InfraOnly start.
# - provision-e2e-database.ps1: records any call.
# - docker: a child function that records its arguments and sets $global:LASTEXITCODE to 0, so the
#   setup's 'docker version' check passes. That check runs before the guard by design.
# The collision runs are regression rows: they fail if the guard compares with -ieq alone or the names
# are validated after the first start. The disagreeing-value run is a regression row for the setup's
# Assert-UnambiguousProtectedDeclaration call: without it the guard accepts the map's distinct value
# and the run reaches the first start. The control runs are positive controls; they show the harness
# reaches the first start.
Describe "Dedicated E2E database guard in the Instance Management setup" {
    BeforeAll {
        $sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:setupRoot = Join-Path ([System.IO.Path]::GetTempPath()) "e2e-guard-im-$([Guid]::NewGuid().ToString('N'))"
        $setupScriptDirectory = Join-Path $script:setupRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E"
        $setupDockerComposeRoot = Join-Path $script:setupRoot "eng/docker-compose"
        New-Item -ItemType Directory -Path $setupScriptDirectory, $setupDockerComposeRoot -Force | Out-Null

        Copy-Item -LiteralPath (Join-Path $sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1") -Destination $setupScriptDirectory
        foreach ($fileName in @("env-utility.psm1", "database-safety.psm1")) {
            Copy-Item -LiteralPath (Join-Path $sourceRepoRoot "eng/docker-compose/$fileName") -Destination (Join-Path $setupDockerComposeRoot $fileName)
        }
        $script:setupScript = Join-Path $setupScriptDirectory "setup-local-dms.ps1"

        @'
function Invoke-WithDmsEnvironmentFileSchemaAuthority {
    param([Parameter(Mandatory)] [scriptblock]$Action)
    Add-Content -LiteralPath $env:GUARD_IM_AUTHORITY_LOG -Value "phase"
    & $Action
}

Export-ModuleMember -Function Invoke-WithDmsEnvironmentFileSchemaAuthority
'@ | Set-Content -LiteralPath (Join-Path $setupDockerComposeRoot "dms-schema-environment.psm1") -Encoding utf8

        @'
Add-Content -LiteralPath $env:GUARD_IM_START_LOG -Value ($args -join " ")
throw "guard-im sentinel: start-local-dms reached"
'@ | Set-Content -LiteralPath (Join-Path $setupDockerComposeRoot "start-local-dms.ps1") -Encoding utf8

        @'
Add-Content -LiteralPath $env:GUARD_IM_PROVISION_LOG -Value ($args -join " ")
'@ | Set-Content -LiteralPath (Join-Path $setupDockerComposeRoot "provision-e2e-database.ps1") -Encoding utf8

        $script:setupChildScript = Join-Path $script:setupRoot "invoke-setup.ps1"
        @'
param(
    [string]$SetupScript,
    [string]$ResolvedEnvironmentFile,
    [string]$DatabaseEngine,
    [string]$RunDirectory
)

foreach ($name in @(
        "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "DATABASE_CONNECTION_STRING_ADMIN",
        "CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING", "INSTANCE_E2E_DATABASE_1_NAME",
        "INSTANCE_E2E_DATABASE_2_NAME", "INSTANCE_E2E_DATABASE_3_NAME", "POSTGRES_USER", "DB_ALIAS"
    )) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}

$logs = [ordered]@{
    Authority = Join-Path $RunDirectory "authority.log"
    Start = Join-Path $RunDirectory "start.log"
    Provision = Join-Path $RunDirectory "provision.log"
    Docker = Join-Path $RunDirectory "docker.log"
}
$env:GUARD_IM_AUTHORITY_LOG = $logs.Authority
$env:GUARD_IM_START_LOG = $logs.Start
$env:GUARD_IM_PROVISION_LOG = $logs.Provision
$env:GUARD_IM_DOCKER_LOG = $logs.Docker

function global:docker {
    Add-Content -LiteralPath $env:GUARD_IM_DOCKER_LOG -Value ($args -join " ")
    $global:LASTEXITCODE = 0
    "docker stub"
}

$hadExitCode = Test-Path -LiteralPath "Variable:global:LASTEXITCODE"
$priorExitCode = if ($hadExitCode) { $global:LASTEXITCODE } else { $null }
$outcome = "completed"
$message = $null
try {
    & $SetupScript -SkipDockerBuild -DatabaseEngine $DatabaseEngine -ResolvedEnvironmentFile $ResolvedEnvironmentFile *> $null
}
catch {
    $outcome = "threw"
    $message = $_.Exception.Message
}
finally {
    if ($hadExitCode) {
        $global:LASTEXITCODE = $priorExitCode
    }
    else {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}

$record = [ordered]@{ Outcome = $outcome; Message = $message }
foreach ($name in $logs.Keys) {
    $record["$($name)LogExists"] = Test-Path -LiteralPath $logs[$name]
    $record["$($name)Log"] = @(if (Test-Path -LiteralPath $logs[$name]) { Get-Content -LiteralPath $logs[$name] })
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $RunDirectory "record.json") -Encoding utf8
'@ | Set-Content -LiteralPath $script:setupChildScript -Encoding utf8

        $script:pwshPath = (Get-Process -Id $PID).Path

        function Invoke-InstanceSetup {
            param(
                [string]$Name,
                [string]$DatabaseEngine,
                [string[]]$EnvironmentLines
            )

            $runDirectory = Join-Path $script:setupRoot "runs/$Name"
            New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
            $environmentFile = Join-Path $runDirectory ".env.instance"
            [System.IO.File]::WriteAllLines($environmentFile, $EnvironmentLines, [System.Text.UTF8Encoding]::new($false))

            $childOutput = & $script:pwshPath -NoProfile -NonInteractive -File $script:setupChildScript `
                -SetupScript $script:setupScript `
                -ResolvedEnvironmentFile $environmentFile `
                -DatabaseEngine $DatabaseEngine `
                -RunDirectory $runDirectory 2>&1

            $recordPath = Join-Path $runDirectory "record.json"
            if (-not (Test-Path -LiteralPath $recordPath)) {
                throw "The setup child wrote no record. Output:`n$($childOutput -join "`n")"
            }

            $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            $record | Add-Member -NotePropertyName EnvironmentFile -NotePropertyValue $environmentFile
            return $record
        }

        $script:routeNameLines = @(
            "INSTANCE_E2E_DATABASE_1_NAME=edfi_route_d1",
            "INSTANCE_E2E_DATABASE_2_NAME=edfi_route_d2",
            "INSTANCE_E2E_DATABASE_3_NAME=edfi_route_d3"
        )
    }

    AfterAll {
        if ($script:setupRoot -and (Test-Path -LiteralPath $script:setupRoot)) {
            Remove-Item -LiteralPath $script:setupRoot -Recurse -Force
        }
    }

    It "rejects a <Engine> setup whose third route name matches <Key> before any start or provisioning" -ForEach @(
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Protected = "POSTGRES_DB_NAME='edfi_route_d3 '" }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Protected = "MSSQL_DB_NAME=`"EDFI_ROUTE_D3$([char]0x3000)`"" }
    ) {
        $record = Invoke-InstanceSetup -Name "collision-$Engine" -DatabaseEngine $Engine -EnvironmentLines ($script:routeNameLines + $Protected)

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "E2E database 'edfi_route_d3' in '$($record.EnvironmentFile)' must be dedicated and cannot match $Key."
        $record.AuthorityLogExists | Should -BeFalse -Because "no guarded phase may begin after a rejection"
        $record.StartLogExists | Should -BeFalse -Because "infrastructure must not start after a rejection"
        $record.ProvisionLogExists | Should -BeFalse -Because "route database 1 must never be provisioned before a later name fails"
        ($record.DockerLog -join "`n") | Should -BeExactly "version" -Because "only the read-only docker version check precedes the guard"
    }

    It "rejects an mssql setup whose MSSQL_DB_NAME references DB_ALIAS before its declaration, before any start or provisioning" {
        $record = Invoke-InstanceSetup -Name "disagreeing-mssql" -DatabaseEngine "mssql" -EnvironmentLines ($script:routeNameLines + @(
                'MSSQL_DB_NAME=${DB_ALIAS:-edfi_route_d3}',
                'DB_ALIAS=edfi_other'
            ))

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "E2E database safety check: '$($record.EnvironmentFile)' gives MSSQL_DB_NAME a different value or presence under Docker Compose's line-by-line, case-sensitive resolution than the one this check compares. A reference sees only earlier declarations of the same letter case, and a declaration in another letter case is a different name; refusing a destructive reset. Declare each name once, in one letter case, before any line that references it. The values are withheld."
        $record.AuthorityLogExists | Should -BeFalse -Because "no guarded phase may begin after a refusal"
        $record.StartLogExists | Should -BeFalse -Because "infrastructure must not start after a refusal"
        $record.ProvisionLogExists | Should -BeFalse -Because "no route database may be provisioned after a refusal"
        ($record.DockerLog -join "`n") | Should -BeExactly "version" -Because "only the read-only docker version check precedes the declaration check"
    }

    It "lets a <Engine> setup with a distinct <Key> reach the first -InfraOnly start, then stop" -ForEach @(
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Protected = "POSTGRES_DB_NAME='edfi_primary '" }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Protected = "MSSQL_DB_NAME=`"EDFI_PRIMARY$([char]0x3000)`"" }
    ) {
        $record = Invoke-InstanceSetup -Name "control-$Engine" -DatabaseEngine $Engine -EnvironmentLines ($script:routeNameLines + $Protected)

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "guard-im sentinel: start-local-dms reached"
        ($record.AuthorityLog -join "`n") | Should -BeExactly "phase"
        @($record.StartLog).Count | Should -Be 1 -Because "the start sentinel ends the run at the first start"
        $startArguments = $record.StartLog[0]
        $startArguments | Should -Match "(^| )-InfraOnly( |$)"
        $startArguments | Should -Match ([regex]::Escape("-EnvironmentFile $($record.EnvironmentFile)"))
        $startArguments | Should -Match "(^| )-DatabaseEngine $Engine( |$)"
        $record.ProvisionLogExists | Should -BeFalse -Because "the start sentinel stops the run before provisioning"
        ($record.DockerLog -join "`n") | Should -BeExactly "version"
    }
}
