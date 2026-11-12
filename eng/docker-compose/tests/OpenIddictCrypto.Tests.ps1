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

    It "escapes a single quote in the encryption key instead of emitting invalid PostgreSQL" {
        # A configured CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY may legitimately contain a single quote.
        # Pasted between bare quotes it closes the literal early, psql rejects the whole INSERT, and
        # the mandatory signing-key replacement fails before CMS can mint a token.
        $encryptionKey = "Identity'Encryption;Key"

        $sql = New-OpenIddictKeyInsertSql -EncryptionKey $encryptionKey

        $sql | Should -Match ([regex]::Escape("'Identity''Encryption;Key'"))
        $sql | Should -Not -Match ([regex]::Escape($encryptionKey)) -Because "the unescaped key would end its literal at the quote"
        ($sql -split "`n" | Where-Object { $_ -like "VALUES*" }) |
            Should -Match "pgp_sym_encrypt\('[A-Za-z0-9+/=]+', 'Identity''Encryption;Key'\), TRUE\);$"
    }

    It "escapes the encryption key in the standalone generator the restore recipe calls" {
        # Generate-OpenIddictKey-Insert.ps1 builds the same statement independently, so it is pinned
        # separately: the recipe pipes its output straight into psql and cannot escape anything.
        $sql = & (Join-Path $script:DockerComposePath "Generate-OpenIddictKey-Insert.ps1") `
            -KeyId "Key'Id" -EncryptionKey "Identity'Encryption;Key"

        $sql | Should -Match ([regex]::Escape("'Key''Id'"))
        $sql | Should -Match ([regex]::Escape("'Identity''Encryption;Key'"))
    }

    It "quotes values at the generation boundary so callers never escape anything" {
        ConvertTo-PostgresSqlLiteral -Value "a'b" | Should -BeExactly "'a''b'"
        ConvertTo-PostgresSqlLiteral -Value "" | Should -BeExactly "''"
        ConvertTo-PostgresSqlLiteral -Value "plain" | Should -BeExactly "'plain'"
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

    It "quotes SQL Server string literals without letting configured values terminate them" {
        ConvertTo-MssqlSqlLiteral -Value "a'b" | Should -BeExactly "N'a''b'"
        ConvertTo-MssqlSqlLiteral -Value "" | Should -BeExactly "N''"
        ConvertTo-MssqlSqlLiteral -Value "plain" | Should -BeExactly "N'plain'"
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

        # The OpenIddictKey initialization path uses a real ADO.NET SqlConnection, not docker exec -
        # mock it to report a created key so these tests exercise only the guarded-create/postcondition
        # logic under test, without attempting a real SQL Server connection.
        Mock Invoke-MssqlParameterizedQuery { "created" }
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

Describe "OpenIddict PostgreSQL client provisioning from configured values" {
    BeforeAll {
        # These tests drive the whole -InsertData sequence, which is what the Northridge restore
        # recipe's step 10 invokes to recreate the client DMS authenticates to CMS with. Its client
        # id, scope and role names are all supported compose overrides, so every one of them can
        # legitimately contain a single quote, and each reaches PostgreSQL as a string literal.
        #
        # A global PowerShell function stands in for docker rather than Pester's Mock, for the same
        # reason as the guarded-database-creation block above and one more: the script under test is
        # dot-sourced with -InsertData, so the stand-in has to already exist when it loads.
        function script:Invoke-InsertDataCapture {
            [OutputType([string[]])]
            param([hashtable]$Parameter = @{})

            $captured = [System.Collections.Generic.List[string]]::new()
            Set-Item -Path Function:\global:docker -Value {
                $flat = @($args | ForEach-Object { $_ })
                # Invoke-DbQuery passes the statement as the final argument of `psql ... -c <sql>`.
                $captured.Add([string]$flat[-1])
                $global:LASTEXITCODE = 0
                # psql's default aligned output for the RETURNING/SELECT statements: a header, a rule,
                # the indented row, then the row count. Get-ScalarResult reads index 2 and callers
                # trim it, so the shape matters as much as the value.
                return @(
                    '                  Id                  ',
                    '--------------------------------------',
                    ('  00000000-0000-0000-0000-{0:d12}  ' -f $captured.Count),
                    '(1 row)'
                )
            }.GetNewClosure()

            # The hostile secret is the default; a case may replace it through -Parameter, which would
            # otherwise be a second binding of the same parameter, or name the environment variable that
            # holds the secret instead, in which case the literal parameter is not bound at all.
            $bound = @{}
            if (-not ($Parameter.ContainsKey("NewClientSecret") -or $Parameter.ContainsKey("NewClientSecretEnvironmentVariable"))) {
                $bound.NewClientSecret = $script:HostileSecret
            }
            foreach ($key in $Parameter.Keys) { $bound[$key] = $Parameter[$key] }

            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -InsertData -EnvironmentFile "" -DbType "Postgresql" `
                    -ConnectionString "Host=localhost;Port=5432;Database=edfi_configurationservice;Username=postgres;" `
                    -PostgresContainerName "dms-postgresql-test" -HashIterations "1000" `
                    @bound | Out-Null
            }
            finally {
                Pop-Location
                Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
            }

            return @($captured)
        }

        # Strips every well-formed PostgreSQL string literal: '...', with a doubled '' standing for an
        # embedded quote. Whatever single quote is left behind is one that ended its literal early --
        # exactly the failure being guarded against, where psql rejects the statement and step 10
        # stops with the DMS-to-CMS client deleted and not recreated. Asserting on the remainder
        # rather than on one expected substring is what makes these tests cover the whole path: a
        # value pasted between bare quotes anywhere in any statement fails them.
        function script:Remove-PostgresStringLiteral {
            [OutputType([string])]
            param([string]$Sql)

            return [regex]::Replace($Sql, "'(?:[^']|'')*'", '')
        }

        # Satisfies Test-ClientSecretComplexity and the default 32/128 bounds while carrying a quote of
        # its own, so the secret is never the reason a case passes.
        $script:HostileSecret = "Str'ong-Secret-1234567890-Abcdef!"

        # Selects the statements of interest out of a captured sequence.
        $script:StatementFor = {
            param([string[]]$Sql, [string]$Pattern)
            return @($Sql | Where-Object { $_ -match $Pattern })
        }

        # Reads the ClientSecret hash out of a captured OpenIddictApplication INSERT -- the third literal
        # of its VALUES -- and checks it against a plain text the way ASP.NET Identity would:
        # New-AspNetPasswordHash writes a version byte, the salt length, the salt and a PBKDF2-SHA256
        # subkey, so the subkey is re-derived from the candidate with the stored salt and compared.
        function script:Test-StoredClientSecretHash {
            [OutputType([bool])]
            param([string]$InsertSql, [string]$Secret, [int]$Iterations = 1000)

            $hash = [regex]::Match($InsertSql, "(?s)VALUES \('[^']*', '(?:[^']|'')*', '(?<hash>[^']*)'").Groups["hash"].Value
            if (-not $hash) { throw "no ClientSecret literal in: $InsertSql" }
            $bytes = [Convert]::FromBase64String($hash)
            $saltLength = [BitConverter]::ToInt32($bytes, 1)
            $salt = [byte[]]$bytes[5..(4 + $saltLength)]
            $stored = [byte[]]$bytes[(5 + $saltLength)..($bytes.Length - 1)]
            $derived = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
                [System.Text.Encoding]::UTF8.GetBytes($Secret), $salt, $Iterations,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256, $stored.Length)
            return [System.Linq.Enumerable]::SequenceEqual([byte[]]$derived, $stored)
        }
    }

    It "escapes a configured client id containing a single quote in both statements that carry it" {
        # DMS_CONFIGURATION_SERVICE_CLIENT_ID is a supported override. Pasted between bare quotes it closes its
        # literal, psql rejects the INSERT, and the restore leaves DMS unable to authenticate to CMS.
        $sql = Invoke-InsertDataCapture -Parameter @{ NewClientId = "CMS'ReadOnly" }

        $insert = @(& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictApplication"')
        $insert | Should -HaveCount 1
        $insert[0] | Should -BeLike "*'CMS''ReadOnly'*"

        $select = @(& $script:StatementFor $sql 'SELECT "Id" FROM "dmscs"\."OpenIddictApplication"')
        $select | Should -HaveCount 1
        $select[0] | Should -BeLike "*'CMS''ReadOnly'*"

        foreach ($statement in @($insert) + @($select)) {
            Remove-PostgresStringLiteral $statement |
                Should -Not -Match "'" -Because "an unpaired quote means the client id ended its literal"
        }
    }

    It "escapes a configured scope containing a single quote everywhere it lands" {
        # DMS_CONFIGURATION_SERVICE_CLIENT_SCOPE is a supported override as well, and it reaches three separate
        # statements: the scope insert, the scope lookup, and the permissions update.
        $sql = Invoke-InsertDataCapture -Parameter @{ ClientScopeName = "edfi_admin_api/read'only" }

        $scope = @(& $script:StatementFor $sql '"dmscs"\."OpenIddictScope"')
        $scope | Should -HaveCount 2 -Because "the scope is inserted and then read back"

        $permissions = @(& $script:StatementFor $sql 'SET "Permissions"')
        $permissions | Should -HaveCount 1

        foreach ($statement in @($scope) + @($permissions)) {
            $statement | Should -BeLike "*edfi_admin_api/read''only*"
            Remove-PostgresStringLiteral $statement | Should -Not -Match "'"
        }
    }

    It "escapes configured role names containing a single quote" {
        # IdentitySettings:ConfigServiceRole and IdentitySettings:ClientRole are supported overrides
        # too, and the restore recipe reads both from the running Configuration Service and passes
        # them, so a quote in either reaches the role insert and the role lookup.
        $sql = Invoke-InsertDataCapture -Parameter @{
            ConfigServiceRole = "cms'client"
            DmsClientRole     = "dms'client"
        }

        $roles = @(& $script:StatementFor $sql '"dmscs"\."OpenIddictRole"')
        $roles | Should -HaveCount 4 -Because "each of the two roles is inserted and then read back"

        ($roles -join "`n") | Should -BeLike "*cms''client*"
        ($roles -join "`n") | Should -BeLike "*dms''client*"
        foreach ($statement in $roles) {
            Remove-PostgresStringLiteral $statement | Should -Not -Match "'"
        }
    }

    It "escapes the custom claim name and value containing single quotes" {
        $sql = Invoke-InsertDataCapture -Parameter @{
            ClaimName  = "namespace'Prefixes"
            ClaimValue = "http://ed-fi.org/o'brien"
        }

        $claim = @(& $script:StatementFor $sql 'SET "ProtocolMappers"')
        $claim | Should -HaveCount 1
        $claim[0] | Should -BeLike "*'namespace''Prefixes'*"
        $claim[0] | Should -BeLike "*'http://ed-fi.org/o''brien'*"
        Remove-PostgresStringLiteral $claim[0] | Should -Not -Match "'"
    }

    It "stores a scope containing a comma as one permission rather than silently splitting it" {
        # "Permissions" is varchar(100)[]. Built as array text -- '{...}' -- a scope carrying a comma
        # parses as two permissions, and one carrying a brace, a double quote or a backslash is stored
        # altered, in both cases without any error to notice. An ARRAY constructor takes the value as
        # exactly one element whatever it contains. Verified against PostgreSQL 16.8: the array-text
        # form yields two elements for the value below, the constructor form yields one.
        $sql = Invoke-InsertDataCapture -Parameter @{ ClientScopeName = 'edfi_admin_api/full,access' }

        $permissions = @(& $script:StatementFor $sql 'SET "Permissions"')
        $permissions | Should -HaveCount 1
        $permissions[0] | Should -Match 'SET "Permissions" = ARRAY\[''edfi_admin_api/full,access''\]::varchar\[\]'
        $permissions[0] | Should -Not -Match "'\{" -Because "an array-text literal re-parses the value by array syntax"
    }

    It "hashes a literal -NewClientSecret that begins with ENV: as that literal, reading no environment variable" {
        # Every start script passes the secret it resolved from .env as a literal, and the complexity
        # rule admits ':', so a valid secret may begin with "ENV:". Read as an indirection, such a secret
        # either fails by the name of a variable that does not exist or -- were one to exist -- hashes
        # that variable's value in place of the secret, and DMS can no longer authenticate to CMS.
        $literal = "ENV:LiteralSecret1234567890!Abcd"
        $literal.Length | Should -Be 32 -Because "the literal must satisfy the default 32-character minimum on its own"
        [System.Environment]::GetEnvironmentVariable($literal.Substring(4)) | Should -BeNullOrEmpty -Because "nothing but the literal may be what gets hashed"

        $sql = Invoke-InsertDataCapture -Parameter @{ NewClientSecret = $literal }

        $insert = @(& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictApplication"')
        $insert | Should -HaveCount 1
        $insert[0] | Should -Not -Match ([regex]::Escape($literal)) -Because "the secret is stored as a hash"
        Test-StoredClientSecretHash -InsertSql $insert[0] -Secret $literal | Should -BeTrue -Because "the hash must be of the literal, ENV: prefix included"
    }

    It "reads the secret from the environment variable -NewClientSecretEnvironmentVariable names, before validating and hashing it, and fails by that name when it is unset" {
        # What the restore recipe's step 10 uses: setup-openiddict.ps1 is a new process whose argument
        # list is readable by every process on the host, so the secret travels as one variable of that
        # process's environment and this parameter names it. Resolved before the length and complexity
        # checks -- "NR_TEST_CLIENT_SECRET" is 21 characters and would fail the 32-character minimum on
        # its own, so a pass here is the proof of the order.
        $name = "NR_TEST_CLIENT_SECRET"
        try {
            [System.Environment]::SetEnvironmentVariable($name, $script:HostileSecret)
            $sql = Invoke-InsertDataCapture -Parameter @{ NewClientSecretEnvironmentVariable = $name }
        }
        finally {
            Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
        }

        $insert = @(& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictApplication"')
        $insert | Should -HaveCount 1
        ($sql -join "`n") | Should -Not -Match $name -Because "the variable's name must not be what is stored"
        ($sql -join "`n") | Should -Not -Match ([regex]::Escape($script:HostileSecret)) -Because "the secret is stored as a hash"
        Test-StoredClientSecretHash -InsertSql $insert[0] -Secret $script:HostileSecret | Should -BeTrue -Because "the hash must be of the variable's value"

        # Configured nowhere, the parameter fails by the variable's name rather than validating anything.
        { Invoke-InsertDataCapture -Parameter @{ NewClientSecretEnvironmentVariable = $name } } | Should -Throw "*$name*"
    }

    It "refuses -NewClientSecret and -NewClientSecretEnvironmentVariable together, and a blank variable name" {
        # Two sources for one secret is a caller mistake either way, and a blank name would fall through
        # to the default literal -- a secret the caller never chose, hashed without a word.
        { Invoke-InsertDataCapture -Parameter @{ NewClientSecret = $script:HostileSecret; NewClientSecretEnvironmentVariable = "NR_TEST_CLIENT_SECRET" } } |
            Should -Throw "*either -NewClientSecret or -NewClientSecretEnvironmentVariable*"
        { Invoke-InsertDataCapture -Parameter @{ NewClientSecretEnvironmentVariable = "" } } |
            Should -Throw "*-NewClientSecretEnvironmentVariable must name the environment variable*"
    }

    It "leaves no value outside a string literal anywhere in the sequence, with every configured value hostile" {
        # The whole-path assertion. Every configurable string the recipe can reach this script with
        # carries a quote at once, and no statement in the sequence may contain an unpaired one. The
        # per-table counts are asserted so the sweep cannot pass by having generated nothing: this
        # fails if a statement stops being emitted as loudly as if it were mis-quoted.
        $sql = Invoke-InsertDataCapture -Parameter @{
            NewClientId       = "CMS'ReadOnly"
            NewClientName     = "CMS ReadOnly 'Access'"
            ClientScopeName   = "edfi_admin_api/read'only"
            ConfigServiceRole = "cms'client"
            DmsClientRole     = "dms'client"
            ClaimName         = "namespace'Prefixes"
            ClaimValue        = "http://ed-fi.org/o'brien"
        }

        (& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictApplication"') | Should -HaveCount 1
        (& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictRole"') | Should -HaveCount 2
        (& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictScope"') | Should -HaveCount 1
        (& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictClientRole"') | Should -HaveCount 2
        (& $script:StatementFor $sql 'INSERT INTO "dmscs"\."OpenIddictApplicationScope"') | Should -HaveCount 1
        (& $script:StatementFor $sql 'SET "Permissions"') | Should -HaveCount 1
        (& $script:StatementFor $sql 'SET "ProtocolMappers"') | Should -HaveCount 1

        foreach ($statement in $sql) {
            Remove-PostgresStringLiteral $statement |
                Should -Not -Match "'" -Because "every value must stay inside its literal: $statement"
        }
    }

    It "quotes the client id in the standalone secret-update generator as well" {
        # New-ClientSecretUpdateSql builds the same kind of PostgreSQL statement from the same
        # configured client id, so it is pinned here rather than left as the one generator that still
        # pastes a value between bare quotes.
        $sql = New-ClientSecretUpdateSql -ClientId "CMS'ReadOnly" -PlainTextSecret $script:HostileSecret

        $sql | Should -BeLike "*'CMS''ReadOnly'*"
        Remove-PostgresStringLiteral $sql | Should -Not -Match "'"
    }
}

Describe "OpenIddict SQL Server client provisioning from configured values" {
    BeforeAll {
        function script:Invoke-MssqlInsertDataCapture {
            [OutputType([string[]])]
            param([hashtable]$Parameter = @{})

            $captured = [System.Collections.Generic.List[string]]::new()
            Set-Item -Path Function:\global:docker -Value {
                $flat = @($args | ForEach-Object { $_ })
                $queryIndex = [array]::IndexOf($flat, "-Q")
                if ($queryIndex -ge 0 -and $queryIndex -lt ($flat.Count - 1)) {
                    $captured.Add([string]$flat[$queryIndex + 1])
                }
                $global:LASTEXITCODE = 0
                return ('00000000-0000-0000-0000-{0:d12}' -f $captured.Count)
            }.GetNewClosure()

            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -InsertData -EnvironmentFile "" -DbType "MSSQL" `
                    -ConnectionString "" -MssqlContainerName "dms-mssql-test" -DbHost "dms-mssql" `
                    -DbPort "1433" -DbName "edfi_configurationservice" -DbUser "sa" -DbPassword "abcdefgh1!" `
                    -HashIterations "1000" -NewClientSecret $script:HostileSecret @Parameter | Out-Null
            }
            finally {
                Pop-Location
                Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
            }

            return @($captured)
        }

        function script:Remove-MssqlStringLiteral {
            [OutputType([string])]
            param([string]$Sql)

            return [regex]::Replace($Sql, "(?i)N?'(?:[^']|'')*'", '')
        }
    }

    It "leaves no configured value outside a SQL Server string literal anywhere in the sequence" {
        # The start scripts now forward role overrides to SQL Server as well as PostgreSQL. That makes
        # every configured value in the client-registration path part of the same quoting contract.
        $sql = Invoke-MssqlInsertDataCapture -Parameter @{
            NewClientId       = "CMS'ReadOnly"
            NewClientName     = "CMS ReadOnly 'Access'"
            ClientScopeName   = "edfi_admin_api/read'only,full"
            ConfigServiceRole = "cms'client"
            DmsClientRole     = "dms'client"
            ClaimName         = "namespace'Prefixes"
            ClaimValue        = "http://ed-fi.org/o'brien"
        }

        $sql | Should -HaveCount 13
        ($sql -join "`n") | Should -BeLike "*CMS''ReadOnly*"
        ($sql -join "`n") | Should -BeLike "*cms''client*"
        ($sql -join "`n") | Should -BeLike "*dms''client*"
        ($sql -join "`n") | Should -BeLike "*edfi_admin_api/read''only,full*"
        ($sql -join "`n") | Should -BeLike "*namespace''Prefixes*"
        ($sql -join "`n") | Should -BeLike "*http://ed-fi.org/o''brien*"

        foreach ($statement in $sql) {
            Remove-MssqlStringLiteral $statement |
                Should -Not -Match "'" -Because "every configured value must stay inside its T-SQL literal: $statement"
        }
    }

    It "stores the SQL Server permissions value as JSON after JSON escaping the configured scope" {
        $sql = Invoke-MssqlInsertDataCapture -Parameter @{ ClientScopeName = 'edfi_admin_api/full"access\scope' }

        $permissions = @($sql | Where-Object { $_ -match 'SET Permissions' })
        $permissions | Should -HaveCount 1
        $permissions[0] | Should -Match ([regex]::Escape('N''["edfi_admin_api/full\"access\\scope"]'''))
    }
}

Describe "OpenIddict signing-key initialization builders (DMS-1292)" {
    It "classifies the newest active key without inserting, locking or raising" {
        $sql = New-OpenIddictKeyStateCheckSql -EncryptionKey "state-check-passphrase"

        $sql | Should -Match 'WHERE "IsActive" = TRUE\s+ORDER BY "CreatedAt" DESC\s+LIMIT 1;' -Because "the check must read the key the Configuration Service signs with"
        $sql | Should -Match ("(?s)BEGIN\s+PERFORM pgp_sym_decrypt\(newest_private_key::bytea, passphrase\);.*EXCEPTION\s+" +
            "WHEN SQLSTATE '39000' THEN\s+key_state := 'active-undecryptable';\s+WHEN OTHERS THEN\s+key_state := 'active-unverified';") -Because "only pgcrypto's wrong-key-or-corrupt-data error may be reported as a key mismatch"
        $sql | Should -Not -Match 'INSERT INTO "dmscs"\."OpenIddictKey"' -Because "the state check must never change a row"
        $sql | Should -Not -Match 'pgp_sym_encrypt' -Because "the state check carries no key material"
        $sql | Should -Not -Match 'pg_advisory'
        $sql | Should -Not -Match 'RAISE' -Because "a raised error would log the statement text, passphrase included"
        $sql | Should -Match "SELECT state FROM openiddict_key_initialization_state;\s*$"
    }

    It "creates a key only under the lock, and decrypt-checks a key another initializer created first" {
        $sql = New-OpenIddictKeyInitializationSql -EncryptionKey "initialization-passphrase"

        $lock = $sql.IndexOf("PERFORM pg_advisory_xact_lock(1292000001);")
        $select = $sql.IndexOf('SELECT "PrivateKey" INTO newest_private_key')
        $lock | Should -BeGreaterThan -1
        $select | Should -BeGreaterThan $lock -Because "the active-key recheck must run under the lock"
        $sql | Should -Match ("(?s)IF newest_private_key IS NOT NULL THEN\s+BEGIN\s+PERFORM pgp_sym_decrypt\(newest_private_key::bytea, passphrase\);\s+" +
            "key_state := 'active-ok';\s+EXCEPTION\s+WHEN SQLSTATE '39000' THEN\s+key_state := 'active-undecryptable';\s+WHEN OTHERS THEN\s+key_state := 'active-unverified';\s+END;\s+ELSE\s+" +
            [regex]::Escape('INSERT INTO "dmscs"."OpenIddictKey" ("KeyId", "PublicKey", "PrivateKey", "IsActive")') + "\s+" +
            [regex]::Escape("VALUES (candidate_key_id, decode(candidate_public_key, 'base64'), pgp_sym_encrypt(candidate_private_key, passphrase), TRUE);") +
            "\s+key_state := 'created';\s+END IF;")
        ([regex]::Matches($sql, 'INSERT INTO "dmscs"\."OpenIddictKey"')).Count | Should -Be 1
        $sql | Should -Not -Match 'RAISE'
    }

    It "bounds the creation lock wait with a transaction-local lock_timeout set before the lock is requested" {
        $sql = New-OpenIddictKeyInitializationSql -EncryptionKey "initialization-passphrase"

        # set_config's third argument (is_local) scopes the value to the DO statement's transaction;
        # a standalone SET LOCAL outside an explicit transaction would not bound the lock wait.
        $sql | Should -Match ("(?s)" + [regex]::Escape("PERFORM set_config('lock_timeout', '30s', true);") + "\s+" +
            [regex]::Escape("PERFORM pg_advisory_xact_lock(1292000001);")) -Because "the lock wait must already be bounded when the lock is requested"
        ([regex]::Matches($sql, 'lock_timeout')).Count | Should -Be 1
        $sql | Should -Not -Match '(?m)^\s*SET\s' -Because "a session-level SET would outlive the initialization"
        New-OpenIddictKeyStateCheckSql -EncryptionKey "state-check-passphrase" | Should -Not -Match 'lock_timeout' -Because "the state check takes no lock"
    }

    It "catches every runtime failure of the <Builder> body, cancellation included, so the statement is never logged" -ForEach @(
        @{ Builder = "New-OpenIddictKeyStateCheckSql"; FirstStep = 'SELECT "PrivateKey" INTO newest_private_key' }
        @{ Builder = "New-OpenIddictKeyInitializationSql"; FirstStep = "PERFORM set_config('lock_timeout', '30s', true);" }
    ) {
        $sql = & $Builder -EncryptionKey "protected-passphrase"

        # The input read, lookup, lock and insert sit inside one protected block; the state row is written after it.
        $sql | Should -Match ("(?s)BEGIN\s+BEGIN\s+" +
            [regex]::Escape("SELECT convert_from(decode(input.encryption_key, 'base64'), 'UTF8'), input.key_id, input.public_key, input.private_key") + "\s+" +
            [regex]::Escape("INTO STRICT passphrase, candidate_key_id, candidate_public_key, candidate_private_key") + "\s+" +
            [regex]::Escape("FROM openiddict_key_initialization_input AS input;") + "\s+" +
            [regex]::Escape($FirstStep) + ".*END IF;\s+EXCEPTION\s+" +
            "WHEN query_canceled THEN\s+key_state := 'canceled';\s+WHEN OTHERS THEN\s+key_state := 'database-error ' \|\| SQLSTATE;\s+END;\s+" +
            "INSERT INTO openiddict_key_initialization_state \(state\) VALUES \(key_state\);\s+END\s+") -Because "WHEN OTHERS does not catch query_canceled, so cancellation needs its own handler"
        $sql | Should -Not -Match 'SQLERRM' -Because "the server's error text can quote the failing values"
    }

    It "sends the encryption key <Case> only as base64 COPY data, never in statement text, from <Builder>" -ForEach @(
        foreach ($builder in @("New-OpenIddictKeyStateCheckSql", "New-OpenIddictKeyInitializationSql")) {
            @{ Builder = $builder; Case = "with a quote"; EncryptionKey = "Identity'Encryption;Key" }
            @{ Builder = $builder; Case = "with the block's dollar-quote tag"; EncryptionKey = 'prefix$openiddict_key_initialization$suffix' }
            @{ Builder = $builder; Case = "with a tab, a line break and a backslash"; EncryptionKey = "tab`tline`nback\slash" }
            @{ Builder = $builder; Case = "that is empty"; EncryptionKey = "" }
        }
    ) {
        $sql = & $Builder -EncryptionKey $EncryptionKey
        $lines = $sql -split "`n"

        # The COPY line, one data row and the end-of-data marker, in that order, before any other statement.
        $copy = [array]::IndexOf($lines, "COPY openiddict_key_initialization_input (encryption_key, key_id, public_key, private_key) FROM STDIN;")
        $copy | Should -Be 1
        $lines[$copy + 2] | Should -BeExactly '\.'
        $fields = $lines[$copy + 1] -split "`t"
        $fields.Count | Should -Be 4
        [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($fields[0])) | Should -BeExactly $EncryptionKey

        $statements = (@($lines[0..$copy]) + @($lines[($copy + 2)..($lines.Count - 1)])) -join "`n"
        foreach ($value in @($fields | Where-Object { $_ -and $_ -ne '\N' })) {
            $statements.Contains($value) | Should -BeFalse -Because "a COPY data value must not also appear in statement text"
        }
        if ($EncryptionKey) {
            $sql.Contains($EncryptionKey) | Should -BeFalse -Because "the encryption key travels only base64-encoded"
            $sql.Contains($EncryptionKey.Replace("'", "''")) | Should -BeFalse
        }

        if ($Builder -eq "New-OpenIddictKeyStateCheckSql") {
            $fields[1..3] | Should -Be @('\N', '\N', '\N') -Because "the state check carries no key material"
        }
        else {
            $fields[1..3] | Should -Not -Contain '\N'
            [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($fields[1])) | Should -Match '^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$' -Because "the key id is the base64 of a GUID, as New-OpenIddictKeyInsertSql writes it"
            $null = [Convert]::FromBase64String($fields[2])
            $null = [Convert]::FromBase64String($fields[3])
        }
    }

    It "refuses candidate key material that COPY's text format would read as a field or row break, withholding it" {
        Mock New-OpenIddictKeyPair -ModuleName "OpenIddict-Crypto" {
            [PSCustomObject]@{ PublicKey = "AQID"; PrivateKey = "NOT-BASE64`tSECRET-MATERIAL" }
        }

        $failure = { New-OpenIddictKeyInitializationSql -EncryptionKey "unused" } | Should -Throw "*candidate PrivateKey is not base64*" -PassThru
        $failure.Exception.Message | Should -Not -BeLike "*SECRET-MATERIAL*"
    }

    It "serializes SQL Server initialization in one transaction and binds the secret as a parameter" {
        $encryptionKey = "SqlServer'Initialization;Key"

        $command = New-OpenIddictKeyInitializationCommand -EncryptionKey $encryptionKey

        $command.Sql | Should -Match "SET XACT_ABORT ON;"
        $command.Sql | Should -Match ([regex]::Escape("EXEC @lockResult = sp_getapplock @Resource = N'dmscs.OpenIddictKey.Initialize', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;"))
        $command.Sql | Should -Match "IF @lockResult < 0\s+THROW 51292,"
        $command.Sql | Should -Match ("(?s)IF @newestPrivateKey IS NULL\s+BEGIN\s+INSERT INTO dmscs\.OpenIddictKey.*SET @keyState = N'created';\s+END\s+" +
            "ELSE IF DECRYPTBYPASSPHRASE\(@EncryptionKey, @newestPrivateKey\) IS NOT NULL\s+SET @keyState = N'active-ok';\s+ELSE\s+SET @keyState = N'active-undecryptable';")
        $command.Sql | Should -Match "COMMIT TRANSACTION;\s+SELECT @keyState;\s*$"
        $command.Sql | Should -Not -Match ([regex]::Escape($encryptionKey))
        $command.Parameters.EncryptionKey | Should -Be $encryptionKey
        $command.Parameters.PrivateKey | Should -Not -BeNullOrEmpty
        $command.Parameters.PublicKey.GetType() | Should -Be ([byte[]])
    }
}

Describe "OpenIddict signing-key initialization in -InitDb (DMS-1292)" {
    BeforeAll {
        $script:SyntheticPassphrase = "Synthetic-Dms1292-Passphrase'x"
        $script:SyntheticPrivateKey = "SYNTHETIC-DMS1292-PRIVATE-KEY-MATERIAL"
        $script:FixedUndecryptableError = "*newest active OpenIddict signing key cannot be decrypted with the configured encryption key; no signing key was changed*"
    }

    Context "PostgreSQL" {
        BeforeEach {
            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "Postgresql" `
                    -ConnectionString "Host=localhost;Port=5432;Database=edfi_configurationservice;Username=postgres;" `
                    -DbName "edfi_configurationservice" -PostgresContainerName "dms-postgresql-test" -EncryptionKey $script:SyntheticPassphrase
            }
            finally {
                Pop-Location
            }

            Mock Invoke-PostgresGuardedDatabaseCreate { }
            Mock Invoke-DbQuery { }
            Mock New-OpenIddictKeyPair -ModuleName "OpenIddict-Crypto" {
                [PSCustomObject]@{ PublicKey = [Convert]::ToBase64String([byte[]](1, 2, 3)); PrivateKey = $script:SyntheticPrivateKey }
            }

            # The state each phase reports. Phase B is the script that takes the advisory lock.
            $script:PhaseAState = "none"
            $script:PhaseBState = "created"
            Mock Invoke-PostgresOpenIddictKeyScript { $script:PhaseAState } -ParameterFilter { $Script -notmatch "pg_advisory_xact_lock" }
            Mock Invoke-PostgresOpenIddictKeyScript { $script:PhaseBState } -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "creates the initial key when no active key exists" {
            $output = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }

            $output | Should -Contain "OpenIddict signing key: created the initial active key"
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 1 -Exactly -ParameterFilter { $Script -notmatch "pg_advisory_xact_lock" }
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 1 -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "reuses a decryptable active key without generating key material" {
            $script:PhaseAState = "active-ok"

            $output = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }

            $output | Should -Contain "OpenIddict signing key: reusing the existing active key"
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 0 -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
            Should -Invoke New-OpenIddictKeyPair -ModuleName "OpenIddict-Crypto" -Times 0 -Exactly -Because "the ordinary rerun must not generate or send a candidate key"
        }

        It "stops with the fixed error, changing nothing, when the newest active key does not decrypt" {
            $script:PhaseAState = "active-undecryptable"

            { Invoke-InitDbScripts 6>$null } | Should -Throw $script:FixedUndecryptableError
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 0 -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "stops without blaming either key, changing nothing, when the database rejects the check itself" {
            $script:PhaseAState = "active-unverified"

            $failure = { Invoke-InitDbScripts 6>$null } | Should -Throw -PassThru
            $failure.Exception.Message | Should -BeExactly "The newest active OpenIddict signing key could not be checked: the database rejected the decryption check for a reason other than the encryption key (for example, pgcrypto permissions); no signing key was changed. Fix the database access; neither the signing key nor the encryption key is shown to be wrong."
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 0 -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "stops with the fixed error when a concurrent initializer created a key this passphrase cannot decrypt" {
            $script:PhaseBState = "active-undecryptable"

            { Invoke-InitDbScripts 6>$null } | Should -Throw $script:FixedUndecryptableError
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 1 -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "reuses a decryptable key a concurrent initializer created first" {
            $script:PhaseBState = "active-ok"

            $output = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }

            $output | Should -Contain "OpenIddict signing key: reusing the existing active key"
        }

        It "fails on an unexpected state rather than treating it as success" {
            $script:PhaseAState = "something-else"

            { Invoke-InitDbScripts 6>$null } | Should -Throw "*unexpected result*"
        }

        It "stops with a fixed database error, naming only the SQLSTATE, when phase <Phase> reports one" -ForEach @(
            @{ Phase = "A"; PhaseA = "database-error 42501"; PhaseB = "created"; SqlState = "42501"; LockedRuns = 0 }
            @{ Phase = "B"; PhaseA = "none"; PhaseB = "database-error 23514"; SqlState = "23514"; LockedRuns = 1 }
        ) {
            $script:PhaseAState = $PhaseA
            $script:PhaseBState = $PhaseB

            $failure = { Invoke-InitDbScripts 6>$null } | Should -Throw -PassThru
            $failure.Exception.Message | Should -BeExactly "The database rejected OpenIddict signing-key initialization while reading, locking or creating the signing key (SQLSTATE $SqlState); no signing key was changed. Neither the signing key nor the encryption key is shown to be wrong; check the database user's access to the ""dmscs"".""OpenIddictKey"" table."
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times $LockedRuns -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "stops with the fixed cancellation error when the database cancels the locked phase" {
            $script:PhaseBState = "canceled"

            $failure = { Invoke-InitDbScripts 6>$null } | Should -Throw -PassThru
            $failure.Exception.Message | Should -BeExactly "OpenIddict signing-key initialization was canceled in the database (for example, by statement_timeout or a cancel request); no signing key was changed."
        }

        It "stops with the fixed lock-timeout error, not a key or permission error, when the locked phase reports 55P03" {
            $script:PhaseBState = "database-error 55P03"

            $failure = { Invoke-InitDbScripts 6>$null } | Should -Throw -PassThru
            $failure.Exception.Message | Should -BeExactly "OpenIddict signing-key initialization timed out waiting for a database lock (SQLSTATE 55P03), most likely the initialization lock held by another -InitDb run; no signing key was changed. Neither the signing key nor the encryption key is shown to be wrong; rerun once the other initialization has finished."
            $failure.Exception.Message.Contains($script:SyntheticPassphrase) | Should -BeFalse
            Should -Invoke Invoke-PostgresOpenIddictKeyScript -Times 1 -Exactly -ParameterFilter { $Script -match "pg_advisory_xact_lock" }
        }

        It "never runs the bare key insert" {
            Invoke-InitDbScripts 6>$null

            Should -Invoke Invoke-DbQuery -Times 0 -Exactly -ParameterFilter { $Sql -match 'OpenIddictKey" \("KeyId"' }
        }

        It "keeps the passphrase and key material off every stream, on success and on failure" {
            $output = & {
                $VerbosePreference = "Continue"
                $DebugPreference = "Continue"
                Invoke-InitDbScripts
                $script:PhaseAState = "active-undecryptable"
                try { Invoke-InitDbScripts } catch { $_ }
            } *>&1 | ForEach-Object { [string]$_ }
            $text = $output -join "`n"

            $text | Should -BeLike "*created the initial active key*" -Because "the success path must have run"
            $text | Should -BeLike "*cannot be decrypted*" -Because "the failure path must have run"
            $text.Contains($script:SyntheticPassphrase) | Should -BeFalse
            $text.Contains($script:SyntheticPassphrase.Replace("'", "''")) | Should -BeFalse
            $text.Contains($script:SyntheticPrivateKey) | Should -BeFalse
        }
    }

    Context "PostgreSQL script transport" {
        BeforeAll {
            # A plain function stands in for docker (or host psql), as in the guarded-database-creation
            # tests, so the test sees what reaches stdin as well as the arguments.
            function script:Invoke-KeyScriptWithDockerShim {
                param([int]$Exit, [object[]]$Output, [string]$Script, [string]$Command = "docker")

                $calls = [System.Collections.Generic.List[object]]::new()
                $exitCode = $Exit
                $psqlOutput = $Output
                Set-Item -Path "Function:\global:$Command" -Value {
                    $piped = @($input)
                    $calls.Add([PSCustomObject]@{
                        Args           = @($args | ForEach-Object { [string]$_ })
                        ArgLine        = (@($args | ForEach-Object { $_ }) -join " ")
                        ExpectingInput = $MyInvocation.ExpectingInput
                        Stdin          = (($piped | ForEach-Object { [string]$_ }) -join "`n")
                    })
                    $global:LASTEXITCODE = $exitCode
                    return $psqlOutput
                }.GetNewClosure()

                $caught = $null
                $result = $null
                try { $result = Invoke-PostgresOpenIddictKeyScript -Script $Script }
                catch { $caught = $_ }
                finally { Remove-Item "Function:\$Command" -Force -ErrorAction SilentlyContinue }

                return [PSCustomObject]@{ Call = $calls[0]; Result = $result; Error = $caught }
            }
        }

        BeforeEach {
            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "Postgresql" `
                    -ConnectionString "Host=localhost;Port=5432;Database=edfi_configurationservice;Username=postgres;" `
                    -PostgresContainerName "dms-postgresql-test" -EncryptionKey "unused"
            }
            finally {
                Pop-Location
            }
        }

        It "sends the script on stdin, never as an argument, with error text suppressed" {
            $keyScript = New-OpenIddictKeyStateCheckSql -EncryptionKey "Transport-Passphrase-1292"

            $run = Invoke-KeyScriptWithDockerShim -Exit 0 -Output @("active-ok") -Script $keyScript

            $run.Result | Should -Be "active-ok"
            $run.Call.ExpectingInput | Should -BeTrue
            $run.Call.Stdin | Should -BeExactly $keyScript
            $run.Call.ArgLine | Should -Not -BeLike "*Transport-Passphrase-1292*"
            $run.Call.ArgLine | Should -BeLike "*-v ON_ERROR_STOP=1*"
            $run.Call.ArgLine | Should -BeLike "*-v VERBOSITY=sqlstate*"
            $run.Call.ArgLine | Should -BeLike "*-f -*"
            $run.Call.ArgLine | Should -BeLike "*-d edfi_configurationservice*"
        }

        It "skips psqlrc startup files in the container and on the host" {
            # A shim checks the argument contract only; psql's own startup-file handling is not exercised.
            $keyScript = New-OpenIddictKeyStateCheckSql -EncryptionKey "Transport-Passphrase-1292"

            $container = Invoke-KeyScriptWithDockerShim -Exit 0 -Output @("active-ok") -Script $keyScript

            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "Postgresql" `
                    -ConnectionString "Host=localhost;Port=5432;Database=edfi_configurationservice;Username=postgres;" `
                    -PostgresContainerName "" -EncryptionKey "unused"
            }
            finally {
                Pop-Location
            }
            $hostRun = Invoke-KeyScriptWithDockerShim -Command "psql" -Exit 0 -Output @("active-ok") -Script $keyScript

            $container.Call.Args | Should -Contain "-X"
            $container.Result | Should -BeExactly "active-ok"
            $hostRun.Call | Should -Not -BeNullOrEmpty -Because "the host path must invoke psql directly"
            $hostRun.Call.Args | Should -Contain "-X"
            $hostRun.Call.Args | Should -Contain "localhost"
            $hostRun.Call.Stdin | Should -BeExactly $keyScript
            $hostRun.Result | Should -BeExactly "active-ok"
        }

        It "fails with the SQLSTATE only, withholding the captured output" {
            $run = Invoke-KeyScriptWithDockerShim -Exit 3 -Output @("psql:<stdin>:4: ERROR:  42501", "Transport-Passphrase-1292") `
                -Script (New-OpenIddictKeyStateCheckSql -EncryptionKey "Transport-Passphrase-1292")

            $run.Error.Exception.Message | Should -BeLike "*SQLSTATE: 42501*"
            $run.Error.Exception.Message | Should -Not -BeLike "*Transport-Passphrase-1292*"
        }

        It "reads the state from stdout only, whatever psql diagnostics 2>&1 merges in after it" {
            # 2>&1 turns each psql stderr line into an ErrorRecord, and PowerShell does not order the
            # two streams relative to each other, so a diagnostic can land after the state row.
            $diagnostic = [System.Management.Automation.ErrorRecord]::new(
                [System.Management.Automation.RemoteException]::new("psql:<stdin>:9: NOTICE:  00000"), "NativeCommandError", "NotSpecified", $null)

            $run = Invoke-KeyScriptWithDockerShim -Exit 0 -Output @("created", $diagnostic) `
                -Script (New-OpenIddictKeyStateCheckSql -EncryptionKey "Transport-Passphrase-1292")

            $run.Error | Should -BeNullOrEmpty
            $run.Result | Should -BeExactly "created"
        }
    }

    Context "SQL Server" {
        BeforeEach {
            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "MSSQL" -DbName "edfi_configurationservice" -DbUser "sa" `
                    -DbPassword "abcdefgh1!" -MssqlContainerName "dms-mssql-test" -EncryptionKey $script:SyntheticPassphrase
            }
            finally {
                Pop-Location
            }

            Mock Invoke-DbQuery { "1" } -ParameterFilter { $Sql -match "DB_ID" }
            Mock Invoke-DbQuery { "" } -ParameterFilter { $Sql -notmatch "DB_ID" }
            $script:MssqlState = "created"
            Mock Invoke-MssqlParameterizedQuery { $script:MssqlState }
        }

        It "runs the locked initialization command, not the bare insert" {
            $output = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }

            $output | Should -Contain "OpenIddict signing key: created the initial active key"
            Should -Invoke Invoke-MssqlParameterizedQuery -Times 1 -Exactly -ParameterFilter {
                $Sql -match "sp_getapplock" -and $Sql -match "DECRYPTBYPASSPHRASE" -and $Parameters.EncryptionKey -eq $script:SyntheticPassphrase
            }
        }

        It "reports a reused key" {
            $script:MssqlState = "active-ok"

            $output = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }

            $output | Should -Contain "OpenIddict signing key: reusing the existing active key"
        }

        It "stops with the fixed error when the newest active key does not decrypt" {
            $script:MssqlState = "active-undecryptable"

            { Invoke-InitDbScripts 6>$null } | Should -Throw $script:FixedUndecryptableError
        }

        It "gives the command longer than the application-lock wait, so a lock timeout reports its own error" {
            # SqlCommand's default 30 s timeout equals the lock wait and could cancel the batch first.
            $lockTimeoutMs = [int][regex]::Match((New-OpenIddictKeyInitializationCommand -EncryptionKey "unused").Sql, '@LockTimeout = (\d+);').Groups[1].Value
            $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $script:DockerComposePath "setup-openiddict.ps1"), [ref]$null, [ref]$null)
            $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Invoke-MssqlParameterizedQuery" }, $true)
            $assignment = $function.Body.Find({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$command.CommandTimeout' }, $true)

            $lockTimeoutMs | Should -BeGreaterThan 0
            $assignment | Should -Not -BeNullOrEmpty
            ([int]$assignment.Right.Extent.Text) * 1000 | Should -BeGreaterThan $lockTimeoutMs
        }
    }
}

# Opt-in live suites (DMS-1292): the signing-key initialization runs against a real engine, with
# nothing on the key path mocked. Each Describe skips itself unless its variable names a disposable
# server, so CI lanes without one are unaffected. Every test creates and drops its own database.
#   DMS_OPENIDDICT_KEY_LIVE_PG_CONTAINER      a PostgreSQL container whose local psql trusts postgres
#   DMS_OPENIDDICT_KEY_LIVE_MSSQL_CONNECTION  a SqlClient connection string with server-admin rights
# Concurrent initializers are not covered here: no existing fixture can hold one initializer inside
# the lock while another waits without timing assumptions. A plain session holding the lock is
# covered, for the bounded wait.
Describe "OpenIddict signing-key initialization against live PostgreSQL (DMS-1292)" -Skip:([string]::IsNullOrWhiteSpace($env:DMS_OPENIDDICT_KEY_LIVE_PG_CONTAINER)) {
    BeforeAll {
        $script:LivePgContainer = $env:DMS_OPENIDDICT_KEY_LIVE_PG_CONTAINER
        $script:LivePwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        $script:LiveKey = "Live-Dms1292-Encryption'Key"
        $script:LiveWrongKey = "Live-Dms1292-Other-Key"

        function script:Invoke-LivePgSql {
            param([string]$Database, [string]$Sql)

            $output = $Sql | & docker exec -i $script:LivePgContainer psql -U postgres -d $Database -v ON_ERROR_STOP=1 -q -tA -f - 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Live fixture SQL failed: $($output | Out-String)" }
            return @($output | ForEach-Object { [string]$_ } | Where-Object { $_ })
        }

        function script:Get-LiveKeyRows {
            Invoke-LivePgSql -Database $script:LiveDb -Sql @'
SELECT "Id" || '|' || "KeyId" || '|' || md5("PrivateKey") || '|' || md5("PublicKey") || '|' || "IsActive" || '|' || "CreatedAt"
FROM "dmscs"."OpenIddictKey" ORDER BY "Id";
'@
        }

        # Runs PowerShell in a child process from eng/docker-compose, as an operator would, so each run
        # loads setup-openiddict.ps1 afresh. Returns the exit code and all output as text; a failure's
        # message is printed on one line, unwrapped by the console's error view.
        function script:Invoke-LiveChild {
            param([string]$Command)

            $quotedPath = "'" + ([string]$script:DockerComposePath).Replace("'", "''") + "'"
            $output = & $script:LivePwsh -NoProfile -NonInteractive -Command "Set-Location -LiteralPath $quotedPath; try { $Command } catch { 'FAILED: ' + `$_.Exception.Message; exit 1 }" 2>&1
            return [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Text = ($output | Out-String) }
        }

        function script:Get-LiveSetupArguments {
            param([string]$EncryptionKey, [string]$User = "postgres")

            $quote = { param([string]$Value) "'" + $Value.Replace("'", "''") + "'" }
            return "-EnvironmentFile '' -SkipLegacyEnvironmentVariableWarning -DbType Postgresql " +
                "-ConnectionString $(& $quote "Host=localhost;Database=$($script:LiveDb);Username=$User;") " +
                "-DbName $(& $quote $script:LiveDb) -PostgresContainerName $(& $quote $script:LivePgContainer) " +
                "-EncryptionKey $(& $quote $EncryptionKey)"
        }

        function script:Invoke-LiveInitDb {
            param([string]$EncryptionKey, [string]$User = "postgres")

            Invoke-LiveChild -Command "./setup-openiddict.ps1 -InitDb $(Get-LiveSetupArguments -EncryptionKey $EncryptionKey -User $User)"
        }

        function script:Get-LiveServerLogLineCount {
            return @(& docker logs $script:LivePgContainer 2>&1).Count
        }

        # Asserts that the server log written since $FromLine holds none of the initialization statement's
        # sensitive text. A deliberately failing control statement runs first and must appear with its
        # STATEMENT line, so the check fails rather than passing vacuously if error-statement logging is
        # off or the log is not being read.
        function script:Assert-LiveServerLogWithholdsStatement {
            param([int]$FromLine, [string[]]$Sensitive)

            $marker = "dms1292_log_control_" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
            $null = "SELECT 1/0 AS $marker;" | & docker exec -i $script:LivePgContainer psql -U postgres -d postgres -q -f - 2>&1
            $log = (@(& docker logs $script:LivePgContainer 2>&1 | ForEach-Object { [string]$_ }) | Select-Object -Skip $FromLine) -join "`n"

            $log | Should -Match "STATEMENT:\s+SELECT 1/0 AS $marker" -Because "the control error must be logged with its statement, or this check proves nothing"
            foreach ($value in @($Sensitive + @('openiddict_key_initialization', 'pgp_sym_', 'MIIE'))) {
                $log.Contains($value) | Should -BeFalse -Because "the server log must not carry the initialization statement ('$value')"
            }
        }

        function script:Assert-LiveOutputWithholdsStatement {
            param([string]$Text)

            foreach ($sensitive in @($script:LiveKey, $script:LiveKey.Replace("'", "''"), 'openiddict_key_initialization', 'pgp_sym_', 'MIIE')) {
                $Text.Contains($sensitive) | Should -BeFalse -Because "the client output must not carry the initialization statement ('$sensitive')"
            }
        }

        # Creates the database through a normal run, then empties the key table so the next run takes
        # the locked creation path.
        function script:Initialize-LiveEmptyKeyTable {
            (Invoke-LiveInitDb -EncryptionKey $script:LiveKey).ExitCode | Should -Be 0
            Invoke-LivePgSql -Database $script:LiveDb -Sql 'DELETE FROM "dmscs"."OpenIddictKey";' | Out-Null
        }

        # A login role that may run every -InitDb statement; $TableGrant adds key-table privileges.
        function script:New-LiveLimitedRole {
            param([string]$TableGrant = "")

            Invoke-LivePgSql -Database $script:LiveDb -Sql @"
CREATE ROLE $($script:LiveRole) LOGIN;
GRANT CONNECT, CREATE ON DATABASE $($script:LiveDb) TO $($script:LiveRole);
GRANT USAGE, CREATE ON SCHEMA "dmscs" TO $($script:LiveRole);
$TableGrant
"@ | Out-Null
        }
    }

    BeforeEach {
        $suffix = [Guid]::NewGuid().ToString("N").Substring(0, 12)
        $script:LiveDb = "dms1292_keyinit_$suffix"
        $script:LiveRole = "dms1292_limited_$suffix"
    }

    AfterEach {
        Invoke-LivePgSql -Database "postgres" -Sql "DROP DATABASE IF EXISTS $($script:LiveDb) WITH (FORCE); DROP ROLE IF EXISTS $($script:LiveRole);" | Out-Null
    }

    It "creates one key that decrypts with the configured encryption key, then keeps it unchanged on rerun" {
        $first = Invoke-LiveInitDb -EncryptionKey $script:LiveKey
        $afterFirst = @(Get-LiveKeyRows)
        $second = Invoke-LiveInitDb -EncryptionKey $script:LiveKey

        $first.ExitCode | Should -Be 0 -Because $first.Text
        $first.Text | Should -BeLike "*created the initial active key*"
        $afterFirst.Count | Should -Be 1
        $decryptable = Invoke-LivePgSql -Database $script:LiveDb -Sql "SELECT count(*) FROM ""dmscs"".""OpenIddictKey"" WHERE pgp_sym_decrypt(""PrivateKey""::bytea, '$($script:LiveKey.Replace("'", "''"))') IS NOT NULL;"
        $decryptable | Should -Be "1"
        $second.ExitCode | Should -Be 0 -Because $second.Text
        $second.Text | Should -BeLike "*reusing the existing active key*"
        @(Get-LiveKeyRows) | Should -Be $afterFirst -Because "a rerun must not insert, update or deactivate any key"
    }

    It "keeps the encryption key and private key out of a server log that records every successful statement (<Setting>)" -ForEach @(
        # log_min_duration_statement is what local-postgresql-diagnostics.yml and -headroom.yml set (250 ms);
        # a zero threshold logs every statement without depending on how long one runs or waits.
        @{ Setting = "log_min_duration_statement = 0" }
        @{ Setting = "log_statement = 'all'" }
    ) {
        # Scoped to the disposable database, which -InitDb then finds already created.
        Invoke-LivePgSql -Database "postgres" -Sql "CREATE DATABASE $($script:LiveDb);`nALTER DATABASE $($script:LiveDb) SET $Setting;" | Out-Null
        $logStart = Get-LiveServerLogLineCount

        $first = Invoke-LiveInitDb -EncryptionKey $script:LiveKey
        $second = Invoke-LiveInitDb -EncryptionKey $script:LiveKey
        # A successful, non-sensitive control statement in the same database, so the check fails rather
        # than passing vacuously if the setting is not in effect or the log is not being read.
        $marker = "dms1292_log_control_" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
        Invoke-LivePgSql -Database $script:LiveDb -Sql "SELECT '$marker';" | Out-Null
        # Read before any fixture statement below carries the passphrase into this database's log.
        $log = (@(& docker logs $script:LivePgContainer 2>&1 | ForEach-Object { [string]$_ }) | Select-Object -Skip $logStart) -join "`n"

        $first.ExitCode | Should -Be 0 -Because $first.Text
        $first.Text | Should -BeLike "*created the initial active key*"
        $second.ExitCode | Should -Be 0 -Because $second.Text
        $second.Text | Should -BeLike "*reusing the existing active key*"
        $log | Should -Match "statement: SELECT '$marker'" -Because "the control statement must be logged, or this check proves nothing"

        $privateKeys = @(Invoke-LivePgSql -Database $script:LiveDb -Sql "SELECT pgp_sym_decrypt(""PrivateKey""::bytea, '$($script:LiveKey.Replace("'", "''"))') FROM ""dmscs"".""OpenIddictKey"";")
        $privateKeys.Count | Should -Be 1
        $privateKey = [string]$privateKeys[0]
        $privateKey.Length | Should -BeGreaterThan 1000 -Because "the decrypted value must be the generated PKCS#8 private key"
        $sensitive = @(
            $script:LiveKey,
            $script:LiveKey.Replace("'", "''"),
            [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($script:LiveKey)),
            $privateKey,
            $privateKey.Substring(400, 64)
        )
        foreach ($value in $sensitive) {
            $log.Contains($value) | Should -BeFalse -Because "the server log must not carry the passphrase or private-key material"
        }
        $log | Should -Match ([regex]::Escape("pgp_sym_encrypt(candidate_private_key, passphrase)")) -Because "the creation statement itself must have been logged"
        $log | Should -Match ([regex]::Escape("COPY openiddict_key_initialization_input")) -Because "the input statement itself must have been logged"
    }

    It "stops with the encryption-key error and changes no key when the configured key does not decrypt the active key" {
        (Invoke-LiveInitDb -EncryptionKey $script:LiveKey).ExitCode | Should -Be 0
        $before = @(Get-LiveKeyRows)

        $run = Invoke-LiveInitDb -EncryptionKey $script:LiveWrongKey

        $run.ExitCode | Should -Not -Be 0
        $run.Text | Should -BeLike "*cannot be decrypted with the configured encryption key; no signing key was changed*"
        $run.Text | Should -Not -BeLike "*$($script:LiveWrongKey)*"
        @(Get-LiveKeyRows) | Should -Be $before
    }

    It "keeps a key another initializer created before the locked phase ran, inserting nothing" {
        # The outcome of losing the race: the locked phase rechecks under the lock and finds a key.
        (Invoke-LiveInitDb -EncryptionKey $script:LiveKey).ExitCode | Should -Be 0
        $before = @(Get-LiveKeyRows)

        $states = foreach ($key in @($script:LiveKey, $script:LiveWrongKey)) {
            $quotedKey = "'" + $key.Replace("'", "''") + "'"
            $run = Invoke-LiveChild -Command ". ./setup-openiddict.ps1 $(Get-LiveSetupArguments -EncryptionKey $key); Invoke-PostgresOpenIddictKeyScript -Script (New-OpenIddictKeyInitializationSql -EncryptionKey $quotedKey)"
            $run.ExitCode | Should -Be 0 -Because $run.Text
            ($run.Text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
        }

        $states | Should -Be @("active-ok", "active-undecryptable")
        @(Get-LiveKeyRows) | Should -Be $before
    }

    It "reports a check the database rejects as unverified, not as a key mismatch, and changes no key" {
        (Invoke-LiveInitDb -EncryptionKey $script:LiveKey).ExitCode | Should -Be 0
        $before = @(Get-LiveKeyRows)
        # A role that may run every -InitDb statement except pgcrypto's decrypt function.
        Invoke-LivePgSql -Database $script:LiveDb -Sql @"
CREATE ROLE $($script:LiveRole) LOGIN;
GRANT CONNECT, CREATE ON DATABASE $($script:LiveDb) TO $($script:LiveRole);
GRANT USAGE, CREATE ON SCHEMA "dmscs" TO $($script:LiveRole);
GRANT SELECT ON "dmscs"."OpenIddictKey" TO $($script:LiveRole);
REVOKE EXECUTE ON FUNCTION pgp_sym_decrypt(bytea, text) FROM PUBLIC;
"@ | Out-Null

        $run = Invoke-LiveInitDb -EncryptionKey $script:LiveKey -User $script:LiveRole

        $run.ExitCode | Should -Not -Be 0
        $run.Text | Should -BeLike "*could not be checked: the database rejected the decryption check for a reason other than the encryption key*"
        $run.Text | Should -Not -BeLike "*cannot be decrypted*"
        @(Get-LiveKeyRows) | Should -Be $before
    }

    It "reports a denied key lookup as a database error, not a key mismatch, and keeps the statement out of the server log" {
        (Invoke-LiveInitDb -EncryptionKey $script:LiveKey).ExitCode | Should -Be 0
        $before = @(Get-LiveKeyRows)
        New-LiveLimitedRole
        $logStart = Get-LiveServerLogLineCount

        $run = Invoke-LiveInitDb -EncryptionKey $script:LiveKey -User $script:LiveRole

        $run.ExitCode | Should -Not -Be 0
        $run.Text | Should -BeLike "*rejected OpenIddict signing-key initialization while reading, locking or creating the signing key (SQLSTATE 42501); no signing key was changed*"
        $run.Text | Should -Not -BeLike "*cannot be decrypted*"
        @(Get-LiveKeyRows) | Should -Be $before
        Assert-LiveOutputWithholdsStatement -Text $run.Text
        Assert-LiveServerLogWithholdsStatement -FromLine $logStart -Sensitive @($script:LiveKey, $script:LiveKey.Replace("'", "''"))
    }

    It "reports a <Case> on the creation path as <Expected>, inserts nothing, and keeps the statement out of the server log" -ForEach @(
        @{ Case = "denied advisory lock"; Expected = "a database error"; Message = "*while reading, locking or creating the signing key (SQLSTATE 42501); no signing key was changed*"; LimitedRole = $true
            Fault = @'
GRANT SELECT, INSERT ON "dmscs"."OpenIddictKey" TO {role};
GRANT USAGE ON SEQUENCE "dmscs"."OpenIddictKey_Id_seq" TO {role};
REVOKE EXECUTE ON FUNCTION pg_advisory_xact_lock(bigint) FROM PUBLIC;
'@ }
        @{ Case = "rejected insert"; Expected = "a database error"; Message = "*while reading, locking or creating the signing key (SQLSTATE 23514); no signing key was changed*"; LimitedRole = $false
            Fault = @'
ALTER TABLE "dmscs"."OpenIddictKey" ADD CONSTRAINT dms1292_reject_insert CHECK (false) NOT VALID;
'@ }
        # WHEN OTHERS does not catch query_canceled; a trigger raising it stands in for statement_timeout
        # or a cancel request without any timing.
        @{ Case = "canceled insert"; Expected = "a cancellation"; Message = "*was canceled in the database*no signing key was changed*"; LimitedRole = $false
            Fault = @'
CREATE FUNCTION "dmscs".dms1292_cancel_insert() RETURNS trigger LANGUAGE plpgsql AS $cancel$
BEGIN
    RAISE EXCEPTION USING ERRCODE = 'query_canceled';
END
$cancel$;
CREATE TRIGGER dms1292_cancel_insert BEFORE INSERT ON "dmscs"."OpenIddictKey" FOR EACH ROW EXECUTE FUNCTION "dmscs".dms1292_cancel_insert();
'@ }
    ) {
        Initialize-LiveEmptyKeyTable
        $user = "postgres"
        if ($LimitedRole) {
            New-LiveLimitedRole -TableGrant $Fault.Replace("{role}", $script:LiveRole)
            $user = $script:LiveRole
        }
        else {
            Invoke-LivePgSql -Database $script:LiveDb -Sql $Fault | Out-Null
        }
        $logStart = Get-LiveServerLogLineCount

        $run = Invoke-LiveInitDb -EncryptionKey $script:LiveKey -User $user

        $run.ExitCode | Should -Not -Be 0
        $run.Text | Should -BeLike $Message
        $run.Text | Should -Not -BeLike "*cannot be decrypted*"
        @(Get-LiveKeyRows).Count | Should -Be 0 -Because "a failed creation must roll back its insert"
        Assert-LiveOutputWithholdsStatement -Text $run.Text
        Assert-LiveServerLogWithholdsStatement -FromLine $logStart -Sensitive @($script:LiveKey, $script:LiveKey.Replace("'", "''"))
    }

    It "stops with the lock-timeout error and inserts nothing when another session holds the initialization lock" {
        Initialize-LiveEmptyKeyTable
        # The holder session takes the lock and confirms it on stdout before -InitDb starts, so the
        # locked phase is known to wait; the only timing involved is the script's own lock_timeout.
        $holderStart = [System.Diagnostics.ProcessStartInfo]::new("docker")
        foreach ($argument in @("exec", "-i", $script:LivePgContainer, "psql", "-X", "-q", "-tA", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", $script:LiveDb)) {
            $holderStart.ArgumentList.Add($argument)
        }
        $holderStart.RedirectStandardInput = $true
        $holderStart.RedirectStandardOutput = $true
        $holderStart.UseShellExecute = $false
        $holder = [System.Diagnostics.Process]::Start($holderStart)
        $initializer = $null
        try {
            $holder.StandardInput.WriteLine("SELECT pg_advisory_lock(1292000001);")
            $holder.StandardInput.WriteLine("SELECT 'dms1292_lock_held';")
            $holder.StandardInput.Flush()
            do {
                $read = $holder.StandardOutput.ReadLineAsync()
                $read.Wait(30000) | Should -BeTrue -Because "the holder session must confirm the lock"
                $null -eq $read.Result | Should -BeFalse -Because "the holder session ended before confirming the lock"
            } until ($read.Result -eq "dms1292_lock_held")
            Invoke-LivePgSql -Database $script:LiveDb -Sql "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objid = 1292000001 AND granted;" |
                Should -Be "1"

            # The same child run as Invoke-LiveInitDb, but as a process this test can stop: if the
            # production timeout is missing or ineffective, the watchdog fails the test instead of the
            # wait hanging the run with the holder still open.
            $quotedPath = "'" + ([string]$script:DockerComposePath).Replace("'", "''") + "'"
            $initializerStart = [System.Diagnostics.ProcessStartInfo]::new($script:LivePwsh)
            foreach ($argument in @("-NoProfile", "-NonInteractive", "-Command",
                    "Set-Location -LiteralPath $quotedPath; try { ./setup-openiddict.ps1 -InitDb $(Get-LiveSetupArguments -EncryptionKey $script:LiveKey) } catch { 'FAILED: ' + `$_.Exception.Message; exit 1 }")) {
                $initializerStart.ArgumentList.Add($argument)
            }
            $initializerStart.RedirectStandardOutput = $true
            $initializerStart.RedirectStandardError = $true
            $initializerStart.UseShellExecute = $false
            $initializer = [System.Diagnostics.Process]::Start($initializerStart)
            $stdout = $initializer.StandardOutput.ReadToEndAsync()
            $stderr = $initializer.StandardError.ReadToEndAsync()
            $initializer.WaitForExit(180000) | Should -BeTrue -Because "the bounded lock wait must end -InitDb; the watchdog stopped it instead"
            $run = [PSCustomObject]@{ ExitCode = $initializer.ExitCode; Text = $stdout.Result + $stderr.Result }
        }
        finally {
            # Stop a still-waiting initializer before the lock is released, so it cannot go on to insert.
            if ($null -ne $initializer) {
                if (-not $initializer.HasExited) {
                    $initializer.Kill($true)
                    $null = $initializer.WaitForExit(30000)
                }
                $initializer.Dispose()
            }
            # Ending the holder's session releases its lock.
            $holder.StandardInput.Close()
            if (-not $holder.WaitForExit(30000)) { $holder.Kill($true) }
            $holder.Dispose()
        }

        $run.ExitCode | Should -Not -Be 0
        $run.Text | Should -BeLike "*timed out waiting for a database lock (SQLSTATE 55P03)*no signing key was changed*"
        $run.Text | Should -Not -BeLike "*cannot be decrypted*"
        $run.Text | Should -Not -BeLike "*check the database user's access*"
        @(Get-LiveKeyRows).Count | Should -Be 0 -Because "a timed-out creation must insert nothing"
        Assert-LiveOutputWithholdsStatement -Text $run.Text
    }
}

Describe "OpenIddict signing-key initialization against live SQL Server (DMS-1292)" -Skip:([string]::IsNullOrWhiteSpace($env:DMS_OPENIDDICT_KEY_LIVE_MSSQL_CONNECTION)) {
    BeforeAll {
        $script:LiveKey = "Live-Dms1292-Encryption'Key"
        $script:LiveWrongKey = "Live-Dms1292-Other-Key"

        function script:Invoke-LiveMssqlSql {
            param([string]$Sql, [switch]$Master)

            $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($env:DMS_OPENIDDICT_KEY_LIVE_MSSQL_CONNECTION)
            # .PSBase: PowerShell's dictionary adapter would otherwise treat these names as keywords.
            $builder.PSBase.InitialCatalog = if ($Master) { "master" } else { $script:LiveDb }
            $connection = [System.Data.SqlClient.SqlConnection]::new($builder.PSBase.ConnectionString)
            try {
                $connection.Open()
                $command = $connection.CreateCommand()
                $command.CommandText = $Sql
                $reader = $command.ExecuteReader()
                $rows = [System.Collections.Generic.List[string]]::new()
                while ($reader.Read()) { $rows.Add([string]$reader.GetValue(0)) }
                $reader.Dispose()
                return $rows.ToArray()
            }
            finally {
                $connection.Dispose()
            }
        }

        function script:Get-LiveKeyRows {
            Invoke-LiveMssqlSql -Sql @'
SELECT CONCAT(Id, '|', KeyId, '|', CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', PrivateKey), 2), '|',
    CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', PublicKey), 2), '|', IsActive, '|', CONVERT(VARCHAR(30), CreatedAt, 126))
FROM dmscs.OpenIddictKey ORDER BY Id;
'@
        }
    }

    BeforeEach {
        $script:LiveDb = "dms1292_keyinit_$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($env:DMS_OPENIDDICT_KEY_LIVE_MSSQL_CONNECTION)
        $builder.PSBase.InitialCatalog = $script:LiveDb
        $livePassword = $builder.PSBase.Password

        Push-Location $script:DockerComposePath
        try {
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType "MSSQL" -ConnectionString $builder.PSBase.ConnectionString `
                -DbName $script:LiveDb -DbPassword $livePassword -EncryptionKey $script:LiveKey
        }
        finally {
            Pop-Location
        }

        # -InitDb runs its DDL through sqlcmd in the Compose container; here the same statements go to
        # the live server over SqlClient. The key initialization keeps its own SqlClient path.
        Mock Invoke-DbQuery {
            $result = Invoke-LiveMssqlSql -Sql $Sql -Master:$UseMasterDatabase
            if ($result.Count -gt 0) { $result }
        }
    }

    AfterEach {
        [System.Data.SqlClient.SqlConnection]::ClearAllPools()
        Invoke-LiveMssqlSql -Master -Sql "IF DB_ID(N'$($script:LiveDb)') IS NOT NULL BEGIN ALTER DATABASE [$($script:LiveDb)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$($script:LiveDb)]; END" | Out-Null
    }

    It "creates one key that decrypts with the configured encryption key, then keeps it unchanged on rerun" {
        $first = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }
        $afterFirst = @(Get-LiveKeyRows)
        $second = Invoke-InitDbScripts 6>&1 | ForEach-Object { [string]$_ }

        $first | Should -Contain "OpenIddict signing key: created the initial active key"
        $afterFirst.Count | Should -Be 1
        $decryptable = Invoke-LiveMssqlSql -Sql "SELECT COUNT(*) FROM dmscs.OpenIddictKey WHERE DECRYPTBYPASSPHRASE(N'$($script:LiveKey.Replace("'", "''"))', PrivateKey) IS NOT NULL;"
        $decryptable | Should -Be "1"
        $second | Should -Contain "OpenIddict signing key: reusing the existing active key"
        @(Get-LiveKeyRows) | Should -Be $afterFirst -Because "a rerun must not insert, update or deactivate any key"
    }

    It "stops with the encryption-key error and changes no key when the configured key does not decrypt the active key" {
        Invoke-InitDbScripts 6>$null
        $before = @(Get-LiveKeyRows)
        # The -EncryptionKey value the dot-sourced setup-openiddict.ps1 reads at call time.
        Set-Variable -Name EncryptionKey -Value $script:LiveWrongKey

        { Invoke-InitDbScripts 6>$null } | Should -Throw "*cannot be decrypted with the configured encryption key; no signing key was changed*"
        @(Get-LiveKeyRows) | Should -Be $before
    }
}
