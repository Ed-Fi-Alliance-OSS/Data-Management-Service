# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot "../OpenIddict-Crypto.psm1") -Force
    $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
    Push-Location $script:DockerComposePath
    try {
        . ./setup-openiddict.ps1 -EnvironmentFile ""
    }
    finally {
        Pop-Location
    }
}

Describe "OpenIddict SQL Server signing-key insert command" {
    It "keeps the identity encryption key out of generated SQL text" {
        $encryptionKey = "Identity'Encryption;Key"

        $command = New-OpenIddictKeyInsertCommand -EncryptionKey $encryptionKey -DbType "MSSQL"

        $command.Sql | Should -Match "ENCRYPTBYPASSPHRASE\(@EncryptionKey, @PrivateKey\)"
        $command.Sql | Should -Not -Match [regex]::Escape($encryptionKey)
        $command.Parameters.EncryptionKey | Should -Be $encryptionKey
        $command.Parameters.PrivateKey | Should -Not -BeNullOrEmpty
        $command.Parameters.PublicKey.GetType() | Should -Be ([byte[]])
    }

    It "keeps PostgreSQL SQL generation backward compatible" {
        $sql = New-OpenIddictKeyInsertSql -EncryptionKey "postgres-secret" -DbType "Postgresql"

        $sql | Should -Match "pgp_sym_encrypt"
        $sql | Should -Match "postgres-secret"
    }

    It "rejects unsafe SQL Server string generation" {
        $encryptionKey = "SqlServer'Encryption;Key"

        { New-OpenIddictKeyInsertSql -EncryptionKey $encryptionKey -DbType "MSSQL" } |
            Should -Throw "*New-OpenIddictKeyInsertCommand*"
    }
}

Describe "OpenIddict SQL Server bootstrap script" {
    It "adds all SQL Server OpenIddict key parameters through ADO.NET command parameters" {
        $command = [System.Data.SqlClient.SqlCommand]::new()
        $parameters = [PSCustomObject]@{
            KeyId = "key-id"
            PublicKey = [byte[]](1, 2, 3)
            PrivateKey = "Private'Key"
            EncryptionKey = "Encryption'Key"
        }

        Add-MssqlOpenIddictKeyParameters -Command $command -Parameters $parameters

        $command.Parameters.Count | Should -Be 4
        $command.Parameters["@KeyId"].SqlDbType | Should -Be ([System.Data.SqlDbType]::NVarChar)
        $command.Parameters["@KeyId"].Size | Should -Be 64
        $command.Parameters["@KeyId"].Value | Should -Be $parameters.KeyId
        $command.Parameters["@PublicKey"].SqlDbType | Should -Be ([System.Data.SqlDbType]::VarBinary)
        $command.Parameters["@PublicKey"].Size | Should -Be -1
        $command.Parameters["@PublicKey"].Value.GetType() | Should -Be ([byte[]])
        [Convert]::ToBase64String($command.Parameters["@PublicKey"].Value) |
            Should -Be ([Convert]::ToBase64String($parameters.PublicKey))
        $command.Parameters["@PrivateKey"].SqlDbType | Should -Be ([System.Data.SqlDbType]::VarChar)
        $command.Parameters["@PrivateKey"].Size | Should -Be -1
        $command.Parameters["@PrivateKey"].Value | Should -Be $parameters.PrivateKey
        $command.Parameters["@EncryptionKey"].SqlDbType | Should -Be ([System.Data.SqlDbType]::NVarChar)
        $command.Parameters["@EncryptionKey"].Size | Should -Be -1
        $command.Parameters["@EncryptionKey"].Value | Should -Be $parameters.EncryptionKey
    }

    It "derives the default database port from the database type when DbPort is omitted" {
        Resolve-DbPort -DbPort "" -DbType "MSSQL" | Should -Be "ENV:MSSQL_PORT"
        Resolve-DbPort -DbPort "" -DbType "Postgresql" | Should -Be "ENV:POSTGRES_PORT"
        Resolve-DbPort -DbPort "15433" -DbType "MSSQL" | Should -Be "15433"
    }

    It "derives the default database host from the database type when DbHost is omitted" {
        Resolve-DbHost -DbHost "" -DbType "MSSQL" | Should -Be "127.0.0.1"
        Resolve-DbHost -DbHost "" -DbType "Postgresql" | Should -Be "localhost"
        Resolve-DbHost -DbHost "sql-host" -DbType "MSSQL" | Should -Be "sql-host"
    }

    It "builds the guarded create-if-absent statement, escaping both T-SQL delimiter positions" {
        $statement = New-MssqlCreateDatabaseStatement -DatabaseName "a'b]c"
        $statement | Should -Be "IF DB_ID(N'a''b]c') IS NULL CREATE DATABASE [a'b]]c];"
    }

    It "keeps the database name out of the PostgreSQL create script entirely" {
        # The MSSQL statement has to embed and escape the name; the PostgreSQL script must not embed
        # it at all. The name travels as a psql variable (-v dbName=...) and the script refers to it
        # as :'dbName', so psql quotes the comparison and format('%I') quotes the identifier - no
        # string building on our side, hence nothing to escape wrongly.
        $script = New-PostgresCreateDatabaseScript

        $script | Should -Match "format\('CREATE DATABASE %I', :'dbName'\)"
        $script | Should -Match "WHERE NOT EXISTS \(SELECT FROM pg_database WHERE datname = :'dbName'\)"
        $script | Should -Match '\\gexec$' -Because "CREATE DATABASE cannot run inside a transaction or plpgsql block, so the generated statement is executed by psql's own \gexec"
    }
}

Describe "OpenIddict MSSQL guarded database creation (DMS-1270 Phase 1b)" {
    BeforeEach {
        Push-Location $script:DockerComposePath
        try {
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "MSSQL" -DbName "edfi_configurationservice" -DbUser "sa" -DbPassword "abcdefgh1!" -MssqlContainerName "dms-mssql-test" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }

        # The OpenIddictKey insert path uses a real ADO.NET SqlConnection, not docker exec - mock it
        # to a no-op so these tests exercise only the guarded-create/postcondition logic under test,
        # without attempting a real SQL Server connection.
        Mock Invoke-MssqlParameterizedQuery { }
    }

    # Real sqlcmd emits result rows as separate output objects, and a bare SELECT is followed by a
    # blank line and a "(1 rows affected)" row-count message: -h -1 suppresses column headers, not
    # row counts. These mocks therefore return that multi-line shape rather than a bare scalar, so a
    # postcondition that compares the whole captured output against "1" fails here as it would in
    # production. The code both sends SET NOCOUNT ON (asserted below) and reads the first non-blank
    # line, so it is correct even against this un-suppressed shape.
    BeforeAll {
        $script:MssqlExistsOutput = { param([string]$Value) return @($Value, "", "(1 rows affected)") }
        $script:Msg1801Output = "Msg 1801, Level 16, State 3, Server dms-mssql-test, Line 1`nDatabase 'edfi_configurationservice' already exists. Choose a different database name."
    }

    It "tolerates the benign concurrent-creation race (SQL Server error 1801) and does not swallow the postcondition check" {
        # Round: DMS-1270 Phase 1b. The IF DB_ID(...) IS NULL CREATE DATABASE guard is a
        # check-then-act statement, not truly atomic: this simulates the losing side of a genuine
        # concurrent race (sqlcmd exits nonzero reporting "Msg 1801,") and confirms
        # Invoke-InitDbScripts does not throw when the follow-up postcondition query proves the
        # database exists.
        Mock docker {
            $sql = $args[-1]
            if ($sql -match "CREATE DATABASE") {
                $global:LASTEXITCODE = 1
                return $script:Msg1801Output
            }
            if ($sql -match "SELECT CASE WHEN DB_ID") {
                $global:LASTEXITCODE = 0
                return (& $script:MssqlExistsOutput "1")
            }
            $global:LASTEXITCODE = 0
            return ""
        }

        { Invoke-InitDbScripts } | Should -Not -Throw
    }

    It "suppresses row-count messages on the postcondition query so its result is parseable" {
        # Without SET NOCOUNT ON the query's output carries a trailing "(1 rows affected)" line and
        # no comparison against "1" can ever succeed, failing every MSSQL initialization even though
        # the database exists. Pinned here so the prefix cannot be dropped again.
        $script:capturedPostconditionSql = $null
        Mock docker {
            $sql = $args[-1]
            if ($sql -match "SELECT CASE WHEN DB_ID") {
                $script:capturedPostconditionSql = $sql
                $global:LASTEXITCODE = 0
                return (& $script:MssqlExistsOutput "1")
            }
            $global:LASTEXITCODE = 0
            return ""
        }

        Invoke-InitDbScripts

        $script:capturedPostconditionSql | Should -Not -BeNullOrEmpty
        $script:capturedPostconditionSql | Should -BeLike "SET NOCOUNT ON;*"
    }

    It "does not tolerate a non-1801 sqlcmd failure even with the tolerance flag set" {
        Mock docker {
            $sql = $args[-1]
            if ($sql -match "CREATE DATABASE") {
                $global:LASTEXITCODE = 1
                return "Msg 4060, Level 11, State 1, Server dms-mssql-test, Line 1`nCannot open database requested by the login."
            }
            $global:LASTEXITCODE = 0
            return ""
        }

        { Invoke-InitDbScripts } | Should -Throw "*sqlcmd failed*"
    }

    It "throws when the postcondition cannot confirm the database exists after a tolerated 1801" {
        Mock docker {
            $sql = $args[-1]
            if ($sql -match "CREATE DATABASE") {
                $global:LASTEXITCODE = 1
                return $script:Msg1801Output
            }
            if ($sql -match "SELECT CASE WHEN DB_ID") {
                $global:LASTEXITCODE = 0
                return (& $script:MssqlExistsOutput "0")
            }
            $global:LASTEXITCODE = 0
            return ""
        }

        { Invoke-InitDbScripts } | Should -Throw "*does not exist*"
    }
}

Describe "OpenIddict PostgreSQL guarded database creation (DMS-1270 Phase 2)" {
    BeforeEach {
        Push-Location $script:DockerComposePath
        try {
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "Postgresql" `
                -ConnectionString "Host=localhost;Port=5432;Database=edfi_configurationservice;Username=postgres;" `
                -PostgresContainerName "dms-postgresql-test" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }

        # Captured live against PostgreSQL 16.8: with -v VERBOSITY=sqlstate psql prints only the bare
        # SQLSTATE, prefixed by its own "psql:<stdin>:<line>:" location, and exits 3 on error.
        $script:PgDuplicateOutput = 'psql:<stdin>:1: ERROR:  42P04'
        $script:PgRaceOutput = 'psql:<stdin>:2: ERROR:  23505'
        $script:PgPermissionOutput = 'psql:<stdin>:1: ERROR:  42501'
    }

    It "creates the database and confirms it, on the ordinary first-run path" {
        Mock docker {
            $global:LASTEXITCODE = 0
            if ($args -contains "-tA") { return "1" }
            return "CREATE DATABASE"
        }

        { Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres" } |
            Should -Not -Throw
    }

    It "pipes the script to psql through docker exec -i so it reaches stdin at all" {
        # Without -i the piped script never reaches psql's stdin: psql reads an empty program and
        # silently does nothing, so the create would appear to succeed while doing nothing.
        $script:capturedArgs = $null
        Mock docker {
            if ($null -eq $script:capturedArgs) { $script:capturedArgs = @($args) }
            $global:LASTEXITCODE = 0
            if ($args -contains "-tA") { return "1" }
            return "CREATE DATABASE"
        }

        Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"

        $script:capturedArgs[0] | Should -Be "exec"
        $script:capturedArgs | Should -Contain "-i"
        $script:capturedArgs | Should -Contain "-f"
        ($script:capturedArgs -join " ") | Should -BeLike "*-v dbName=edfi_configurationservice*"
        ($script:capturedArgs -join " ") | Should -BeLike "*ON_ERROR_STOP=1*" -Because "without it psql exits 0 even when the gexec-generated CREATE DATABASE fails"
        ($script:capturedArgs -join " ") | Should -BeLike "*VERBOSITY=sqlstate*" -Because "the duplicate-error predicate matches on the bare SQLSTATE"
        ($script:capturedArgs -join " ") | Should -BeLike "*-d postgres*" -Because "the create cannot connect to the database it is about to create"
    }

    It "tolerates the benign duplicate-database race (42P04) when the postcondition confirms the database" {
        Mock docker {
            if ($args -contains "-tA") {
                $global:LASTEXITCODE = 0
                return "1"
            }
            $global:LASTEXITCODE = 3
            return $script:PgDuplicateOutput
        }

        { Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres" } |
            Should -Not -Throw
    }

    It "tolerates the narrower internal catalog-index race (23505) as well" {
        Mock docker {
            if ($args -contains "-tA") {
                $global:LASTEXITCODE = 0
                return "1"
            }
            $global:LASTEXITCODE = 3
            return $script:PgRaceOutput
        }

        { Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres" } |
            Should -Not -Throw
    }

    It "does not tolerate a non-duplicate failure such as insufficient privilege (42501)" {
        Mock docker {
            $global:LASTEXITCODE = 3
            return $script:PgPermissionOutput
        }

        { Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "nocreate" } |
            Should -Throw "*failed to create database*"
    }

    It "throws when the postcondition cannot confirm the database exists after a tolerated race" {
        Mock docker {
            if ($args -contains "-tA") {
                $global:LASTEXITCODE = 0
                return ""
            }
            $global:LASTEXITCODE = 3
            return $script:PgDuplicateOutput
        }

        { Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres" } |
            Should -Throw "*does not exist*"
    }

    It "throws when the postcondition query itself fails" {
        Mock docker {
            if ($args -contains "-tA") {
                $global:LASTEXITCODE = 2
                return "connection refused"
            }
            $global:LASTEXITCODE = 0
            return "CREATE DATABASE"
        }

        { Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres" } |
            Should -Throw "*failed to confirm*"
    }
}
