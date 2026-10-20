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
