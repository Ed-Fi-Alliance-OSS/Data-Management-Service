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
# are regression cases: comparer rows assert that -ieq alone returns false for them, and the guard
# matrix keeps them in the context named for what -ieq misses, so they fail if either the comparer or
# the guard's use of it is reduced to -ieq. Every other guard context is preservation. Characters are
# written as code points; no literal non-ASCII text.
#
# Comparison-domain rows are classified by the verdict the guard gave before the domain check existed,
# measured per key kind: a spelling it accepted is a regression row whose only rejection is the domain
# refusal; a spelling -ieq already rejected is a preservation row that accepts either the collision or
# the domain diagnostic for its key.

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

    # Spellings outside the comparison domain, with the verdict the guard gave each before the domain
    # check, measured separately for direct keys (a double-quoted value) and connection-string keys (a
    # provider-quoted Database). The extractor's secondary conversion trims the connection-string
    # candidate, so a trailing U+00A0 or a leading U+0020 already collides there.
    $script:outOfDomainSpellings = @(
        @{ Label = "full-width U+FF45 for the first letter"; Name = "$([char]0xFF45)dfi_dms"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi_dms + U+1F600"; Name = "edfi_dms$([char]::ConvertFromUtf32(0x1F600))"; Direct = "Accepted"; ConnectionString = "Accepted" }
        @{ Label = "edfi_dms + U+00A0"; Name = "edfi_dms$([char]0x00A0)"; Direct = "Accepted"; ConnectionString = "Collision" }
        @{ Label = "U+0020 + edfi_dms"; Name = " edfi_dms"; Direct = "Accepted"; ConnectionString = "Collision" }
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
    # parses from the Compose-resolved string, the candidate the extractor returns after its
    # per-value conversion, and the guard's verdict against the E2E database 'edfi_dms'. The pinned
    # SqlClient 6.1.4 and Npgsql 8.0.4 builders were measured outside CI (the lane has no restored
    # NuGet cache) to trim an unquoted value and keep a quoted one verbatim, as the generic parse
    # does in the whitespace rows; for two keywords SqlClient keeps the later Initial Catalog and
    # Npgsql the later DB. Every row is preservation: the guard gave the same verdict before these
    # tests existed.
    $inDomainName = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"
    $script:connectionStringStageSpellings = @(
        @{ Label = "unquoted Database 'edfi_dms' + U+0020"; Raw = "Server=s;Database=edfi_dms ;User Id=sa"; Parsed = @("edfi_dms"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "double-quoted Database 'edfi_dms' + U+0020"; Raw = "Server=s;Database=`"edfi_dms `";User Id=sa"; Parsed = @("edfi_dms "); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "single-quoted Database 'edfi_dms' + U+0020"; Raw = "Server=s;Database='edfi_dms ';User Id=sa"; Parsed = @("edfi_dms "); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "double-quoted Database 'edfi_dms' + U+3000"; Raw = "Server=s;Database=`"edfi_dms$([char]0x3000)`";User Id=sa"; Parsed = @("edfi_dms$([char]0x3000)"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "double-quoted Database 'edfi_dms' + TAB"; Raw = "Server=s;Database=`"edfi_dms`t`";User Id=sa"; Parsed = @("edfi_dms`t"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "double-quoted Database 'edfi_dms' + U+00A0"; Raw = "Server=s;Database=`"edfi_dms$([char]0x00A0)`";User Id=sa"; Parsed = @("edfi_dms$([char]0x00A0)"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "double-quoted Database U+0020 + 'edfi_dms'"; Raw = "Server=s;Database=`" edfi_dms`";User Id=sa"; Parsed = @(" edfi_dms"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "Database with quotes inside the value"; Raw = "Server=s;Database=`"'edfi_dms'`";User Id=sa"; Parsed = @("'edfi_dms'"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "Database 'edfi_dms #x' kept by single outer quotes"; Raw = "'Server=s;Database=`"edfi_dms #x`";User Id=sa'"; Parsed = @("edfi_dms #x"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "Database literal `${GUARD_STAGE_NAME} with GUARD_STAGE_NAME = 'edfi_dms'"; Raw = "Server=s;Database=`$`${GUARD_STAGE_NAME};User Id=sa"; StageName = "edfi_dms"; Parsed = @("`${GUARD_STAGE_NAME}"); Candidates = @("edfi_dms"); Verdict = "Collision" }
        @{ Label = "Database literal `${GUARD_STAGE_NAME} with GUARD_STAGE_NAME undefined"; Raw = "Server=s;Database=`$`${GUARD_STAGE_NAME};User Id=sa"; Parsed = @("`${GUARD_STAGE_NAME}"); Candidates = @(""); Verdict = "Undetermined" }
        @{ Label = "Database with a '`$' a single-quoted value keeps"; Raw = "Server=s;Database=`"'edfi`$`$x'`";User Id=sa"; Parsed = @("'edfi`$x'"); Candidates = @("edfi`$x"); Verdict = "Unresolved" }
        @{ Label = "Database 'edfi`$dms', a parsing limitation"; Raw = "Server=s;Database=edfi`$`$dms;User Id=sa"; Parsed = @("edfi`$dms"); Candidates = @("edfi"); Verdict = "Accepted" }
        @{ Label = "Database 'other #x' kept by single outer quotes, a parsing limitation"; Raw = "'Server=s;Database=other #x;User Id=sa'"; Parsed = @("other #x"); Candidates = @("other"); Verdict = "Accepted" }
        @{ Label = "unquoted Database of every in-domain character"; Raw = "Server=s;Database=$inDomainName;User Id=sa"; Parsed = @($inDomainName); Candidates = @($inDomainName); Verdict = "Accepted" }
        @{ Label = "double-quoted DB of every in-domain character"; Raw = "Host=h;DB=`"$inDomainName`";Username=u"; Parsed = @($inDomainName); Candidates = @($inDomainName); Verdict = "Accepted" }
        @{ Label = "single-quoted Initial Catalog of every in-domain character"; Raw = "Server=s;Initial Catalog='$inDomainName';User Id=sa"; Parsed = @($inDomainName); Candidates = @($inDomainName); Verdict = "Accepted" }
        @{ Label = "double-quoted Database with full-width U+FF45"; Raw = "Server=s;Database=`"$([char]0xFF45)dfi_dms`";User Id=sa"; Parsed = @("$([char]0xFF45)dfi_dms"); Candidates = @("$([char]0xFF45)dfi_dms"); Verdict = "Domain" }
        @{ Label = "Database 'first' then Initial Catalog 'edfi_dms', which SqlClient uses"; Raw = "Server=s;Database=first;Initial Catalog=edfi_dms;User Id=sa"; Parsed = @("first", "edfi_dms"); Candidates = @("first", "edfi_dms"); Verdict = "Collision" }
        @{ Label = "Database 'edfi_dms' then DB 'other', which Npgsql uses"; Raw = "Host=h;Database=edfi_dms;DB=other;Username=u"; Parsed = @("edfi_dms", "other"); Candidates = @("edfi_dms", "other"); Verdict = "Collision" }
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

        function Get-DomainDiagnostic {
            param([string]$Key)
            $source = if ($Key -in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) { $Key } else { "the database name from $Key" }
            "E2E database safety check cannot prove $source in '$script:environmentFileLabel' dedicated: after trailing whitespace is removed it contains characters outside the guard's supported comparison domain (ASCII letters, digits, underscore); refusing a destructive reset. The value is withheld."
        }

        function Get-GuardRejectionMessage {
            param([hashtable]$EnvironmentValues)
            try {
                Invoke-IsolatedGuard -EnvironmentValues $EnvironmentValues
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
                Get-DatabaseNameFromConnectionString -ConnectionString $connectionString -EnvironmentValues $environmentValues
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
                    Candidates = @(Get-DatabaseNameFromConnectionString -ConnectionString $connectionString -EnvironmentValues $EnvironmentValues)
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
                Should -Throw -ExpectedMessage "E2E database safety check could not resolve $Key in '$script:environmentFileLabel' (blank, or an unresolved or cyclic reference)*"
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

    # The extractor already trims each connection-string candidate, so the E2E name is the only side
    # these rows leave untrimmed.
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

            (@(Get-IsolatedConnectionStringDatabaseName -Key $Key -RawValue $rawValue) -join "|") | Should -BeExactly "x|edfi_dms"
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
        It "reads <Label> in <Key>" -ForEach $script:connectionStringStageRows {
            $environmentValues = @{ $Key = $Raw }
            if ($null -ne $StageName) {
                $environmentValues["GUARD_STAGE_NAME"] = $StageName
            }
            # The rows reference GUARD_STAGE_NAME, and 'edfi$$dms' reaches the per-value conversion as
            # 'edfi$dms', which expands a variable named dms; both are cleared from the process
            # environment for the extraction and the guard run, then restored.
            $helperVariables = @("GUARD_STAGE_NAME", "dms")

            $stage = Get-IsolatedConnectionStringStage -Key $Key -EnvironmentValues $environmentValues -HelperVariables $helperVariables

            $stage.Parsed.Count | Should -Be $Parsed.Count
            ($stage.Parsed -join "|") | Should -BeExactly ($Parsed -join "|")
            $stage.Candidates.Count | Should -Be $Candidates.Count
            ($stage.Candidates -join "|") | Should -BeExactly ($Candidates -join "|")

            $guard = { Invoke-IsolatedGuard -EnvironmentValues $environmentValues -HelperVariables $helperVariables }
            switch ($Verdict) {
                "Collision" { $guard | Should -Throw -ExpectedMessage (Get-SeparateDiagnostic -Key $Key) }
                "Domain" { $guard | Should -Throw -ExpectedMessage (Get-DomainDiagnostic -Key $Key) }
                "Undetermined" { $guard | Should -Throw -ExpectedMessage "E2E database safety check could not determine a database name from $Key in '$script:environmentFileLabel'." }
                "Unresolved" { $guard | Should -Throw -ExpectedMessage "E2E database safety check could not fully resolve the database name from $Key in '$script:environmentFileLabel' (unresolved or cyclic reference)*" }
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

    Context "ambient isolation" {
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
#   first probe.
# - schema-package-utility.psm1 (stub): Resolve-SchemaFilesFromEnvironmentFile logs and throws a
#   sentinel, so a control run stops before any reset.
# - docker and dotnet: child functions that log to the destructive log and throw.
# The collision runs fail if the guard compares with -ieq alone or runs after readiness; the
# control runs are preservation.
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

Export-ModuleMember -Function Resolve-DatabaseEngineEnvironmentFile, Test-NativeCommandWithTimeout
'@ | Set-Content -LiteralPath (Join-Path $flowDockerComposeRoot "env-utility.psm1") -Encoding utf8

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
        "POSTGRES_PASSWORD", "POSTGRES_USER", "MSSQL_PORT", "MSSQL_SA_PASSWORD"
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

    It "stops a <Engine> run at the <Key> collision before any readiness probe, schema resolution or reset" -ForEach @(
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Lines = @("POSTGRES_PORT=5432", "POSTGRES_PASSWORD=guard-flow-pass", "POSTGRES_DB_NAME='edfi_dms '") }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Lines = @("MSSQL_PORT=1433", "MSSQL_SA_PASSWORD=guard-flow-pass", "MSSQL_DB_NAME=`"EDFI_DMS$([char]0x3000)`"") }
    ) {
        $record = Invoke-ProvisionFlow -Name "collision-$Engine" -DatabaseEngine $Engine -EnvironmentLines $Lines

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "E2E database 'edfi_dms' in '$($record.EnvironmentFile)' must be dedicated and cannot match $Key."
        $record.BoundaryLogExists | Should -BeFalse -Because "the guard must reject before the readiness probe and schema resolution"
        $record.DestructiveLogExists | Should -BeFalse -Because "no docker or dotnet command may run after a rejection"
    }

    It "lets a <Engine> run with a distinct <Key> reach readiness and schema resolution, then stop before reset" -ForEach @(
        @{ Engine = "postgresql"; Key = "POSTGRES_DB_NAME"; Lines = @("POSTGRES_PORT=5432", "POSTGRES_PASSWORD=guard-flow-pass", "POSTGRES_DB_NAME='edfi_dms_primary '") }
        @{ Engine = "mssql"; Key = "MSSQL_DB_NAME"; Lines = @("MSSQL_PORT=1433", "MSSQL_SA_PASSWORD=guard-flow-pass", "MSSQL_DB_NAME=`"EDFI_DMS_PRIMARY$([char]0x3000)`"") }
    ) {
        $record = Invoke-ProvisionFlow -Name "control-$Engine" -DatabaseEngine $Engine -EnvironmentLines $Lines

        $record.Outcome | Should -Be "threw"
        $record.Message | Should -BeExactly "guard-flow sentinel: schema resolution reached"
        ($record.BoundaryLog -join "`n") | Should -BeExactly "readiness|docker|exec`nschema-resolution|.env.flow"
        $record.DestructiveLogExists | Should -BeFalse -Because "the sentinel stops the run before Reset-* and SchemaTools"
    }
}
