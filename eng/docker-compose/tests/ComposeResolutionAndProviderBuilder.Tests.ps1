# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1284 FR8/FR10: the E2E startup readiness, provisioning, CMS/test, and destructive-safety phases all
# resolve credentials/ports through one Compose-equivalent resolver (database-safety.psm1) so an ambient
# process/shell override, a reference chain, or a special-character credential is read exactly as the
# running container sees it, and provider connection strings are built through DbConnectionStringBuilder
# rather than raw interpolation. These tests exercise the shared primitives directly.

Describe "Shared Compose resolution and safe provider builder (DMS-1284)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
        # Dot-source provision (its dot-source guard returns before any provisioning) to expose the
        # Build-ConnectionString provider-string builder without connecting to a database.
        . (Join-Path $script:dockerComposeRoot "provision-e2e-database.ps1")
    }

    Context "Get-ComposeResolvedEnvValue" {
        It "gives a process/shell value precedence over the env file" {
            $priorExists = Test-Path "Env:FR10_PWD"
            $priorValue = [System.Environment]::GetEnvironmentVariable("FR10_PWD")
            try {
                [System.Environment]::SetEnvironmentVariable("FR10_PWD", "AmbientPort9!")
                Get-ComposeResolvedEnvValue -EnvironmentValues @{ FR10_PWD = "FileValue" } -Name "FR10_PWD" |
                    Should -Be "AmbientPort9!"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("FR10_PWD", $priorValue) }
                else { Remove-Item Env:FR10_PWD -ErrorAction SilentlyContinue }
            }
        }

        It "lets an ambient override of a referenced variable win through a reference chain" {
            $priorExists = Test-Path "Env:FR10_REF"
            $priorValue = [System.Environment]::GetEnvironmentVariable("FR10_REF")
            try {
                [System.Environment]::SetEnvironmentVariable("FR10_REF", "AmbientRef9!")
                Get-ComposeResolvedEnvValue -EnvironmentValues @{ TOP = '${FR10_REF}'; FR10_REF = "FileRef" } -Name "TOP" |
                    Should -Be "AmbientRef9!"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("FR10_REF", $priorValue) }
                else { Remove-Item Env:FR10_REF -ErrorAction SilentlyContinue }
            }
        }

        It "keeps a single-quoted referenced value literal through a reference chain" {
            $values = @{ TOP = '${SHARED}'; SHARED = "'`${OTHER}'"; OTHER = "should-not-expand" }
            Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name "TOP" | Should -Be '${OTHER}'
        }

        It "preserves connection-string metacharacters in a resolved value" {
            $special = 'Aa1!;=,"x'
            Get-ComposeResolvedEnvValue -EnvironmentValues @{ P = $special } -Name "P" | Should -Be $special
        }

        It "falls back to the documented default when the key is absent" {
            Get-ComposeResolvedEnvValue -EnvironmentValues @{} -Name "ABSENT" -DefaultValue "def" | Should -Be "def"
        }
    }

    Context "Get-RequiredComposeResolvedEnvValue" {
        It "returns the resolved value when present" {
            Get-RequiredComposeResolvedEnvValue -EnvironmentValues @{ K = "value" } -Name "K" | Should -Be "value"
        }

        It "throws when the required value is absent in both the environment and the file" {
            { Get-RequiredComposeResolvedEnvValue -EnvironmentValues @{} -Name "MISSING_REQUIRED" } |
                Should -Throw "*MISSING_REQUIRED*is not set*"
        }
    }

    Context "provision Build-ConnectionString safe builder" {
        It "quotes a <Dialect> password with connection-string metacharacters so it round-trips intact" -ForEach @(
            @{ Dialect = "mssql"; DbHost = "127.0.0.1"; Port = "1435" }
            @{ Dialect = "pgsql"; DbHost = "localhost"; Port = "5435" }
        ) {
            # Raw string interpolation would let the ';' terminate the connection string early and drop
            # the rest of the password; the DbConnectionStringBuilder form quotes it per ADO.NET rules.
            $password = 'pa;ss"wo''rd=x'
            # Build the SecureString via AppendChar (as provision-e2e-database.ps1 does) rather than
            # ConvertTo-SecureString -AsPlainText, which PSScriptAnalyzer rejects.
            $securePassword = [System.Security.SecureString]::new()
            foreach ($character in $password.ToCharArray()) { $securePassword.AppendChar($character) }
            $securePassword.MakeReadOnly()
            $credential = [System.Management.Automation.PSCredential]::new("sa", $securePassword)

            $connectionString = Build-ConnectionString -ServerHost $DbHost -Port $Port -Credential $credential -DatabaseName "edfi_e2e" -Dialect $Dialect

            $parsed = [System.Data.Common.DbConnectionStringBuilder]::new()
            $parsed.set_ConnectionString($connectionString)
            $parsed["Password"] | Should -Be $password
        }
    }
}
