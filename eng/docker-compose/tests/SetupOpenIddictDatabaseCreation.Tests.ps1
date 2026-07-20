# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe "Test-SafeDatabaseIdentifier" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            . ./setup-openiddict.ps1 -EnvironmentFile ""
        }
        finally {
            Pop-Location
        }
    }

    It "accepts the configuration-database names used by both topologies" {
        foreach ($name in @("edfi_configurationservice", "edfi_datamanagementservice", "district_local", "_leading_underscore")) {
            Test-SafeDatabaseIdentifier -Name $name | Should -BeTrue -Because "'$name' is a valid database name"
        }
    }

    It "accepts valid database names with hyphens, spaces, Unicode, a leading digit, or a period" {
        # These are valid PostgreSQL / SQL Server database names once quoted, and the Keycloak CMS
        # EnsureDatabase path creates them; the self-contained path must not reject the same selected
        # POSTGRES_DB_NAME / MSSQL_DB_NAME.
        foreach ($name in @("edfi-dms", "district local", "datestá", "配置数据库", "1datastore", "district.local")) {
            Test-SafeDatabaseIdentifier -Name $name | Should -BeTrue -Because "'$name' is a valid database name once quoted"
        }
    }

    It "rejects names carrying SQL metacharacters or control characters the local tooling cannot embed" {
        foreach ($name in @("bad;DROP DATABASE x", "quote'name", 'bracket]name', 'double"quote', 'dollar${x}', "paren(name)", "back\slash", "line`nbreak", "", "   ")) {
            Test-SafeDatabaseIdentifier -Name $name | Should -BeFalse -Because "'$name' cannot be carried safely through the interpolated SQL / ad-hoc connection string"
        }
    }
}

Describe "setup-openiddict.ps1 database-creation ownership (static contract)" {
    BeforeAll {
        $script:source = Get-Content -LiteralPath (
            Join-Path (Join-Path $PSScriptRoot "..") "setup-openiddict.ps1"
        ) -Raw
    }

    It "routes the maintenance-database switch to 'postgres' on PostgreSQL and 'master' on SQL Server" {
        # The guarded CREATE DATABASE path must connect to the always-present maintenance database
        # because the target configuration database may not exist yet. On PostgreSQL only that path
        # uses -tA; the normal path keeps psql's aligned output that Get-ScalarResult indexes.
        $script:source | Should -Match 'psql -U \$user -d postgres -tA -c \$Sql'
        $script:source | Should -Match "if \(\`$UseMaintenanceDatabase\) \{ 'master' \}"
    }

    It "renames the maintenance-database switch to the engine-neutral name" {
        $script:source | Should -Not -Match 'UseMasterDatabase' -Because "the switch is engine-neutral now (postgres/master)"
        $script:source | Should -Match '\[switch\]\$UseMaintenanceDatabase'
    }

    It "honors the SQL Server Initial Catalog alias when selecting the target database" {
        $script:source | Should -Match "elseif \(\`$params\['Database'\]\) \{ \`$params\['Database'\] \} else \{ \`$params\['Initial Catalog'\] \}"
    }

    It "keeps the guarded SQL Server database creation on the InitDb path" {
        $script:source | Should -Match "Invoke-DbQuery -UseMaintenanceDatabase `"IF DB_ID\(N'\`$dbName'\) IS NULL CREATE DATABASE \[\`$dbName\]"
    }

    It "adds a guarded, idempotent PostgreSQL database creation on the InitDb path" {
        # Validate the identifier, probe pg_database through the maintenance database, and create
        # only when absent (PostgreSQL has no CREATE DATABASE IF NOT EXISTS).
        $script:source | Should -Match 'Test-SafeDatabaseIdentifier -Name \$dbName'
        $script:source | Should -Match 'Invoke-DbQuery -UseMaintenanceDatabase "SELECT 1 FROM pg_database WHERE datname'
        $script:source | Should -Match 'Invoke-DbQuery -UseMaintenanceDatabase "CREATE DATABASE'
    }

    It "creates the PostgreSQL configuration database before creating the dmscs schema" {
        $createDatabaseIndex = $script:source.IndexOf('Invoke-DbQuery -UseMaintenanceDatabase "CREATE DATABASE')
        $createSchemaIndex = $script:source.IndexOf('CREATE SCHEMA IF NOT EXISTS "dmscs"')

        $createDatabaseIndex | Should -BeGreaterThan -1
        $createSchemaIndex | Should -BeGreaterThan $createDatabaseIndex
    }
}

Describe "postgresql-init.sh boundary (no CMS configuration-database creation)" {
    BeforeAll {
        $script:initScript = Get-Content -LiteralPath (
            Join-Path (Join-Path $PSScriptRoot "..") "postgresql-init.sh"
        ) -Raw
    }

    It "creates only the DMS datastore database, never a CMS configuration database" {
        # The container entrypoint runs only on a fresh volume; CMS EnsureDatabase and the guarded
        # setup-openiddict.ps1 path own configuration-database creation, so a second CREATE DATABASE
        # here would duplicate that ownership.
        ([regex]::Matches($script:initScript, 'CREATE DATABASE')).Count | Should -Be 1
        # The database name is the DMS datastore (POSTGRES_DB_NAME), supplied as a psql variable and
        # rendered by format - never a CMS configuration database.
        $script:initScript | Should -Match 'dbname=.*POSTGRES_DB_NAME'
        $script:initScript | Should -Not -Match 'edfi_configurationservice'
        $script:initScript | Should -Not -Match 'DMS_CONFIG_DATABASE_NAME'
    }

    It "creates the datastore database via format('%I') and never splices POSTGRES_DB_NAME into the SQL text" {
        # This path runs as the superuser on every fresh-volume init, before and independently of the
        # host-side identifier guard (which is not on this path at all). format('%I') safely quotes any
        # selected POSTGRES_DB_NAME - handling hyphens/spaces/periods/leading digits and doubling embedded
        # double quotes - so a crafted name cannot break out of the identifier and inject SQL. The name
        # must be passed as a psql variable (:'dbname'), never interpolated into the SQL string.
        $script:initScript | Should -Match "format\('CREATE DATABASE %I'"
        $script:initScript | Should -Match ":'dbname'"
        # Regression guard: the injectable raw forms - CREATE DATABASE ${POSTGRES_DB_NAME} or the merely
        # double-quoted CREATE DATABASE "${POSTGRES_DB_NAME}" - must never return.
        $script:initScript | Should -Not -Match 'CREATE DATABASE\s*\\?"?\$\{?POSTGRES_DB_NAME'
    }
}

Describe "start-published-dms.ps1 self-contained OpenIddict ordering" {
    BeforeAll {
        $script:publishedSource = Get-Content -LiteralPath (
            Join-Path (Join-Path $PSScriptRoot "..") "start-published-dms.ps1"
        ) -Raw
    }

    It "initializes the OpenIddict database and key store before starting the full published stack" {
        # Separate topology requires the dedicated configuration database to exist, and self-contained
        # CMS requires its signing key, before the Configuration Service container starts. The
        # full-stack path must therefore run -InitDb ahead of the whole-stack 'up', mirroring the
        # -InfraOnly path and the local start script.
        $fullStackStartIndex = $script:publishedSource.IndexOf('Write-Output "Starting published DMS"')
        $fullStackStartIndex | Should -BeGreaterThan -1

        # The -InitDb call immediately preceding the full-stack start (the last one in the file).
        $initDbIndex = $script:publishedSource.LastIndexOf('./setup-openiddict.ps1 -InitDb')
        $initDbIndex | Should -BeGreaterThan -1
        $initDbIndex | Should -BeLessThan $fullStackStartIndex -Because "the CMS database and key store must be created before the Configuration Service starts"

        # Client registration (-InsertData) still runs after the stack is up.
        $insertDataIndex = $script:publishedSource.IndexOf('-InsertData', $fullStackStartIndex)
        $insertDataIndex | Should -BeGreaterThan $fullStackStartIndex
    }
}

Describe "start-local-config.ps1 self-contained OpenIddict ordering" {
    BeforeAll {
        $script:localConfigSource = Get-Content -LiteralPath (
            Join-Path (Join-Path $PSScriptRoot "..") "start-local-config.ps1"
        ) -Raw
    }

    It "initializes the OpenIddict database and key store before starting the Configuration Service" {
        # The standalone Configuration Service stack must guarded-create the CMS database and signing
        # key before the config container starts, so CMS never relies on its own EnsureDatabase to
        # create a missing database. The database container comes up first, then -InitDb, then the
        # Configuration Service - mirroring start-local-dms.ps1 / start-published-dms.ps1.
        $dbStartIndex = $script:localConfigSource.IndexOf('up --detach --wait db')
        $configStartIndex = $script:localConfigSource.IndexOf('Write-Output "Starting locally-built DMS config service"')
        $initDbIndex = $script:localConfigSource.IndexOf('./setup-openiddict.ps1 -InitDb')

        $dbStartIndex | Should -BeGreaterThan -1
        $configStartIndex | Should -BeGreaterThan -1
        $initDbIndex | Should -BeGreaterThan -1

        $dbStartIndex | Should -BeLessThan $initDbIndex -Because "the database must be up before setup-openiddict.ps1 can create the configuration database"
        $initDbIndex | Should -BeLessThan $configStartIndex -Because "the CMS database and key store must exist before the Configuration Service starts"

        # Client registration (-InsertData) still runs after the Configuration Service is up.
        $insertDataIndex = $script:localConfigSource.IndexOf('-InsertData', $configStartIndex)
        $insertDataIndex | Should -BeGreaterThan $configStartIndex
    }

    It "resolves the effective runtime contract before starting the database (for both identity providers)" {
        # The standalone lane resolves one runtime contract that enforces engine agreement, the connection
        # engine/database invariant, and datastore-name agreement - for BOTH identity providers (Keycloak's
        # EnsureDatabase would otherwise silently create a shell-redirected database) - before the database
        # starts and before -InitDb.
        $contractIndex = $script:localConfigSource.IndexOf('Resolve-EffectiveConfigRuntimeContract')
        $dbStartIndex = $script:localConfigSource.IndexOf('Write-Output "Starting database..."')
        $initDbIndex = $script:localConfigSource.IndexOf('./setup-openiddict.ps1 -InitDb')

        $contractIndex | Should -BeGreaterThan -1 -Because "the standalone lane must resolve the runtime contract"
        $contractIndex | Should -BeLessThan $dbStartIndex -Because "the contract must fail fast before any docker action, for both identity providers"
        $contractIndex | Should -BeLessThan $initDbIndex -Because "the contract must be resolved before OpenIddict initialization"
    }

    It "resolves the runtime contract for both identity providers (not gated on self-contained)" {
        # Regression guard: agreement validation previously ran only inside the self-contained branch, so
        # Keycloak accepted shell overrides docker-compose applies over the env file (a shell
        # DMS_CONFIG_DATABASE_NAME=rogue_db redirects CMS; a wrong-engine shell connection reaches it). The
        # single runtime contract now runs for both providers. Assert via AST that the contract call has NO
        # enclosing if/elseif clause whose condition references $IdentityProvider.
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($script:localConfigSource, [ref]$null, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty -Because "start-local-config.ps1 must parse cleanly for the AST assertion to be meaningful"

        $contractCalls = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Resolve-EffectiveConfigRuntimeContract'
            },
            $true
        )
        $contractCalls.Count | Should -Be 1 -Because "the standalone lane resolves the contract exactly once, for both identity providers"

        $node = $contractCalls[0]
        while ($null -ne $node.Parent) {
            $node = $node.Parent
            if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                foreach ($clause in $node.Clauses) {
                    $clause.Item1.Extent.Text | Should -Not -Match 'IdentityProvider' -Because "the contract must not be nested in an identity-provider branch; it validates shell overrides for both self-contained and Keycloak before CMS boots"
                }
            }
        }
    }

    It "resolves the standalone contract against the RAW env file (never -ConfigDatabaseNameMaterialized)" {
        # The standalone lane hands docker-compose the RAW env file (DMS_CONFIG_DATABASE_NAME is not a
        # materialized literal), so the contract must model compose re-resolving the seam with shell
        # precedence. Passing -ConfigDatabaseNameMaterialized would pin the name and hide a shell
        # POSTGRES_DB_NAME override the connection routes through.
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($script:localConfigSource, [ref]$null, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty

        $contractCalls = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Resolve-EffectiveConfigRuntimeContract'
            },
            $true
        )
        $contractCalls.Count | Should -BeGreaterThan 0
        foreach ($contractCall in $contractCalls) {
            $materializedSwitch = $contractCall.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                $_.ParameterName -eq 'ConfigDatabaseNameMaterialized'
            }
            $materializedSwitch | Should -BeNullOrEmpty -Because "the standalone lane passes the RAW env file; pinning the name would hide a shell override the connection routes through"
        }
    }

    It "exports the materialized connection for both identity providers before the Configuration Service starts" {
        # On a SQL Server stack with no connection string the contract materializes a connection; the lane
        # exports it so docker-compose reads it (shell over --env-file), for both identity providers - an
        # index-only check would be fooled by the earlier OAuth/JWT self-contained block. Assert via AST that
        # the export assignment has NO enclosing if/elseif clause whose condition references $IdentityProvider.
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($script:localConfigSource, [ref]$null, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty -Because "start-local-config.ps1 must parse cleanly for the AST assertion to be meaningful"

        $exportAssignments = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -eq '$env:DMS_CONFIG_DATABASE_CONNECTION_STRING'
            },
            $true
        )
        $exportAssignments.Count | Should -Be 1 -Because "the standalone lane exports the materialized connection exactly once"

        # Its only enclosing condition is the Source='Materialized' guard, never an identity-provider branch.
        $node = $exportAssignments[0]
        while ($null -ne $node.Parent) {
            $node = $node.Parent
            if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                foreach ($clause in $node.Clauses) {
                    $clause.Item1.Extent.Text | Should -Not -Match 'IdentityProvider' -Because "the materialized export must run for both self-contained and Keycloak"
                }
            }
        }

        $exportIndex = $script:localConfigSource.IndexOf('$env:DMS_CONFIG_DATABASE_CONNECTION_STRING =')
        $configStartIndex = $script:localConfigSource.IndexOf('Write-Output "Starting locally-built DMS config service"')
        $exportIndex | Should -BeGreaterThan -1
        $exportIndex | Should -BeLessThan $configStartIndex -Because "the materialized connection must be exported before the Configuration Service container starts"
    }

    It "restores DMS_CONFIG_DATABASE_CONNECTION_STRING in a finally so the materialized export cannot leak into a later invocation" {
        # The materialized SQL Server connection is exported into the PROCESS environment (so docker-compose
        # reads it with shell precedence), which outlives the script. Left behind, a later invocation in the
        # same shell honors it as a caller-authored override - reusing the prior database, carrying a SQL
        # Server connection into a PostgreSQL run, or passing the database-name-only agreement guard when the
        # names match. Assert via AST that the export is snapshotted beforehand and restored in a finally on
        # every exit path (success, throw, teardown), so dropping the guard fails here rather than silently
        # restoring the cross-invocation leak.
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($script:localConfigSource, [ref]$null, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty -Because "start-local-config.ps1 must parse cleanly for the AST assertion to be meaningful"

        # The single process-environment export of the materialized connection string.
        $exportAssignments = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -eq '$env:DMS_CONFIG_DATABASE_CONNECTION_STRING'
            },
            $true
        )
        $exportAssignments.Count | Should -Be 1 -Because "the standalone lane exports the materialized connection string exactly once"

        # It must be enclosed by a try statement whose finally restores the variable.
        $node = $exportAssignments[0]
        $enclosingTry = $null
        while ($null -ne $node.Parent) {
            $node = $node.Parent
            if ($node -is [System.Management.Automation.Language.TryStatementAst]) { $enclosingTry = $node; break }
        }
        $enclosingTry | Should -Not -BeNullOrEmpty -Because "the materialized export must be inside a try whose finally restores it"
        $enclosingTry.Finally | Should -Not -BeNullOrEmpty -Because "a try without a finally would not restore the exported connection string on the throw or teardown path"

        $restoreCalls = $enclosingTry.Finally.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Restore-ProcessEnvironmentVariable'
            },
            $true
        )
        $restoreCalls.Count | Should -BeGreaterThan 0 -Because "the finally must restore DMS_CONFIG_DATABASE_CONNECTION_STRING to its pre-export state"

        # The snapshot must be captured BEFORE the try, or the finally would restore a value already
        # overwritten by the export.
        $snapshotIndex = $script:localConfigSource.IndexOf('Get-ProcessEnvironmentVariableSnapshot')
        $snapshotIndex | Should -BeGreaterThan -1 -Because "the lane must snapshot DMS_CONFIG_DATABASE_CONNECTION_STRING before exporting the materialized value"
        $snapshotIndex | Should -BeLessThan $enclosingTry.Extent.StartOffset -Because "the snapshot must be taken before the try, capturing the pre-export state"
    }

    It "sources every setup-openiddict.ps1 database parameter from the runtime contract" {
        # Regression guard: -InitDb (pre-CMS) and the -InsertData calls must authenticate with exactly the
        # engine, database, and SA credential Compose uses for CMS. Those all come from the one runtime
        # contract's OpenIddict target, splatted via @identityDbArgs (DbPassword only on the SQL Server path).
        $script:localConfigSource |
            Should -Match '\$identityDbArgs\.DbPassword = \$contract\.OpenIddict\.DbPassword'

        $openiddictCalls = [regex]::Matches($script:localConfigSource, '(?m)^.*\./setup-openiddict\.ps1 .*$')
        $openiddictCalls.Count | Should -BeGreaterThan 0
        foreach ($call in $openiddictCalls) {
            $call.Value | Should -Match '@identityDbArgs' -Because "every setup-openiddict.ps1 call takes its engine, database, and credential from the splatted @identityDbArgs"
        }
    }
}

Describe "Invoke-InitDbScripts PostgreSQL configuration-database creation (behavioral)" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            # Dot-source with a concrete, safe configuration-database name and a literal encryption
            # key so the create-decision logic runs without an env file or real crypto material. No
            # -InitDb/-InsertData switch, so only the functions are defined (no side effects at load).
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType Postgresql -DbName "edfi_configurationservice" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }
    }

    BeforeEach {
        # Neutralize OpenIddict key-material generation so each test exercises only the guarded
        # database-creation path, not crypto/key insertion.
        Mock New-OpenIddictKeyInsertSql { "SELECT 1;" }
    }

    It "probes existence through the maintenance database and creates the database when it is absent" {
        # Default mock: every query - including the pg_database probe - returns nothing, i.e. absent.
        Mock Invoke-DbQuery { }

        Invoke-InitDbScripts

        Should -Invoke Invoke-DbQuery -ParameterFilter {
            $Sql -match 'SELECT 1 FROM pg_database WHERE datname' -and $UseMaintenanceDatabase
        } -Because "existence must be probed via the always-present 'postgres' maintenance database"

        Should -Invoke Invoke-DbQuery -ParameterFilter {
            $Sql -match 'CREATE DATABASE' -and $UseMaintenanceDatabase
        } -Because "an absent configuration database must be created via the maintenance database"
    }

    It "does not re-create the database when the probe reports it already exists" {
        Mock Invoke-DbQuery { }
        # The pg_database probe returns a bare '1' -> database present.
        Mock Invoke-DbQuery -ParameterFilter { $Sql -match 'pg_database' } { "1" }

        Invoke-InitDbScripts

        Should -Invoke Invoke-DbQuery -Times 0 -Exactly -ParameterFilter {
            $Sql -match 'CREATE DATABASE'
        } -Because "PostgreSQL has no CREATE DATABASE IF NOT EXISTS; an existing database must be left untouched"
    }

    It "creates the dmscs schema against the configuration database, not the maintenance database" {
        Mock Invoke-DbQuery { }

        Invoke-InitDbScripts

        Should -Invoke Invoke-DbQuery -ParameterFilter {
            $Sql -match 'CREATE SCHEMA IF NOT EXISTS "dmscs"' -and -not $UseMaintenanceDatabase
        } -Because "schema creation runs inside the configuration database itself, not the maintenance database"
    }
}

Describe "Invoke-InitDbScripts PostgreSQL identifier guard (behavioral)" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            # Dot-source with a deliberately unsafe configuration-database name to exercise the
            # injection guard on the guarded CREATE DATABASE path.
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType Postgresql -DbName "cfg'; DROP DATABASE edfi; --" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }
    }

    It "refuses an unsafe database identifier before issuing any maintenance-database query" {
        Mock Invoke-DbQuery { }

        { Invoke-InitDbScripts } | Should -Throw "*not an accepted database name*"

        Should -Invoke Invoke-DbQuery -Times 0 -Exactly -Because "the identifier guard must block before any SQL is sent to the maintenance database"
    }
}

Describe "Invoke-InitDbScripts PostgreSQL accepts a hyphenated configuration-database name (behavioral)" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            # A hyphenated name is a valid quoted PostgreSQL database name that the old whitelist wrongly
            # rejected on the self-contained path while Keycloak's CMS EnsureDatabase created it.
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType Postgresql -DbName "edfi-dms" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }
    }

    BeforeEach {
        Mock New-OpenIddictKeyInsertSql { "SELECT 1;" }
    }

    It "creates the hyphenated database as a quoted identifier via the maintenance database" {
        Mock Invoke-DbQuery { }

        { Invoke-InitDbScripts } | Should -Not -Throw

        Should -Invoke Invoke-DbQuery -ParameterFilter {
            $Sql -match "SELECT 1 FROM pg_database WHERE datname = 'edfi-dms'" -and $UseMaintenanceDatabase
        } -Because "the existence probe must carry the exact selected name as a string literal"

        Should -Invoke Invoke-DbQuery -ParameterFilter {
            $Sql -match 'CREATE DATABASE "edfi-dms"' -and $UseMaintenanceDatabase
        } -Because "the hyphenated name must be created as a double-quoted identifier, not rejected"
    }
}

Describe "Invoke-InitDbScripts SQL Server configuration-database creation (behavioral)" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType MSSQL -DbName "edfi_configurationservice" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }
    }

    BeforeEach {
        # The SQL Server key insert uses a parameterized ADO.NET command; stub the command builder
        # and its executor so each test exercises only database creation.
        Mock New-OpenIddictKeyInsertCommand { [pscustomobject]@{ Sql = "SELECT 1;"; Parameters = [pscustomobject]@{} } }
        Mock Invoke-MssqlParameterizedQuery { }
    }

    It "issues a single guarded CREATE DATABASE through the master maintenance database" {
        Mock Invoke-DbQuery { }

        Invoke-InitDbScripts

        Should -Invoke Invoke-DbQuery -Times 1 -Exactly -ParameterFilter {
            $Sql -match "IF DB_ID\(N'edfi_configurationservice'\) IS NULL CREATE DATABASE \[edfi_configurationservice\]" -and $UseMaintenanceDatabase
        } -Because "SQL Server guards creation in one statement executed against the master maintenance database"
    }

    It "creates the dmscs schema against the configuration database, not the maintenance database" {
        Mock Invoke-DbQuery { }

        Invoke-InitDbScripts

        Should -Invoke Invoke-DbQuery -ParameterFilter {
            $Sql -match "CREATE SCHEMA dmscs" -and -not $UseMaintenanceDatabase
        } -Because "schema creation runs inside the configuration database itself, not the maintenance database"
    }
}

Describe "Invoke-InitDbScripts SQL Server identifier guard (behavioral)" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            # Dot-source with a name that breaks out of the bracket-quoted / N'...' SQL Server
            # CREATE DATABASE statement, to exercise the injection guard on that path.
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType MSSQL -DbName "x]; DROP DATABASE master; --" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }
    }

    It "refuses an unsafe database identifier before issuing any maintenance-database query" {
        Mock Invoke-DbQuery { }

        { Invoke-InitDbScripts } | Should -Throw "*not an accepted database name*"

        Should -Invoke Invoke-DbQuery -Times 0 -Exactly -Because "the identifier guard must block before any SQL is sent to the master maintenance database"
    }
}

Describe "Invoke-InitDbScripts SQL Server accepts a hyphenated configuration-database name (behavioral)" {
    BeforeAll {
        $script:DockerComposePath = Resolve-Path (Join-Path $PSScriptRoot "..")
        Push-Location $script:DockerComposePath
        try {
            . ./setup-openiddict.ps1 -EnvironmentFile "" -DbType MSSQL -DbName "edfi-dms" -EncryptionKey "test-encryption-key"
        }
        finally {
            Pop-Location
        }
    }

    BeforeEach {
        Mock New-OpenIddictKeyInsertCommand { [pscustomobject]@{ Sql = "SELECT 1;"; Parameters = [pscustomobject]@{} } }
        Mock Invoke-MssqlParameterizedQuery { }
    }

    It "creates the hyphenated database as a bracket-quoted identifier via the master maintenance database" {
        Mock Invoke-DbQuery { }

        { Invoke-InitDbScripts } | Should -Not -Throw

        Should -Invoke Invoke-DbQuery -Times 1 -Exactly -ParameterFilter {
            $Sql -match "IF DB_ID\(N'edfi-dms'\) IS NULL CREATE DATABASE \[edfi-dms\]" -and $UseMaintenanceDatabase
        } -Because "the hyphenated name must be created as a bracket-quoted identifier, not rejected"
    }
}

Describe "Configuration-database creation owner routes by identity provider (start-script contract)" {
    # Acceptance criterion: creation happens through every supported identity-provider path, with the
    # guarded setup-openiddict.ps1 -InitDb owning the self-contained path and CMS EnsureDatabase owning
    # the Keycloak path. CMS EnsureDatabase is C# DbUp - exercised end-to-end by the Docker smoke
    # (Invoke-ConfigDatabaseTopologyMatrixSmoke.ps1 keycloak cells) - so the CI-runnable guarantee here
    # is the orchestration contract: every start script runs -InitDb ONLY under the self-contained
    # provider, so the Keycloak path defers creation to CMS EnsureDatabase. Verified structurally via
    # the AST (each -InitDb invocation is enclosed by an if ($IdentityProvider -eq "self-contained")),
    # not by brittle text matching.
    BeforeAll {
        $script:dockerComposeRoot = Join-Path $PSScriptRoot ".."
        $script:astWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-initdb-ast-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:astWork -Force | Out-Null

        # A clause condition counts as a self-contained guard only when it IS exactly the comparison
        # $IdentityProvider -eq "self-contained" - checked by operator and operands, not by text. It must
        # not merely CONTAIN that comparison: a compound condition such as
        # ($IdentityProvider -eq "self-contained" -or $IdentityProvider -eq "keycloak") is also true for
        # Keycloak and would run -InitDb on the Keycloak path, so any compound (-or/-and), a backwards
        # -ne guard, or a keycloak comparison must NOT qualify. The real start scripts use the bare
        # equality; a future refactor that broadens the guard is a deliberate change that must update
        # this contract, so exact-match is the fail-safe rule.
        function Test-SelfContainedEqualityCondition {
            param([Parameter(Mandatory)] $ConditionAst)

            # Unwrap the pipeline / command-expression / parenthesis wrappers around the if condition to
            # reach the underlying expression, rejecting anything that is not a single bare expression.
            $expression = $ConditionAst
            while ($true) {
                if ($expression -is [System.Management.Automation.Language.PipelineAst]) {
                    if ($expression.PipelineElements.Count -ne 1) { return $false }
                    $element = $expression.PipelineElements[0]
                    if ($element -isnot [System.Management.Automation.Language.CommandExpressionAst]) { return $false }
                    $expression = $element.Expression
                    continue
                }
                if ($expression -is [System.Management.Automation.Language.ParenExpressionAst]) {
                    $expression = $expression.Pipeline
                    continue
                }
                break
            }

            return (
                $expression -is [System.Management.Automation.Language.BinaryExpressionAst] -and
                $expression.Operator -eq [System.Management.Automation.Language.TokenKind]::Ieq -and
                $expression.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $expression.Left.VariablePath.UserPath -eq 'IdentityProvider' -and
                $expression.Right -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                $expression.Right.Value -eq 'self-contained'
            )
        }

        function Get-InitDbInvocationGuard {
            param([Parameter(Mandatory)] [string]$ScriptPath)

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)

            $initDbCommands = $ast.FindAll({
                    param($node)
                    if ($node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
                    $namesSetupOpenIddict = $node.CommandElements | Where-Object {
                        $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                        $_.Value -match 'setup-openiddict\.ps1$'
                    }
                    $hasInitDb = $node.CommandElements | Where-Object {
                        $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                        $_.ParameterName -eq 'InitDb'
                    }
                    return ($namesSetupOpenIddict -and $hasInitDb)
                }, $true)

            return @(
                foreach ($command in $initDbCommands) {
                    $selfContainedGuarded = $false
                    $ancestor = $command.Parent
                    while ($null -ne $ancestor) {
                        if ($ancestor -is [System.Management.Automation.Language.IfStatementAst]) {
                            foreach ($clause in $ancestor.Clauses) {
                                # The command must live inside THIS clause's own body (not the else
                                # branch, and not merely under an if whose first clause happens to test
                                # self-contained), and the clause condition must be the equality guard.
                                $body = $clause.Item2
                                $commandInThisBody =
                                    $command.Extent.StartOffset -ge $body.Extent.StartOffset -and
                                    $command.Extent.EndOffset -le $body.Extent.EndOffset
                                if ($commandInThisBody -and (Test-SelfContainedEqualityCondition -ConditionAst $clause.Item1)) {
                                    $selfContainedGuarded = $true
                                }
                            }
                        }
                        $ancestor = $ancestor.Parent
                    }
                    [pscustomobject]@{ Text = $command.Extent.Text; SelfContainedGuarded = $selfContainedGuarded }
                }
            )
        }
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:astWork) {
            Remove-Item -LiteralPath $script:astWork -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "<Script> runs setup-openiddict.ps1 -InitDb only under the self-contained provider (Keycloak defers to CMS EnsureDatabase)" -ForEach @(
        @{ Script = "start-local-dms.ps1" }
        @{ Script = "start-published-dms.ps1" }
        @{ Script = "start-local-config.ps1" }
    ) {
        $invocations = Get-InitDbInvocationGuard -ScriptPath (Join-Path $script:dockerComposeRoot $Script)

        $invocations.Count | Should -BeGreaterThan 0 -Because "$Script owns self-contained pre-CMS creation via setup-openiddict.ps1 -InitDb"
        foreach ($invocation in $invocations) {
            $invocation.SelfContainedGuarded | Should -BeTrue -Because "on the Keycloak path CMS EnsureDatabase owns creation, so `"$($invocation.Text)`" must run only when the identity provider is self-contained"
        }
    }

    # Controls proving the guard-walk is precise (not merely "some ancestor if mentions both tokens"),
    # so a future restructuring that moves -InitDb off the self-contained path is actually caught.
    It "<Name> is detected as <Expected>" -ForEach @(
        @{ Name = "an -eq self-contained guard"; Expected = $true;  Body = 'if ($IdentityProvider -eq "self-contained") { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
        @{ Name = "the else branch of a self-contained if"; Expected = $false; Body = 'if ($IdentityProvider -eq "self-contained") { Write-Output ok } else { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
        @{ Name = "a backwards -ne self-contained guard"; Expected = $false; Body = 'if ($IdentityProvider -ne "self-contained") { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
        @{ Name = "an unguarded top-level call"; Expected = $false; Body = './setup-openiddict.ps1 -InitDb -EnvironmentFile $e' }
        @{ Name = "a keycloak-branch call"; Expected = $false; Body = 'if ($IdentityProvider -eq "keycloak") { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
        @{ Name = "an -or compound that also matches keycloak"; Expected = $false; Body = 'if ($IdentityProvider -eq "self-contained" -or $IdentityProvider -eq "keycloak") { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
        @{ Name = "an -and compound around the self-contained equality"; Expected = $false; Body = 'if ($IdentityProvider -eq "self-contained" -and $r) { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
        @{ Name = "a parenthesized bare self-contained equality"; Expected = $true; Body = 'if (($IdentityProvider -eq "self-contained")) { ./setup-openiddict.ps1 -InitDb -EnvironmentFile $e }' }
    ) {
        $scriptPath = Join-Path $script:astWork "case-$([Guid]::NewGuid().ToString('N')).ps1"
        Set-Content -LiteralPath $scriptPath -Value $Body
        $invocations = Get-InitDbInvocationGuard -ScriptPath $scriptPath

        $invocations.Count | Should -Be 1 -Because "the synthetic case has exactly one -InitDb invocation"
        $invocations[0].SelfContainedGuarded | Should -Be $Expected
    }
}
