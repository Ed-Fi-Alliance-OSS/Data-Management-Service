# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'The extracted Resolve-EnvValue reads $envValues from the caller scope via dynamic scoping; the analyzer cannot see that use.')]
param()

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

    Context "provision environment map preserves raw Compose values" {
        It "keeps a single-quoted password literal through the shared resolver" {
            # Get-EnvironmentValueMap must store RAW env-file values: the resolver's single-quote
            # literal rule keys off the raw leading quote, so a pre-stripped map would let a
            # single-quoted password be interpolated ('$$' collapsed to '$') while Docker Compose
            # gives the container the literal value - the provision/reset phase would then use a
            # password the running SQL Server never received.
            $envFile = Join-Path ([System.IO.Path]::GetTempPath()) "dms1284-raw-map-$([Guid]::NewGuid().ToString('N')).env"
            'MSSQL_SA_PASSWORD=''Pa$$w0rd!''' | Set-Content -LiteralPath $envFile -Encoding utf8
            try {
                $map = Get-EnvironmentValueMap $envFile

                Get-ComposeResolvedEnvValue -EnvironmentValues $map -Name "MSSQL_SA_PASSWORD" |
                    Should -Be 'Pa$$w0rd!'
            }
            finally {
                Remove-Item -LiteralPath $envFile -ErrorAction SilentlyContinue
            }
        }
    }

    Context "setup-openiddict Resolve-EnvValue (ENV: indirection)" {
        BeforeAll {
            # Extract just the Resolve-EnvValue function from setup-openiddict.ps1 via the AST so the
            # ENV: indirection used for identity-store database values can be exercised without the
            # script's top-level Docker/SQL orchestration. The function reads $envValues from the
            # caller's scope (dynamic scoping), so each test defines it locally.
            $parseErrors = $null
            $tokens = $null
            $setupScript = Join-Path $script:dockerComposeRoot "setup-openiddict.ps1"
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($setupScript, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Resolve-EnvValue" }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Resolve-EnvValue was not found in setup-openiddict.ps1." }
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }

        It "resolves an ENV: value with ambient process precedence over the env file (Compose precedence)" {
            # The container received the ambient value through Compose interpolation, so the
            # identity-store setup must connect with the same value or authentication fails on any
            # ambient credential override.
            $priorExists = Test-Path "Env:DMS1284_OPENIDDICT_PROBE"
            $priorValue = [System.Environment]::GetEnvironmentVariable("DMS1284_OPENIDDICT_PROBE")
            try {
                [System.Environment]::SetEnvironmentVariable("DMS1284_OPENIDDICT_PROBE", "ambient-value")
                $envValues = @{ DMS1284_OPENIDDICT_PROBE = "file-value" }

                Resolve-EnvValue "ENV:DMS1284_OPENIDDICT_PROBE" | Should -Be "ambient-value"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("DMS1284_OPENIDDICT_PROBE", $priorValue) }
                else { Remove-Item Env:DMS1284_OPENIDDICT_PROBE -ErrorAction SilentlyContinue }
            }
        }

        It "resolves an ENV: value from the env file when no ambient override exists" {
            Remove-Item Env:DMS1284_OPENIDDICT_PROBE -ErrorAction SilentlyContinue
            $envValues = @{ DMS1284_OPENIDDICT_PROBE = "file-value" }

            Resolve-EnvValue "ENV:DMS1284_OPENIDDICT_PROBE" | Should -Be "file-value"
        }

        It "returns a non-ENV: value verbatim" {
            $envValues = @{}
            Resolve-EnvValue "literal-value" | Should -Be "literal-value"
        }

        It "throws by key name, without echoing any value, when an ENV: value is configured nowhere" {
            Remove-Item Env:DMS1284_OPENIDDICT_MISSING -ErrorAction SilentlyContinue
            $envValues = @{}

            { Resolve-EnvValue "ENV:DMS1284_OPENIDDICT_MISSING" } | Should -Throw "*DMS1284_OPENIDDICT_MISSING*"
        }
    }

    Context "setup-openiddict New-MssqlCreateDatabaseStatement" {
        BeforeAll {
            # Same AST extraction as above: the statement builder is a pure function, so it is exercised
            # without the script's Docker/SQL orchestration.
            $parseErrors = $null
            $tokens = $null
            $setupScript = Join-Path $script:dockerComposeRoot "setup-openiddict.ps1"
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($setupScript, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "New-MssqlCreateDatabaseStatement" }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "New-MssqlCreateDatabaseStatement was not found in setup-openiddict.ps1." }
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }

        It "creates the configured database when the name is an ordinary identifier" {
            New-MssqlCreateDatabaseStatement -DatabaseName "edfi_configurationservice" |
                Should -Be "IF DB_ID(N'edfi_configurationservice') IS NULL CREATE DATABASE [edfi_configurationservice];"
        }

        It "doubles a single quote so the name cannot terminate the N'...' literal" {
            # The name is configuration-supplied (env file, or an ambient value that wins Compose
            # precedence), so an unescaped quote would end the literal and leave the remainder to run as
            # statement text against master.
            New-MssqlCreateDatabaseStatement -DatabaseName "db'; DROP DATABASE [edfi_datamanagementservice]; --" |
                Should -Be "IF DB_ID(N'db''; DROP DATABASE [edfi_datamanagementservice]; --') IS NULL CREATE DATABASE [db'; DROP DATABASE [edfi_datamanagementservice]]; --];"
        }

        It "doubles a closing bracket so the name cannot terminate the [...] identifier" {
            New-MssqlCreateDatabaseStatement -DatabaseName "db]; DROP DATABASE [edfi_datamanagementservice" |
                Should -Be "IF DB_ID(N'db]; DROP DATABASE [edfi_datamanagementservice') IS NULL CREATE DATABASE [db]]; DROP DATABASE [edfi_datamanagementservice];"
        }

        It "keeps a legal name that a bare identifier could not carry" {
            New-MssqlCreateDatabaseStatement -DatabaseName "edfi-config service" |
                Should -Be "IF DB_ID(N'edfi-config service') IS NULL CREATE DATABASE [edfi-config service];"
        }
    }

    Context "phase wiring for the Compose-equivalent resolver" {
        # Wiring guards for the two seams that cannot be invoked without a Docker stack: the
        # published startup's inline readiness/data-store block and the standard E2E setup wrapper's
        # target-database read. The resolver behavior itself is covered by the invoked tests above.
        BeforeAll {
            $script:startPublishedSource = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-published-dms.ps1") -Raw
            $script:setupLocalDmsSource = Get-Content -LiteralPath ([System.IO.Path]::GetFullPath((Join-Path $script:dockerComposeRoot "../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))) -Raw
        }

        It "start-published-dms.ps1 imports the shared resolver and reads no protected value from raw env-file properties" {
            $script:startPublishedSource | Should -Match 'Import-Module \(Join-Path \$PSScriptRoot "database-safety\.psm1"\)'
            foreach ($rawRead in @(
                    '\$envValues\.MSSQL_SA_PASSWORD',
                    '\$envValues\.MSSQL_DB_NAME',
                    '\$envValues\.POSTGRES_DB_NAME',
                    '\$envValues\.POSTGRES_USER',
                    '\$envValues\.POSTGRES_PASSWORD',
                    '\$envValues\.CONFIG_SERVICE_TENANT',
                    '\$envValues\.DMS_CONFIG_MULTI_TENANCY'
                )) {
                $script:startPublishedSource | Should -Not -Match $rawRead
            }
        }

        It "setup-local-dms.ps1 resolves E2E_DATABASE_NAME through the shared resolver" {
            $script:setupLocalDmsSource | Should -Match 'Import-Module \./database-safety\.psm1'
            $script:setupLocalDmsSource | Should -Match 'Get-ComposeResolvedEnvValue -EnvironmentValues \$envValues -Name "E2E_DATABASE_NAME"'
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
