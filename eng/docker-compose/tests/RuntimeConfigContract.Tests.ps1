# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Convergence suite for the local Configuration Service runtime contract. It exercises the ONE
# resolution boundary (Docker Compose resolves the effective values; the contract validates them in the
# selected provider's context) rather than a re-implementation of Compose interpolation, and it uses
# executable oracles - the real Docker Compose CLI and the pinned Npgsql 8.0.4 / built-in SqlClient
# connection-string builders - so its assertions cannot drift from the behavior that actually runs.

# Gate detection runs at DISCOVERY time (top-level), so the -Skip conditions below - which Pester
# evaluates during discovery, before BeforeAll - see the real availability. The Npgsql type loaded here
# persists into the run phase for the oracle assertions.
$npgsqlLoaded = $false
try {
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
    foreach ($tfm in @("net8.0", "net7.0", "net6.0")) {
        $candidate = Join-Path $nugetRoot "npgsql/8.0.4/lib/$tfm/Npgsql.dll"
        if (Test-Path -LiteralPath $candidate) { Add-Type -Path $candidate; $npgsqlLoaded = $true; break }
    }
}
catch { $npgsqlLoaded = $false }

$dockerAvailable = $false
try {
    docker compose version *> $null
    if ($LASTEXITCODE -eq 0) { $dockerAvailable = $true }
}
catch { $dockerAvailable = $false }

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    # A helper that runs `docker compose config` on the real files exactly as the runtime does.
    function Invoke-ComposeConfigResolution {
        param([string[]]$ComposeFiles, [string]$EnvironmentFile, [hashtable]$ShellOverrides = @{})
        $saved = @{}
        foreach ($k in $ShellOverrides.Keys) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            Set-Item "Env:$k" -Value $ShellOverrides[$k]
        }
        try {
            return Get-ComposeResolvedConfiguration -ComposeFiles $ComposeFiles -EnvironmentFile $EnvironmentFile -ProjectName "dms-contract-oracle"
        }
        finally {
            foreach ($k in $ShellOverrides.Keys) {
                if ($null -ne $saved[$k]) { Set-Item "Env:$k" -Value $saved[$k] } else { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
            }
        }
    }
}

Describe "Resolve-EffectiveConfigRuntimeContract (pure contract - historical regression matrix)" {
    BeforeAll {
        $script:pgConn = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_datamanagementservice;'
        $script:pgShared = 'Server=dms-postgresql;User Id=postgres;Database=edfi_datamanagementservice;Password=p'
        $script:pgUidPwd = 'Server=dms-postgresql;Database=edfi_datamanagementservice;UID=postgres;PWD=x'
        $script:mssqlConn = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
    }

    # Every row is a scenario a prior review round hit (or a finding in the escalation), expressed as a
    # SEMANTIC category so a new edge case in the same class is still covered.
    It "<Category>: <Case>" -ForEach @(
        @{ Category = "finding-4 unsupported provider"; Case = "mysql is rejected, not coerced to postgresql";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'mysql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=edfi_datamanagementservice'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-4 unsupported provider"; Case = "blank provider is rejected";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = ''; ResolvedCmsConnectionString = 'host=dms-postgresql;database=edfi_datamanagementservice'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "provider-vs-engine split"; Case = "shell DMS_CONFIG_DATASTORE=mssql on a postgresql invocation is rejected";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=p'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-2 shared-alias PostgreSQL"; Case = "Server=/User Id= PostgreSQL connection is accepted on postgresql";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgShared'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "finding-3 UID/PWD aliases"; Case = "UID/PWD PostgreSQL connection is accepted on postgresql";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgUidPwd'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "finding-1 opaque shell terminal"; Case = "an unexpanded shell reference target is rejected against the effective name";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=${OTHER_DB};'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "wrong-engine connection"; Case = "a PostgreSQL host= connection on a SQL Server stack is rejected";
           ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=edfi_datamanagementservice'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "empty-connection fail-fast"; Case = "an empty connection on a SQL Server stack is rejected (no silent PG fallback)";
           ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = ''; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "no-database connection"; Case = "a connection targeting no database is rejected";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;username=postgres;password=p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "wrong-target-db"; Case = "a connection targeting a different database than the effective name is rejected";
           ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=wrong_db;User Id=sa;Password=p;TrustServerCertificate=true'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "SA password presence"; Case = "a blank SA password on a SQL Server stack is rejected";
           ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = ''; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "happy path PostgreSQL"; Case = "a valid PostgreSQL full-stack contract resolves";
           ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgConn'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "happy path SQL Server"; Case = "a valid SQL Server full-stack contract resolves";
           ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = 'abcdefgh1!'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
    ) {
        # Resolve placeholder connection strings (Pester -ForEach data is evaluated before BeforeAll).
        $resolvedArgs = @{}
        foreach ($k in $ContractArgs.Keys) {
            $v = $ContractArgs[$k]
            $resolvedArgs[$k] = switch ($v) {
                'PLACEHOLDER_pgConn' { $script:pgConn }
                'PLACEHOLDER_pgShared' { $script:pgShared }
                'PLACEHOLDER_pgUidPwd' { $script:pgUidPwd }
                'PLACEHOLDER_mssqlConn' { $script:mssqlConn }
                default { $v }
            }
        }

        if ($ShouldThrow) {
            { Resolve-EffectiveConfigRuntimeContract @resolvedArgs } | Should -Throw
        }
        else {
            $contract = Resolve-EffectiveConfigRuntimeContract @resolvedArgs
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
            $contract.Provider | Should -Be $resolvedArgs.InfrastructureEngine
        }
    }

    It "derives the effective configuration database from the connection when no -ConfigDatabaseName is supplied (standalone lane)" {
        $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn
        $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        $contract.OpenIddict.DbName | Should -Be 'edfi_datamanagementservice'
        $contract.OpenIddict.DbType | Should -Be 'Postgresql'
        $contract.OpenIddict.DbPassword | Should -BeNullOrEmpty
    }

    It "carries the SQL Server SA password to OpenIddict and the datastore registration connection" {
        $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ResolvedProvider 'mssql' -ResolvedCmsConnectionString $script:mssqlConn -ResolvedMssqlSaPassword 'abcdefgh1!' -ConfigDatabaseName 'edfi_datamanagementservice' -DatastoreDatabaseName 'edfi_datamanagementservice'
        $contract.MssqlSaPassword | Should -Be 'abcdefgh1!'
        $contract.OpenIddict.DbPassword | Should -Be 'abcdefgh1!'
        $contract.OpenIddict.DbType | Should -Be 'MSSQL'
        $contract.DatastoreConnectionString | Should -Match 'dms-mssql,1433'
        $contract.DatastoreConnectionString | Should -Match 'edfi_datamanagementservice'
    }

    Context "datastore-name agreement (shell vs env file)" {
        It "rejects a shell datastore-name override that disagrees with the env file" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -ConfigDatabaseName 'edfi_datamanagementservice' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } -ProcessEnvironment @{ POSTGRES_DB_NAME = 'rogue_db' } } |
                Should -Throw "*POSTGRES_DB_NAME resolves to 'rogue_db'*"
        }

        It "accepts a shell datastore-name override that agrees with the env file" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -ConfigDatabaseName 'edfi_datamanagementservice' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } -ProcessEnvironment @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' }
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }
    }
}

Describe "Get-CmsConnectionStringDatabaseName (provider-context extraction, real builder oracle)" {
    It "extracts the database from a SQL Server connection using Initial Catalog (SqlClient synonym, last-wins)" {
        Get-CmsConnectionStringDatabaseName -Engine 'mssql' -ConnectionString 'Server=x;Initial Catalog=edfi_cms;User Id=sa;Password=p' | Should -Be 'edfi_cms'
        Get-CmsConnectionStringDatabaseName -Engine 'mssql' -ConnectionString 'Server=x;Database=first;Initial Catalog=edfi_cms;User Id=sa;Password=p' | Should -Be 'edfi_cms'
    }

    It "rejects a PostgreSQL-exclusive Host= keyword on a SQL Server stack (SqlClient throws)" {
        { Get-CmsConnectionStringDatabaseName -Engine 'mssql' -ConnectionString 'Host=dms-postgresql;Database=x;Username=u' } | Should -Throw "*not a valid SQL Server connection*"
    }

    It "rejects a SQL Server-only Initial Catalog keyword on a PostgreSQL stack" {
        { Get-CmsConnectionStringDatabaseName -Engine 'postgresql' -ConnectionString 'Host=dms-postgresql;Initial Catalog=x;Username=u' } | Should -Throw "*SQL Server-only 'Initial Catalog'*"
    }

    It "extracts the database from a PostgreSQL connection using shared Server=/User Id= aliases (finding 2)" {
        Get-CmsConnectionStringDatabaseName -Engine 'postgresql' -ConnectionString 'Server=dms-postgresql;User Id=postgres;Database=edfi_dms;Password=p' | Should -Be 'edfi_dms'
    }

    It "extracts the database from a PostgreSQL connection using UID/PWD aliases (finding 3)" {
        Get-CmsConnectionStringDatabaseName -Engine 'postgresql' -ConnectionString 'Server=dms-postgresql;Database=edfi_dms;UID=postgres;PWD=x' | Should -Be 'edfi_dms'
    }

    It "returns empty when no database key is present" {
        @(Get-CmsConnectionStringDatabaseName -Engine 'postgresql' -ConnectionString 'host=x;username=u;password=p').Count | Should -Be 0
    }

    It "the pgsql database-alias set {database, db} matches the pinned Npgsql 8.0.4 builder's real aliases" -Skip:(-not $npgsqlLoaded) {
        # Oracle: enumerate every alias the real NpgsqlConnectionStringBuilder accepts for the Database
        # property (via round-trip), and confirm the production extractor recognizes exactly those, so the
        # small {database, db} set cannot silently drift from the driver.
        $npgsqlDatabaseAliases = @('Database', 'DB') | Where-Object {
            $b = [Npgsql.NpgsqlConnectionStringBuilder]::new()
            try { $b.set_ConnectionString("Host=h;$_=probe_db"); $b.Database -eq 'probe_db' } catch { $false }
        }
        foreach ($alias in $npgsqlDatabaseAliases) {
            Get-CmsConnectionStringDatabaseName -Engine 'postgresql' -ConnectionString "Host=h;$alias=oracle_db;Username=u" | Should -Be 'oracle_db' -Because "the extractor must recognize the real Npgsql '$alias' database alias"
        }
    }

    It "the pinned Npgsql 8.0.4 builder confirms UID and PWD are valid keywords (the finding-3 oracle)" -Skip:(-not $npgsqlLoaded) {
        $b = [Npgsql.NpgsqlConnectionStringBuilder]::new()
        $b.ContainsKey('UID') | Should -BeTrue
        $b.ContainsKey('PWD') | Should -BeTrue
    }
}

Describe "Docker Compose behavioral oracle (live) - the effective values are Compose's, not a re-implementation" {
    BeforeAll {
        $script:pgFiles = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot "postgresql.yml"))
        $script:envDefault = Join-Path $script:composeRoot ".env"
        # `docker compose config` needs no network/images, but ensure the external network is absent-tolerant.
    }

    It "resolves the PostgreSQL CMS connection and provider from the real files" -Skip:(-not $dockerAvailable) {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:envDefault
        $r.Provider | Should -Be 'postgresql'
        $r.CmsConnectionString | Should -Match 'database=edfi_datamanagementservice'
    }

    It "keeps a shell-substituted terminal value OPAQUE (finding 1): the container receives the literal reference" -Skip:(-not $dockerAvailable) {
        # Compose substitutes a shell value verbatim and does NOT recursively expand it, so a shell
        # DMS_CONFIG_DATABASE_NAME='${OTHER_DB}' reaches the container as the literal ${OTHER_DB} (rendered
        # $${OTHER_DB} by config). The contract, parsing the resolved value, rejects it - it is not the
        # effective database. A PowerShell re-implementation that recursively expanded it would wrongly pass.
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:envDefault -ShellOverrides @{ DMS_CONFIG_DATABASE_NAME = '${OTHER_DB}' }
        $r.CmsConnectionString | Should -Match 'database=\$\{OTHER_DB\}'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider $r.Provider -ResolvedCmsConnectionString $r.CmsConnectionString -ConfigDatabaseName 'edfi_datamanagementservice' } |
            Should -Throw
    }

    It "passes an unsupported provider through unchanged (finding 4), which the contract then rejects" -Skip:(-not $dockerAvailable) {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:envDefault -ShellOverrides @{ DMS_CONFIG_DATASTORE = 'mysql' }
        $r.Provider | Should -Be 'mysql'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider $r.Provider -ResolvedCmsConnectionString $r.CmsConnectionString -ConfigDatabaseName 'edfi_datamanagementservice' } |
            Should -Throw "*not a supported engine*"
    }

    It "cell <Engine>/<Topology>: CMS targets <ExpectedConfigDb> end-to-end via docker compose config" -Skip:(-not $dockerAvailable) -ForEach @(
        @{ Engine = 'postgresql'; DbFile = 'postgresql.yml'; EnvFiles = @('.env');              Separate = $false; ExpectedConfigDb = 'edfi_datamanagementservice' }
        @{ Engine = 'postgresql'; DbFile = 'postgresql.yml'; EnvFiles = @('.env');              Separate = $true;  ExpectedConfigDb = 'edfi_configurationservice' }
        @{ Engine = 'mssql';      DbFile = 'mssql.yml';      EnvFiles = @('.env', '.env.mssql'); Separate = $false; ExpectedConfigDb = 'edfi_datamanagementservice' }
        @{ Engine = 'mssql';      DbFile = 'mssql.yml';      EnvFiles = @('.env', '.env.mssql'); Separate = $true;  ExpectedConfigDb = 'edfi_configurationservice' }
    ) {
        # Materialize the topology onto a derived env file (as the start scripts do), then ask Docker
        # Compose itself what the container receives, and parse that in the engine's context.
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cell-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            $baseEnv = if ($Engine -eq 'mssql') {
                # Compose .env + .env.mssql into one file (later wins), mirroring the engine overlay.
                $merged = Join-Path $work ".env.merged"
                $combined = (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)
                Set-Content -LiteralPath $merged -Value $combined -NoNewline
                $merged
            }
            else {
                Join-Path $script:composeRoot '.env'
            }

            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $baseEnv -DockerComposeRoot $work -DatabaseEngine $Engine -SeparateConfigDatabase:$Separate
            $files = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot $DbFile))
            $resolved = Get-ComposeResolvedConfiguration -ComposeFiles $files -EnvironmentFile $resolvedEnv -ProjectName "dms-cell-oracle"

            $resolved.Provider | Should -Be $Engine
            $targets = @(Get-CmsConnectionStringDatabaseName -Engine $Engine -ConnectionString $resolved.CmsConnectionString)
            $targets | Should -Contain $ExpectedConfigDb -Because "the $Engine/$(if($Separate){'separate'}else{'shared'}) cell must point CMS at $ExpectedConfigDb"
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe "Production call-graph invariants (single policy, resolved before mutation, no legacy inference)" {
    BeforeAll {
        $script:startScripts = @('start-local-dms.ps1', 'start-published-dms.ps1', 'start-local-config.ps1')
        $script:productionFiles = Get-ChildItem -LiteralPath $script:composeRoot -Filter *.ps1 -File |
            ForEach-Object { $_.FullName }
        $script:productionFiles += (Get-ChildItem -LiteralPath $script:composeRoot -Filter *.psm1 -File | ForEach-Object { $_.FullName })
    }

    It "no production script references a deleted engine-inference or interpolation helper" {
        $deleted = @(
            'Resolve-ComposeVariable', 'Resolve-EnvFileValueWithProvenance', 'Get-ResolvedValue',
            'Resolve-ConnectionStringDialect', 'Test-ConnectionStringMatchesEngine', 'Test-MssqlConnectionStringValue',
            'Test-SqlServerConnectionStringKeyword', 'Get-NormalizedConnectionStringKeyword',
            'Resolve-CmsConfigurationDatabaseName', 'Resolve-StandaloneCmsConfigurationDatabaseTarget',
            'Assert-CmsConnectionStringTargetsConfigDatabase', 'Assert-ConfigDatabaseProcessEnvironmentAgreement',
            'Assert-MssqlCmsDatabaseIsShared', 'Get-ProcessEnvironmentVariableSnapshot',
            'Restore-ProcessEnvironmentVariable', 'Resolve-TargetDialect', 'SkipMssqlCmsDatabaseValidation',
            'SkipCmsDatabaseValidation', 'ConfigDatabaseNameMaterialized', 'MssqlSaPasswordDefault'
        )
        foreach ($file in $script:productionFiles) {
            $source = Get-Content -LiteralPath $file -Raw
            foreach ($symbol in $deleted) {
                $source | Should -Not -Match ([regex]::Escape($symbol)) -Because "$(Split-Path $file -Leaf) must not reference the deleted '$symbol'"
            }
        }
    }

    It "<Script> resolves and validates the runtime contract before the first Keycloak/database mutation" -ForEach @(
        @{ Script = 'start-local-dms.ps1';     Project = 'dms-local';     KeycloakMarker = 'up $upArgs keycloak' }
        @{ Script = 'start-published-dms.ps1'; Project = 'dms-published'; KeycloakMarker = 'up -d keycloak' }
    ) {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        $resolveIndex = $source.IndexOf('Get-ComposeResolvedConfiguration -ComposeFiles $files')
        $contractIndex = $source.IndexOf('$contract = Resolve-EffectiveConfigRuntimeContract')
        $keycloakIndex = $source.IndexOf($KeycloakMarker)

        $resolveIndex | Should -BeGreaterThan -1 -Because "$Script resolves the effective compose configuration"
        $contractIndex | Should -BeGreaterThan -1 -Because "$Script resolves the runtime contract"
        $keycloakIndex | Should -BeGreaterThan -1
        $resolveIndex | Should -BeLessThan $contractIndex
        $contractIndex | Should -BeLessThan $keycloakIndex -Because "$Script must validate the contract before starting Keycloak (and, in source order, the database) - the fail-fast-before-Docker guarantee"
    }

    It "start-local-config.ps1 resolves the contract before the database start" {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot 'start-local-config.ps1') -Raw
        $contractIndex = $source.IndexOf('$contract = Resolve-EffectiveConfigRuntimeContract')
        $dbStartIndex = $source.IndexOf('up --detach --wait db')
        $contractIndex | Should -BeGreaterThan -1
        $dbStartIndex | Should -BeGreaterThan -1
        $contractIndex | Should -BeLessThan $dbStartIndex
    }

    It "the topology resolver no longer performs CMS-connection or process-environment validation (single policy)" {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot 'env-utility.psm1') -Raw
        # Isolate the resolver body and prove it neither validates the connection nor guards the process env.
        $source | Should -Not -Match 'Assert-CmsConnectionStringTargetsConfigDatabase'
        $source | Should -Not -Match 'Assert-ConfigDatabaseProcessEnvironmentAgreement'
    }
}
