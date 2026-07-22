# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1284: these tests exercise two self-contained helpers by extracting just the function definition
# from its script via the PowerShell AST and invoking it, without executing the script's top-level body
# (build-dms.ps1's Invoke-Main or the setup script's Docker orchestration).
#
# - Invoke-WithE2ETestProcessContext (build-dms.ps1) must restore every environment variable it mutates
#   to its exact prior state, preserving the unset-versus-valued distinction (PowerShell retains empty
#   and whitespace-valued environment variables).
# - Get-DirectSetupTeardownCommand (standard setup-local-dms.ps1) must print a teardown command carrying
#   the selected engine and the resolved environment-file path, safely single-quoted.

param()

Describe "Invoke-WithE2ETestProcessContext restores prior environment state exactly (DMS-1284)" {
    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $buildScript -FunctionName "Invoke-WithE2ETestProcessContext")))

        $script:mutatedVariables = @(
            "AppSettings__DataStoreDatabaseName"
            "AppSettings__DatabaseEngine"
            "AppSettings__DataStoreAdminConnectionString"
            "AppSettings__DataStoreConnectionString"
            "NODE_OPTIONS"
        )

        $script:testSettings = [pscustomobject]@{
            DataStoreDatabaseName          = "edfi_datamanagementservice_e2e"
            DatabaseEngine                 = "mssql"
            DataStoreAdminConnectionString = "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
            DataStoreConnectionString      = "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
        }
    }

    AfterEach {
        foreach ($name in $script:mutatedVariables) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    It "restores AppSettings__DatabaseEngine to its prior <Label> state after the action throws" -ForEach @(
        @{ Label = "absent"; Setup = { Remove-Item Env:AppSettings__DatabaseEngine -ErrorAction SilentlyContinue }; ExpectExists = $false; ExpectValue = $null }
        @{ Label = "empty"; Setup = { $env:AppSettings__DatabaseEngine = "" }; ExpectExists = $true; ExpectValue = "" }
        @{ Label = "whitespace"; Setup = { $env:AppSettings__DatabaseEngine = "   " }; ExpectExists = $true; ExpectValue = "   " }
        @{ Label = "nonempty"; Setup = { $env:AppSettings__DatabaseEngine = "prior-engine" }; ExpectExists = $true; ExpectValue = "prior-engine" }
    ) {
        & $Setup

        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:AppSettings__DatabaseEngine) | Should -Be $ExpectExists
        if ($ExpectExists) {
            $env:AppSettings__DatabaseEngine | Should -Be $ExpectValue
        }
    }

    It "sets the engine and both connection strings for the action, then restores them" {
        Remove-Item Env:AppSettings__DatabaseEngine -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreAdminConnectionString -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreConnectionString -ErrorAction SilentlyContinue

        $observed = $null
        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action {
                $script:observed = [pscustomobject]@{
                    Engine       = $env:AppSettings__DatabaseEngine
                    Admin        = $env:AppSettings__DataStoreAdminConnectionString
                    Registration = $env:AppSettings__DataStoreConnectionString
                }
                throw "boom"
            } } | Should -Throw

        $script:observed.Engine | Should -Be "mssql"
        $script:observed.Admin | Should -Be $script:testSettings.DataStoreAdminConnectionString
        $script:observed.Registration | Should -Be $script:testSettings.DataStoreConnectionString

        (Test-Path Env:AppSettings__DatabaseEngine) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreAdminConnectionString) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreConnectionString) | Should -BeFalse
    }

    It "restores every mutated variable from a mix of absent, empty, whitespace, and valued prior states" {
        Remove-Item Env:AppSettings__DataStoreDatabaseName -ErrorAction SilentlyContinue
        $env:AppSettings__DatabaseEngine = ""
        $env:AppSettings__DataStoreAdminConnectionString = "   "
        $env:AppSettings__DataStoreConnectionString = "prior-registration"
        $env:NODE_OPTIONS = "--max-old-space-size=4096"

        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:AppSettings__DataStoreDatabaseName) | Should -BeFalse
        (Test-Path Env:AppSettings__DatabaseEngine) | Should -BeTrue
        $env:AppSettings__DatabaseEngine | Should -Be ""
        $env:AppSettings__DataStoreAdminConnectionString | Should -Be "   "
        $env:AppSettings__DataStoreConnectionString | Should -Be "prior-registration"
        $env:NODE_OPTIONS | Should -Be "--max-old-space-size=4096"
    }
}

Describe "Get-DirectSetupTeardownCommand carries the engine and resolved environment file (DMS-1284)" {
    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $setupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $setupScript -FunctionName "Get-DirectSetupTeardownCommand")))
    }

    It "includes the selected engine and a single-quoted environment-file path" {
        $command = Get-DirectSetupTeardownCommand -DatabaseEngine mssql -EnvironmentFile "/repo/eng/docker-compose/.env.e2e"

        $command | Should -Be "./teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '/repo/eng/docker-compose/.env.e2e'"
    }

    It "safely single-quotes a path containing spaces and embedded single quotes" {
        $command = Get-DirectSetupTeardownCommand -DatabaseEngine postgresql -EnvironmentFile "C:\my env\o'brien.env"

        $command | Should -Be "./teardown-local-dms.ps1 -DatabaseEngine postgresql -EnvironmentFile 'C:\my env\o''brien.env'"
    }
}

Describe "setup-local-dms.ps1 wires the resolved environment file into teardown guidance (DMS-1284)" {
    BeforeAll {
        $setupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        $script:setupSource = Get-Content -LiteralPath $setupScript -Raw
    }

    # The teardown-guidance call is a non-executed display line, so a source assertion is the
    # appropriate check that production wires the single-resolution output (data-standard then engine
    # overlay) into teardown rather than the pre-overlay base file.
    It "passes the resolved environment file, not the base, to Get-DirectSetupTeardownCommand" {
        $script:setupSource |
            Should -Match 'Get-DirectSetupTeardownCommand -DatabaseEngine \$DatabaseEngine -EnvironmentFile \$resolvedEnvironmentFile'
        $script:setupSource |
            Should -Not -Match 'Get-DirectSetupTeardownCommand[^\r\n]*-EnvironmentFile \$baseEnvironmentFile'
    }
}
