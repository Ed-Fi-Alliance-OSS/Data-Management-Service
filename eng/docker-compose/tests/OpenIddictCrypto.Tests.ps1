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
        # A configured DMS_CONFIG_IDENTITY_ENCRYPTION_KEY may legitimately contain a single quote.
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

            Push-Location $script:DockerComposePath
            try {
                . ./setup-openiddict.ps1 -InsertData -EnvironmentFile "" -DbType "Postgresql" `
                    -ConnectionString "Host=localhost;Port=5432;Database=edfi_configurationservice;Username=postgres;" `
                    -PostgresContainerName "dms-postgresql-test" -HashIterations "1000" `
                    -NewClientSecret $script:HostileSecret @Parameter | Out-Null
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
    }

    It "escapes a configured client id containing a single quote in both statements that carry it" {
        # CONFIG_SERVICE_CLIENT_ID is a supported override. Pasted between bare quotes it closes its
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
        # CONFIG_SERVICE_CLIENT_SCOPE is a supported override as well, and it reaches three separate
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

Describe "Northridge PostgreSQL restore recipe identity handoff" {
    BeforeAll {
        $readmePath = Join-Path $script:DockerComposePath "../northridge/README.md"
        $readme = Get-Content -Raw -Path $readmePath
        $recipeMatch = [regex]::Match($readme, '(?ms)^```shell\r?\n(?<recipe>.*?)^```')
        $script:NorthridgeRecipe = $recipeMatch.Groups["recipe"].Value
        $script:NorthridgeActiveRecipe = (($script:NorthridgeRecipe -split "`r?`n") |
            Where-Object { $_ -notmatch '^\s*#' }) -join "`n"

        $script:SetupOpenIddictInvocation = {
            $lines = $script:NorthridgeRecipe -split "`r?`n"
            $start = [array]::FindIndex($lines, [Predicate[string]] {
                    param($line)
                    $line -match '^\s*pwsh -NoProfile -File \./setup-openiddict\.ps1 -InsertData'
                })
            $start | Should -BeGreaterOrEqual 0

            $invocationLines = [System.Collections.Generic.List[string]]::new()
            for ($i = $start; $i -lt $lines.Count; $i++) {
                $invocationLines.Add($lines[$i])
                if ($lines[$i] -notmatch '\\\s*$') { break }
            }

            return ($invocationLines -join "`n")
        }
    }

    It "registers restore-admin with the live CMS identity secret and validation bounds" {
        $script:NorthridgeActiveRecipe | Should -Match 'CMSENV=\$\(docker inspect .*ed-fi-api-config-service\)'
        $script:NorthridgeActiveRecipe | Should -Match 'ADMIN_SECRET=\$\(echo "\$CMSENV" \| sed -n ''s/\^IdentitySettings__ClientSecret=//p''\)'
        $script:NorthridgeActiveRecipe | Should -Match 'CLIENT_SECRET_MIN=\$\(echo "\$CMSENV" \| sed -n ''s/\^IdentitySettings__ClientSecretValidation__MinimumLength=//p''\)'
        $script:NorthridgeActiveRecipe | Should -Match 'CLIENT_SECRET_MAX=\$\(echo "\$CMSENV" \| sed -n ''s/\^IdentitySettings__ClientSecretValidation__MaximumLength=//p''\)'
        $script:NorthridgeActiveRecipe | Should -Match '--data-urlencode "ClientSecret=\$ADMIN_SECRET"'
        $script:NorthridgeActiveRecipe | Should -Match '--data-urlencode "client_secret=\$ADMIN_SECRET"'
        $script:NorthridgeActiveRecipe | Should -Not -Match 'ValidClientSecret1234567890!Abcd'
    }

    It "passes the live PostgreSQL user, roles and client-secret bounds into setup-openiddict" {
        $invocation = & $script:SetupOpenIddictInvocation

        $invocation | Should -Match '-NewClientSecret "\$CSEC"'
        $invocation | Should -Match '-ConfigServiceRole "\$CMSROLE"'
        $invocation | Should -Match '-DmsClientRole "\$DMSROLE"'
        $invocation | Should -Match '-DbUser "\$DBUSER"'
        $invocation | Should -Match '-ClientSecretMinimumLength "\$CLIENT_SECRET_MIN"'
        $invocation | Should -Match '-ClientSecretMaximumLength "\$CLIENT_SECRET_MAX"'
    }
}
