# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1284: the SQL Server E2E reset must set the database to single-user and drop it in ONE batch on
# ONE connection. If the two statements ran in separate sqlcmd invocations, a client could seize the
# single-user slot between them and block/fail the drop. Get-MssqlResetBatch is AST-extracted so the
# batch shape can be asserted without running provision-e2e-database.ps1's param/import body.

Describe "provision-e2e-database Get-MssqlResetBatch (DMS-1284)" {
    BeforeAll {
        function Get-ScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName },
                $true
            ) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:provisionScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../provision-e2e-database.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:provisionScript -FunctionName "Get-MssqlResetBatch")))
    }

    It "emits SET SINGLE_USER and DROP DATABASE in one guarded batch, drop after the single-user switch" {
        $batch = Get-MssqlResetBatch -DatabaseName "edfi_datamanagementservice_e2e"

        $batch | Should -Match "IF DB_ID\(N'edfi_datamanagementservice_e2e'\) IS NOT NULL"
        $batch | Should -Match "ALTER DATABASE \[edfi_datamanagementservice_e2e\] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"
        $batch | Should -Match "DROP DATABASE \[edfi_datamanagementservice_e2e\];"

        # Both statements are in the same string (one sqlcmd -Q batch), and the drop follows the switch,
        # so no separate invocation can slip in and take the single-user slot.
        $singleUserIndex = $batch.IndexOf("SET SINGLE_USER")
        $dropIndex = $batch.IndexOf("DROP DATABASE")
        $singleUserIndex | Should -BeGreaterThan 0
        $dropIndex | Should -BeGreaterThan $singleUserIndex
    }

    It "brackets the database name so a name is treated as a single identifier" {
        (Get-MssqlResetBatch -DatabaseName "custom_db_9") | Should -Match "\[custom_db_9\]"
    }
}
