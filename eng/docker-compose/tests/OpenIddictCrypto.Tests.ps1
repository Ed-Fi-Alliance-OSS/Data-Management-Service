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
    BeforeAll {
        # A global PowerShell function stands in for docker rather than Pester's Mock, because these
        # tests must observe what reaches the subprocess's STDIN, not just its arguments. Pester wraps
        # a mock's scriptblock in a way that leaves $input and $MyInvocation.ExpectingInput empty, so
        # a mock cannot tell a piped invocation from an unpiped one - which is exactly the distinction
        # under test here. A plain function sees both. It is also pure PowerShell, so it behaves the
        # same on the ubuntu-latest CI runner as it does locally.
        #
        # $Respond receives the flattened argument list and returns @{ Exit; Output }.
        function script:Invoke-WithDockerStdinShim {
            param(
                [Parameter(Mandatory)] [scriptblock]$Respond,
                [Parameter(Mandatory)] [scriptblock]$Action
            )

            $calls = [System.Collections.Generic.List[object]]::new()
            $respond = $Respond
            $caught = $null
            Set-Item -Path Function:\global:docker -Value {
                $piped = @($input)
                $flat = @($args | ForEach-Object { $_ })
                $calls.Add([PSCustomObject]@{
                    Args           = $flat
                    ArgLine        = ($flat -join " ")
                    ExpectingInput = $MyInvocation.ExpectingInput
                    Stdin          = (($piped | ForEach-Object { [string]$_ }) -join "`n")
                })
                $answer = & $respond $flat
                $global:LASTEXITCODE = [int]$answer.Exit
                return $answer.Output
            }.GetNewClosure()

            try { & $Action }
            catch { $caught = $_ }
            finally { Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue }

            return [PSCustomObject]@{
                Calls        = @($calls)
                Create       = @($calls | Where-Object { $_.ArgLine -notlike "*-tA*" })[0]
                Postcondition = @($calls | Where-Object { $_.ArgLine -like "*-tA*" })[0]
                Error        = $caught
                ErrorMessage = if ($null -ne $caught) { $caught.Exception.Message } else { $null }
            }
        }

        # Captured live against PostgreSQL 16.8: with -v VERBOSITY=sqlstate psql prints only the bare
        # SQLSTATE, prefixed by its own "psql:<stdin>:<line>:" location, and exits 3 on error.
        $script:PgDuplicateOutput = 'psql:<stdin>:1: ERROR:  42P04'
        $script:PgRaceOutput = 'psql:<stdin>:2: ERROR:  23505'
        $script:PgPermissionOutput = 'psql:<stdin>:1: ERROR:  42501'

        # Succeeds both the create and the existence check.
        $script:PgHappyResponder = {
            param($flat)
            if (($flat -join " ") -like "*-tA*") { return @{ Exit = 0; Output = "1" } }
            return @{ Exit = 0; Output = "CREATE DATABASE" }
        }
    }

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
    }

    It "creates the database and confirms it, on the ordinary first-run path" {
        $run = Invoke-WithDockerStdinShim -Respond $script:PgHappyResponder -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        }

        $run.Error | Should -BeNullOrEmpty
        $run.Calls | Should -HaveCount 2 -Because "the guarded create and its postcondition are separate invocations"
    }

    It "actually pipes each script into the subprocess's stdin, not merely naming -i" {
        # The load-bearing assertion for the transport: psql is invoked with `-f -`, so it reads its
        # program from stdin. Deleting the pipe would leave the argument list identical and psql would
        # silently execute nothing, so asserting on arguments alone cannot detect that. These check
        # that the process was genuinely invoked with pipeline input and that the exact script text
        # arrived on it.
        $run = Invoke-WithDockerStdinShim -Respond $script:PgHappyResponder -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        }

        $run.Create.ExpectingInput | Should -BeTrue -Because "without a pipe psql reads an empty program from -f -"
        $run.Create.Stdin | Should -BeExactly (New-PostgresCreateDatabaseScript)

        $run.Postcondition.ExpectingInput | Should -BeTrue
        $run.Postcondition.Stdin | Should -BeLike "*FROM pg_database WHERE datname = :'dbName'*"

        foreach ($call in $run.Calls) {
            $call.Args[0] | Should -Be "exec"
            $call.Args | Should -Contain "-i" -Because "docker exec without -i does not attach stdin"
            $call.Args | Should -Contain "-f"
            $call.Stdin | Should -Not -BeNullOrEmpty
        }
    }

    It "passes the database name as a psql variable and never inside the script text" {
        $run = Invoke-WithDockerStdinShim -Respond $script:PgHappyResponder -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "weird-name" -User "postgres"
        }

        $run.Create.ArgLine | Should -BeLike "*-v dbName=weird-name*"
        $run.Create.Stdin | Should -Not -BeLike "*weird-name*" -Because "the name must reach psql as a variable, so nothing in the SQL text needs escaping"
    }

    It "sets the psql options the guard depends on, against the maintenance database" {
        $run = Invoke-WithDockerStdinShim -Respond $script:PgHappyResponder -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        }

        foreach ($call in $run.Calls) {
            $call.ArgLine | Should -BeLike "*ON_ERROR_STOP=1*" -Because "without it psql exits 0 even when the gexec-generated CREATE DATABASE fails"
            $call.ArgLine | Should -BeLike "*VERBOSITY=sqlstate*" -Because "the duplicate-error predicate matches on the bare SQLSTATE"
            $call.ArgLine | Should -BeLike "*-d postgres*" -Because "neither call can connect to the database being created"
        }
    }

    It "tolerates the benign duplicate-database race (42P04) when the postcondition confirms the database" {
        $run = Invoke-WithDockerStdinShim -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        } -Respond {
            param($flat)
            if (($flat -join " ") -like "*-tA*") { return @{ Exit = 0; Output = "1" } }
            return @{ Exit = 3; Output = $script:PgDuplicateOutput }
        }

        $run.Error | Should -BeNullOrEmpty
    }

    It "tolerates the narrower internal catalog-index race (23505) as well" {
        $run = Invoke-WithDockerStdinShim -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        } -Respond {
            param($flat)
            if (($flat -join " ") -like "*-tA*") { return @{ Exit = 0; Output = "1" } }
            return @{ Exit = 3; Output = $script:PgRaceOutput }
        }

        $run.Error | Should -BeNullOrEmpty
    }

    It "does not tolerate a non-duplicate failure such as insufficient privilege (42501)" {
        $run = Invoke-WithDockerStdinShim -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "nocreate"
        } -Respond {
            # Every invocation fails the same way, so this responder ignores which one it is.
            return @{ Exit = 3; Output = $script:PgPermissionOutput }
        }

        $run.ErrorMessage | Should -BeLike "*failed to create database*"
        $run.Calls | Should -HaveCount 1 -Because "a hard create failure must not go on to run the postcondition"
    }

    It "throws when the postcondition cannot confirm the database exists after a tolerated race" {
        $run = Invoke-WithDockerStdinShim -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        } -Respond {
            param($flat)
            if (($flat -join " ") -like "*-tA*") { return @{ Exit = 0; Output = "" } }
            return @{ Exit = 3; Output = $script:PgDuplicateOutput }
        }

        $run.ErrorMessage | Should -BeLike "*does not exist*"
    }

    It "throws when the postcondition query itself fails" {
        $run = Invoke-WithDockerStdinShim -Action {
            Invoke-PostgresGuardedDatabaseCreate -DatabaseName "edfi_configurationservice" -User "postgres"
        } -Respond {
            param($flat)
            if (($flat -join " ") -like "*-tA*") { return @{ Exit = 2; Output = "connection refused" } }
            return @{ Exit = 0; Output = "CREATE DATABASE" }
        }

        $run.ErrorMessage | Should -BeLike "*failed to confirm*"
    }
}
