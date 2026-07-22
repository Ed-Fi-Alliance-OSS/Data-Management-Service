# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Convergence suite for the local Configuration Service runtime contract. It exercises the ONE resolution
# boundary - Docker Compose resolves the container-facing values (docker compose config); the exact runtime
# providers (Npgsql / Microsoft.Data.SqlClient, via the api-schema-tools `connection validate` verb) parse
# connection strings; the contract validates before any mutation - using EXECUTABLE ORACLES (the real
# Docker Compose CLI and the real provider builders), so its assertions cannot drift from what runs.
#
# There are NO availability -Skip gates: the connection-validator build (dotnet) and the compose oracle
# (docker) are HARD prerequisites. A missing prerequisite FAILS - a silent skip is the false confidence a
# prior round shipped.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    # Build the exact-provider connection validator (api-schema-tools). Fail, never skip, when the SDK or
    # the build is unavailable.
    $script:schemaProject = [System.IO.Path]::GetFullPath(
        (Join-Path $script:composeRoot "../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj")
    )
    if (-not (Test-Path -LiteralPath $script:schemaProject)) {
        throw "api-schema-tools project not found at '$script:schemaProject'; the connection-validator oracle cannot run."
    }
    & dotnet build $script:schemaProject -c Release --nologo 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build api-schema-tools (dotnet build exit $LASTEXITCODE); the connection-validator oracle must build, not skip."
    }
    $script:schemaTool = Get-ChildItem -Path (Join-Path (Split-Path $script:schemaProject) "bin/Release") -Recurse -File |
        Where-Object { $_.Name -eq "api-schema-tools.exe" -or $_.Name -eq "api-schema-tools" } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $script:schemaTool) {
        throw "api-schema-tools executable not found under bin/Release after build."
    }

    function Test-DockerComposeAvailable {
        try { docker compose version *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false }
    }

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

Describe "Get-CmsConnectionStringDatabaseName (exact-provider oracle via api-schema-tools)" {
    # These run the REAL Npgsql 8.0.4 / Microsoft.Data.SqlClient 6.1.4 builders through the verb, so the
    # semantic-equivalence classes are validated against runtime behavior, not a keyword vocabulary.
    It "<Case>" -ForEach @(
        @{ Case = "PG host= connection returns its database";                    Engine = "postgresql"; Conn = "host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi"; Expect = "edfi" }
        @{ Case = "PG shared Server=/User Id= aliases (finding 2 class)";         Engine = "postgresql"; Conn = "Server=dms-postgresql;User Id=postgres;Database=edfi;Password=p";        Expect = "edfi" }
        @{ Case = "PG UID/PWD aliases (finding 3 class)";                         Engine = "postgresql"; Conn = "Server=dms-postgresql;Database=edfi;UID=postgres;PWD=x";                   Expect = "edfi" }
        @{ Case = "PG Database/DB duplicate synonym is last-wins canonical";       Engine = "postgresql"; Conn = "Host=h;Database=first;DB=second";                                        Expect = "second" }
        @{ Case = "MSSQL Initial Catalog returns its database";                   Engine = "mssql";      Conn = "Server=dms-mssql,1433;Initial Catalog=edfi;User Id=sa;Password=p;TrustServerCertificate=true"; Expect = "edfi" }
        @{ Case = "MSSQL Database/Initial Catalog duplicate synonym is last-wins"; Engine = "mssql";      Conn = "Server=x;Database=first;Initial Catalog=second";                        Expect = "second" }
    ) {
        @(Get-CmsConnectionStringDatabaseName -Engine $Engine -ConnectionString $Conn -SchemaToolPath $script:schemaTool) | Should -Be @($Expect)
    }

    It "rejects <Case>" -ForEach @(
        @{ Case = "a SQL Server-only keyword (Data Source) under PostgreSQL";   Engine = "postgresql"; Conn = "Host=h;Data Source=x;Database=d" }
        @{ Case = "a SQL Server-only keyword (Encrypt) under PostgreSQL";       Engine = "postgresql"; Conn = "Host=h;Encrypt=true;Database=d" }
        @{ Case = "a SQL Server-only keyword (Integrated Security) under PostgreSQL"; Engine = "postgresql"; Conn = "Host=h;Integrated Security=true;Database=d" }
        @{ Case = "a PostgreSQL-only keyword (Host) under SQL Server";          Engine = "mssql";      Conn = "Host=dms-postgresql;Database=d" }
        @{ Case = "a PostgreSQL-only keyword (Username) under SQL Server";      Engine = "mssql";      Conn = "Server=x;Username=u;Database=d" }
    ) {
        { Get-CmsConnectionStringDatabaseName -Engine $Engine -ConnectionString $Conn -SchemaToolPath $script:schemaTool } | Should -Throw "*not a valid '$Engine' connection*"
    }

    It "returns empty when the connection targets no database" {
        @(Get-CmsConnectionStringDatabaseName -Engine "postgresql" -ConnectionString "host=h;username=u;password=p" -SchemaToolPath $script:schemaTool).Count | Should -Be 0
    }
}

Describe "Resolve-EffectiveConfigRuntimeContract (historical regression matrix, real parser)" {
    BeforeAll {
        $script:pgConn = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_datamanagementservice;'
        $script:pgShared = 'Server=dms-postgresql;User Id=postgres;Database=edfi_datamanagementservice;Password=p'
        $script:mssqlConn = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
    }

    It "<Category>: <Case>" -ForEach @(
        @{ Category = "finding-4 unsupported provider"; Case = "mysql rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'mysql'; ResolvedCmsConnectionString = 'host=h;database=edfi_datamanagementservice'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-4 unsupported provider"; Case = "blank provider rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = ''; ResolvedCmsConnectionString = 'host=h;database=edfi_datamanagementservice'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "provider-vs-engine split"; Case = "shell mssql provider on a postgresql run"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=p'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-2 shared-alias PostgreSQL"; Case = "Server=/User Id= accepted on postgresql"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgShared'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "finding-1 opaque shell terminal"; Case = "unexpanded reference target rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=${OTHER_DB};'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "wrong-engine connection"; Case = "PostgreSQL Host= on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=edfi_datamanagementservice'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "empty-connection fail-fast"; Case = "empty connection on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = ''; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "no-database connection"; Case = "connection targeting no database rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;username=postgres;password=p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "wrong-target-db"; Case = "connection to a different database rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=wrong_db;User Id=sa;Password=p;TrustServerCertificate=true'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "SA password presence"; Case = "blank SA password on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = ''; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "happy path PostgreSQL"; Case = "valid full-stack contract resolves"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgConn'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "happy path SQL Server"; Case = "valid full-stack contract resolves"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = 'abcdefgh1!'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
    ) {
        # This matrix exercises the CMS invariants in isolation, so DMS participation is off; the DMS
        # provider and datastore-name agreement have their own dedicated contexts below.
        $resolvedArgs = @{ SchemaToolPath = $script:schemaTool; ConfigServiceIncluded = $true; DmsServiceIncluded = $false }
        foreach ($k in $ContractArgs.Keys) {
            $v = $ContractArgs[$k]
            $resolvedArgs[$k] = switch ($v) {
                'PLACEHOLDER_pgConn' { $script:pgConn }
                'PLACEHOLDER_pgShared' { $script:pgShared }
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
            $contract.ConfigProvider | Should -Be $resolvedArgs.InfrastructureEngine
        }
    }

    It "derives the effective configuration database from the connection when no -ConfigDatabaseName is supplied (standalone lane)" {
        $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool
        $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        $contract.OpenIddict.DbName | Should -Be 'edfi_datamanagementservice'
    }

    Context "datastore-name agreement (container Compose-resolved vs host env-file)" {
        It "rejects a datastore database the containers receive that differs from the env-file value (direct or indirect override)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' -ResolvedDatastoreConnectionString 'host=dms-postgresql;username=postgres;database=rogue_database' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } } |
                Should -Throw "*datastore database the containers receive*rogue_database*"
        }

        It "accepts when the container datastore database matches the env-file value" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' -ResolvedDatastoreConnectionString 'host=dms-postgresql;username=postgres;database=edfi_datamanagementservice' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' }
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }
    }

    Context "provider-aware database identity at the contract boundary (finding 2, real parser)" {
        # The CMS-target and datastore-name comparisons route through the one provider-aware identity
        # policy (Test-DatabaseNameEquivalent): PostgreSQL is case-sensitive (a case-only difference is a
        # DIFFERENT physical database and must fail), SQL Server is case-insensitive (a case variant is the
        # SAME database and must be accepted). The connection strings carry a case-variant database name;
        # the exact provider builder preserves it, so the equality policy is what decides pass/fail.
        It "rejects a PostgreSQL CMS target that differs from the effective name only by case" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedCmsConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=p;database=EdFi_DataManagementService;' -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*EdFi_DataManagementService*"
        }

        It "accepts a SQL Server CMS target that differs from the effective name only by case" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -ResolvedConfigProvider 'mssql' -ResolvedCmsConnectionString 'Server=dms-mssql,1433;Database=EdFi_DataManagementService;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;' -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -ConfigDatabaseName 'edfi_datamanagementservice'
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "rejects a PostgreSQL container datastore that differs from the env-file value only by case" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString 'host=dms-postgresql;username=postgres;database=EdFi_DataManagementService' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } } |
                Should -Throw "*datastore database the containers receive*"
        }

        It "accepts a SQL Server container datastore that differs from the env-file value only by case" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -ResolvedDatastoreConnectionString 'Server=dms-mssql,1433;Database=EdFi_DataManagementService;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true' -EnvValues @{ MSSQL_DB_NAME = 'edfi_datamanagementservice' }
            $contract.DmsProvider | Should -Be 'mssql'
        }
    }

    Context "Configuration Service participation split (Keycloak without a local config service)" {
        # Blocker regression: a supported Keycloak start that omits the local config service (external
        # CONFIG_SERVICE_URL, or none) composes no config service, so Compose exposes no provider/connection.
        # The contract must SKIP the CMS invariants for that shape instead of throwing on the legitimately
        # absent values - while still enforcing the stack invariants, because the DMS datastore still starts.
        It "skips the CMS provider/connection/OpenIddict invariants when the config service does not participate" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool
            $contract.ConfigProvider | Should -BeNullOrEmpty
            $contract.CmsDatabaseName | Should -BeNullOrEmpty
            $contract.OpenIddict | Should -BeNullOrEmpty
        }

        It "does not reject an unsupported/absent CMS provider when the config service does not participate" {
            # With config participating this same 'mysql' provider is rejected (see the regression matrix);
            # without it, the CMS provider is never read.
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -ResolvedConfigProvider 'mysql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool } |
                Should -Not -Throw
        }

        It "still rejects a datastore-name mismatch when config is absent but the DMS service participates (published Keycloak)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedConfigProvider $null -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString 'host=dms-postgresql;username=postgres;database=rogue_database' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } } |
                Should -Throw "*datastore database the containers receive*rogue_database*"
        }

        It "still rejects a blank SQL Server SA password when neither the config nor the DMS service participates" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword '' } |
                Should -Throw "*MSSQL_SA_PASSWORD resolves to a blank value*"
        }

        It "still returns the SA password and SQL Server datastore registration connection when the config service does not participate" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -DatastoreDatabaseName 'edfi_datamanagementservice'
            $contract.MssqlSaPassword | Should -Be 'abcdefgh1!'
            $contract.DatastoreConnectionString | Should -Match 'edfi_datamanagementservice'
            $contract.OpenIddict | Should -BeNullOrEmpty
        }
    }

    Context "DMS runtime provider participation (independent of the CMS provider)" {
        # Blocker regression: the DMS service has its own AppSettings__Datastore interpolated from DMS_DATASTORE,
        # independently of the CMS provider. It must be validated against the selected engine on its own so a
        # shell DMS_DATASTORE cannot point the DMS container at a different engine than the one that starts.
        It "rejects DMS provider '<DmsProvider>' on a '<Engine>' stack" -ForEach @(
            @{ Engine = 'postgresql'; DmsProvider = 'mssql' }
            @{ Engine = 'mssql';      DmsProvider = 'postgresql' }
        ) {
            $saArgs = if ($Engine -eq 'mssql') { @{ ResolvedMssqlSaPassword = 'abcdefgh1!' } } else { @{} }
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedDmsProvider $DmsProvider -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool @saArgs } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }

        It "rejects an unsupported/blank/whitespace DMS provider '<Label>' when the DMS service participates" -ForEach @(
            @{ Label = 'mysql';      DmsProvider = 'mysql' }
            @{ Label = 'blank';      DmsProvider = '' }
            @{ Label = 'whitespace'; DmsProvider = ' postgresql ' }
        ) {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedDmsProvider $DmsProvider -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool } |
                Should -Throw "*DMS runtime provider*not a supported engine*"
        }

        It "accepts a DMS provider that matches the selected engine" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool
            $contract.DmsProvider | Should -Be 'postgresql'
        }

        It "accepts an absent DMS provider ONLY when the DMS service does not participate (standalone lane)" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider $null -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool
            $contract.DmsProvider | Should -BeNullOrEmpty
            $contract.ConfigProvider | Should -Be 'postgresql'
        }

        It "validates the DMS provider independently of the CMS provider: matching CMS + mismatched DMS is rejected" {
            # CMS provider matches the PostgreSQL engine, but the DMS provider is MSSQL - the reverse-mismatch
            # class the CMS-only check missed. The DMS invariant rejects it even though the CMS invariant passes.
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }
    }
}

Describe "Docker Compose behavioral oracle (live) - Compose is the authority" {
    BeforeAll {
        if (-not (Test-DockerComposeAvailable)) {
            throw "docker compose is required for the Compose oracle and must not be skipped; it is unavailable in this environment."
        }
        $script:pgFiles = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot "postgresql.yml"))
        $script:baseEnvFile = Join-Path $script:composeRoot ".env.example"
    }

    It "resolves the PostgreSQL CMS connection and provider from the real files" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:baseEnvFile
        $r.ConfigProvider | Should -Be 'postgresql'
        $r.CmsConnectionString | Should -Match 'database=edfi_datamanagementservice'
    }

    It "keeps a shell-substituted terminal OPAQUE (finding 1): the container receives the literal reference, which the contract rejects" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_CONFIG_DATABASE_NAME = '${OTHER_DB}' }
        $r.CmsConnectionString | Should -Match 'database=\$\{OTHER_DB\}'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' } | Should -Throw
    }

    It "passes an unsupported provider through unchanged (finding 4), which the contract rejects" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_CONFIG_DATASTORE = 'mysql' }
        $r.ConfigProvider | Should -Be 'mysql'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' } | Should -Throw "*not a supported engine*"
    }

    Context "datastore-name split through Compose (full-stack, real dms service)" {
        BeforeAll {
            $script:fullFiles = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
        }

        It "an INDIRECT shell override of a referenced datastore variable splits the container datastore from the env file (finding 2)" {
            # Env file: POSTGRES_DB_NAME=${DATASTORE_NAME}; DATASTORE_NAME=edfi...; the shell overrides only
            # DATASTORE_NAME (never POSTGRES_DB_NAME directly). SEPARATE topology makes the CMS database
            # independent (edfi_configurationservice), so this is caught ONLY by the datastore-name check
            # reading Compose's resolution of the DMS datastore admin connection - the exact class the old
            # direct-key guard missed. (In shared topology the same override is caught by the CMS-connection
            # check, since the config database routes through the same variable.)
            $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-ind-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            try {
                $envFile = Join-Path $work ".env.indirect"
                $baseEnv = Get-Content -LiteralPath $script:baseEnvFile -Raw
                # Route POSTGRES_DB_NAME through an indirect variable defined in the same file.
                $baseEnv = $baseEnv -replace '(?m)^POSTGRES_DB_NAME=.*$', "DATASTORE_NAME=edfi_datamanagementservice`nPOSTGRES_DB_NAME=`${DATASTORE_NAME}"
                Set-Content -LiteralPath $envFile -Value $baseEnv -NoNewline

                # Materialize the separate-topology config database (as the start scripts do).
                $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $envFile -DockerComposeRoot $work -DatabaseEngine "postgresql" -SeparateConfigDatabase
                $envValues = ReadValuesFromEnvFile $resolvedEnv
                $envValues['DMS_CONFIG_DATABASE_NAME'] | Should -Be 'edfi_configurationservice'

                $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:fullFiles -EnvironmentFile $resolvedEnv -ShellOverrides @{ DATASTORE_NAME = 'rogue_database' }
                $resolved.DatastoreConnectionString | Should -Match 'database=rogue_database'

                { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString $resolved.DatastoreConnectionString -ConfigDatabaseName $envValues['DMS_CONFIG_DATABASE_NAME'] -EnvValues $envValues } |
                    Should -Throw "*datastore database the containers receive*rogue_database*"
            }
            finally {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "DMS runtime provider resolves independently from the CMS provider (real Compose files)" {
        # Proves via the actual local/published DMS compose files that Compose interpolates DMS_DATASTORE
        # (dms AppSettings__Datastore) independently from DMS_CONFIG_DATASTORE (config AppSettings__Datastore),
        # and that the contract rejects a DMS-provider/engine split even when the CMS provider matches.
        BeforeAll {
            $script:localFullFiles = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            # A published Keycloak start without -EnableConfig composes no config service (external CMS).
            $script:publishedNoConfigFiles = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "published-dms.yml"))
        }

        It "Compose interpolates DMS_DATASTORE independently of DMS_CONFIG_DATASTORE" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:localFullFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_DATASTORE = 'mssql' }
            $r.DmsProvider | Should -Be 'mssql'
            $r.ConfigProvider | Should -Be 'postgresql'
        }

        It "rejects a shell DMS_DATASTORE=mssql on a PostgreSQL invocation even though the CMS provider matches" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:localFullFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_DATASTORE = 'mssql' }
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString $r.DatastoreConnectionString -ConfigDatabaseName 'edfi_datamanagementservice' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }

        It "rejects the DMS-provider mismatch on a published Keycloak start without a local config service" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:publishedNoConfigFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_DATASTORE = 'mssql' }
            $r.ConfigProvider | Should -BeNullOrEmpty
            $r.DmsProvider | Should -Be 'mssql'
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString $r.DatastoreConnectionString -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }

        It "rejects a shell DMS_DATASTORE=postgresql on a SQL Server invocation" {
            $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-dmsprov-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            try {
                $merged = Join-Path $work ".env.mssql.merged"
                ((Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.example') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)) | Set-Content -LiteralPath $merged -NoNewline
                $files = @("-f", (Join-Path $script:composeRoot "mssql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
                $r = Invoke-ComposeConfigResolution -ComposeFiles $files -EnvironmentFile $merged -ShellOverrides @{ DMS_DATASTORE = 'postgresql' }
                $r.DmsProvider | Should -Be 'postgresql'
                { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword $r.MssqlSaPassword -ResolvedDatastoreConnectionString $r.DatastoreConnectionString -ConfigDatabaseName 'edfi_datamanagementservice' -EnvValues @{ MSSQL_DB_NAME = 'edfi_datamanagementservice' } } |
                    Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
            }
            finally {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "accepts when the DMS and CMS providers both match the selected engine" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:localFullFiles -EnvironmentFile $script:baseEnvFile
            $r.DmsProvider | Should -Be 'postgresql'
            $r.ConfigProvider | Should -Be 'postgresql'
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString $r.DatastoreConnectionString -ConfigDatabaseName 'edfi_datamanagementservice' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' }
            $contract.DmsProvider | Should -Be 'postgresql'
            $contract.ConfigProvider | Should -Be 'postgresql'
        }
    }

    It "cell <Engine>/<Topology>: CMS targets <ExpectedConfigDb> end-to-end via docker compose config" -ForEach @(
        @{ Engine = 'postgresql'; DbFile = 'postgresql.yml'; EnvFiles = @('.env');              Separate = $false; ExpectedConfigDb = 'edfi_datamanagementservice' }
        @{ Engine = 'postgresql'; DbFile = 'postgresql.yml'; EnvFiles = @('.env');              Separate = $true;  ExpectedConfigDb = 'edfi_configurationservice' }
        @{ Engine = 'mssql';      DbFile = 'mssql.yml';      EnvFiles = @('.env', '.env.mssql'); Separate = $false; ExpectedConfigDb = 'edfi_datamanagementservice' }
        @{ Engine = 'mssql';      DbFile = 'mssql.yml';      EnvFiles = @('.env', '.env.mssql'); Separate = $true;  ExpectedConfigDb = 'edfi_configurationservice' }
    ) {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cell-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            $baseEnv = if ($Engine -eq 'mssql') {
                $merged = Join-Path $work ".env.merged"
                ((Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.example') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)) | Set-Content -LiteralPath $merged -NoNewline
                $merged
            }
            else { Join-Path $script:composeRoot '.env.example' }

            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $baseEnv -DockerComposeRoot $work -DatabaseEngine $Engine -SeparateConfigDatabase:$Separate
            $files = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot $DbFile))
            $resolved = Get-ComposeResolvedConfiguration -ComposeFiles $files -EnvironmentFile $resolvedEnv -ProjectName "dms-cell-oracle"

            $resolved.ConfigProvider | Should -Be $Engine
            @(Get-CmsConnectionStringDatabaseName -Engine $Engine -ConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool) | Should -Contain $ExpectedConfigDb
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe "Production call-graph invariants (single policy, before ALL mutation, no legacy inference)" {
    BeforeAll {
        $script:startScripts = @('start-local-dms.ps1', 'start-published-dms.ps1', 'start-local-config.ps1')
        $script:productionFiles = @(Get-ChildItem -LiteralPath $script:composeRoot -Filter *.ps1 -File | ForEach-Object { $_.FullName })
        $script:productionFiles += @(Get-ChildItem -LiteralPath $script:composeRoot -Filter *.psm1 -File | ForEach-Object { $_.FullName })
    }

    It "no production script references a deleted engine-inference / interpolation / provenance helper" {
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

    It "connection-string parsing goes through the exact-provider verb, never a non-runtime SqlClient" {
        $envUtility = Get-Content -LiteralPath (Join-Path $script:composeRoot "env-utility.psm1") -Raw
        $envUtility | Should -Match 'connection validate --engine' -Because "the single parser invokes the api-schema-tools verb"
        $envUtility | Should -Not -Match 'System\.Data\.SqlClient' -Because "the built-in SqlClient is not the runtime provider (the runtime is Microsoft.Data.SqlClient); parsing must go through the verb"
    }

    It "<Script> resolves and validates the runtime contract before the FIRST external mutation (network create, build, up, keycloak)" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
        @{ Script = 'start-local-config.ps1' }
    ) {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        $contractIndex = $source.IndexOf('$contract = Resolve-EffectiveConfigRuntimeContract')
        $networkIndex = $source.IndexOf('docker network create dms')

        $contractIndex | Should -BeGreaterThan -1 -Because "$Script resolves the runtime contract"
        $networkIndex | Should -BeGreaterThan -1
        $contractIndex | Should -BeLessThan $networkIndex -Because "$Script must validate before 'docker network create' - the first external action - which precedes image build, container up, and Keycloak/OpenIddict"
    }

    It "build-dms.ps1 StartEnvironment runs the runtime-contract preflight before image build and teardown (finding 1)" {
        # The inner start scripts validate before THEIR first mutation (above), but the outer StartEnvironment
        # orchestration builds images and tears down volumes BEFORE it ever reaches a start script. It must run
        # the SAME preflight (the start script's -PreflightOnly stop point) before either mutation, so an
        # invalid provider/connection string is reported before existing databases are deleted.
        $buildScript = [System.IO.Path]::GetFullPath((Join-Path $script:composeRoot "../../build-dms.ps1"))
        Test-Path -LiteralPath $buildScript | Should -BeTrue -Because "build-dms.ps1 lives at the repository root"
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$null, [ref]$null)
        $orchestrator = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Start-BootstrapDockerEnvironment'
            }, $true) | Select-Object -First 1
        $orchestrator | Should -Not -BeNullOrEmpty -Because "StartEnvironment dispatches to Start-BootstrapDockerEnvironment"

        $body = $orchestrator.Body.Extent.Text
        $preflightIndex = $body.IndexOf('-PreflightOnly')
        $dockerBuildIndex = $body.IndexOf('Invoke-Step { DockerBuild }')
        $stopIndex = $body.IndexOf('Stop-DockerEnvironment ')

        $preflightIndex | Should -BeGreaterThan -1 -Because "the orchestration must run the -PreflightOnly contract validation"
        $stopIndex | Should -BeGreaterThan -1 -Because "the orchestration tears down (compose down -v / volume deletion)"
        $dockerBuildIndex | Should -BeGreaterThan -1 -Because "the orchestration builds images"
        $preflightIndex | Should -BeLessThan $dockerBuildIndex -Because "the preflight must precede image build"
        $preflightIndex | Should -BeLessThan $stopIndex -Because "the preflight must precede teardown and volume deletion"
    }

    It "bootstrap-schema-tool imports bootstrap-manifest WITHOUT -Force (so importing it cannot re-home a caller's manifest)" {
        # A -Force reload of bootstrap-manifest from within bootstrap-schema-tool removes and re-imports it,
        # re-homing it out of a start script's session scope and breaking that script's env-snapshot restore
        # / startup-config functions. This is the root cause of the earlier teardown-test breakage; guard it.
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot 'bootstrap-schema-tool.psm1') -Raw
        $source | Should -Match 'Import-Module \(Join-Path \$PSScriptRoot "bootstrap-manifest\.psm1"\)'
        ([regex]::Matches($source, 'bootstrap-manifest\.psm1"\)\s*-Force')).Count | Should -Be 0
    }

    It "<Script> pins the exact caller participation matrix and resolves the contract exactly once (config=<Config>, dms=<Dms>)" -ForEach @(
        # The finite caller matrix. Each start script declares BOTH participation authorities explicitly from
        # its OWN compose-file selection - never inferred from null Compose fields - and the values are pinned
        # per caller so $true, $false, and $configServiceIncluded can never be swapped. In particular the
        # standalone CMS lane must stay -DmsServiceIncluded $false: its compose set legitimately has no dms
        # service, and flipping it to $true would break standalone startup - this guard fails that change
        # loudly. The always-include-config lanes declare -ConfigServiceIncluded $true as a constant; the
        # published lane routes it through the single $configServiceIncluded authority (bound to
        # published-config.yml inclusion by a separate test below).
        @{ Script = 'start-local-dms.ps1';     Config = '$true';                  Dms = '$true' }
        @{ Script = 'start-published-dms.ps1'; Config = '$configServiceIncluded'; Dms = '$true' }
        @{ Script = 'start-local-config.ps1';  Config = '$true';                  Dms = '$false' }
    ) {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        ([regex]::Matches($source, '\$contract = Resolve-EffectiveConfigRuntimeContract')).Count |
            Should -Be 1 -Because "$Script validates the runtime contract exactly once"
        # Exact-value match with a trailing word boundary: resistant to spacing, but $true / $false /
        # $configServiceIncluded are distinct tokens and cannot satisfy one another.
        $source | Should -Match ('-ConfigServiceIncluded\s+' + [regex]::Escape($Config) + '\b') -Because "$Script must pass -ConfigServiceIncluded $Config"
        $source | Should -Match ('-DmsServiceIncluded\s+' + [regex]::Escape($Dms) + '\b') -Because "$Script must pass -DmsServiceIncluded $Dms"
    }

    It "Get-ComposeResolvedConfiguration exposes distinct ConfigProvider and DmsProvider outputs (no ambiguous Provider)" {
        # Each service has ONE runtime authority: config AppSettings__Datastore -> ConfigProvider, dms
        # AppSettings__Datastore -> DmsProvider. There must be no bare 'Provider' field that could conflate them.
        $envUtility = Get-Content -LiteralPath (Join-Path $script:composeRoot "env-utility.psm1") -Raw
        $envUtility | Should -Match 'ConfigProvider\s*=\s*Get-ComposeEnvironmentValue -EnvironmentObject \$configEnvironment -Key "AppSettings__Datastore"'
        $envUtility | Should -Match 'DmsProvider\s*=\s*Get-ComposeEnvironmentValue -EnvironmentObject \$dmsEnvironment -Key "AppSettings__Datastore"'
        $envUtility | Should -Not -Match '(?m)^\s*Provider\s*=\s*Get-ComposeEnvironmentValue' -Because "the ambiguous generic Provider field must be gone, not aliased"
    }

    It "<Script> reads the DMS and CMS providers through the unambiguous fields, never a bare .Provider" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
        @{ Script = 'start-local-config.ps1' }
    ) {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        $source | Should -Not -Match '\$resolvedCompose\.Provider\b' -Because "$Script must use \$resolvedCompose.ConfigProvider / .DmsProvider, not the removed ambiguous .Provider"
        $source | Should -Not -Match '\$contract\.Provider\b' -Because "$Script must use \$contract.ConfigProvider / .DmsProvider, not the removed ambiguous .Provider"
    }

    It "start-published-dms.ps1 uses ONE participation authority for both the config file and the contract" {
        # The same $configServiceIncluded decides published-config.yml selection and CMS-invariant validation,
        # so a Keycloak-without-config start never validates a CMS the compose set does not contain.
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot 'start-published-dms.ps1') -Raw
        $source | Should -Match '\$configServiceIncluded\s*=\s*\$EnableConfig -or \$InfraOnly -or \(\$IdentityProvider -eq "self-contained"\) -or \$bootstrapMode'
        $source | Should -Match 'if \(\$configServiceIncluded\)\s*\{[^}]*?\$files \+= @\("-f", "published-config\.yml"\)'
        $source | Should -Match '-ConfigServiceIncluded \$configServiceIncluded'
    }

    It "<Script> imports bootstrap-schema-tool WITH -Force (refreshes a stale resolver in a long-lived session)" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
        @{ Script = 'start-local-config.ps1' }
    ) {
        # A session that loaded a pre-BuildIfMissing schema-tool module retains the old resolver signature;
        # a non-forced import reuses it and Resolve-DmsSchemaTool -BuildIfMissing fails with an unknown
        # parameter. -Force on the outer module refreshes the resolver; the module's own nested
        # bootstrap-manifest import stays non-forced (asserted separately) so the manifest is not re-homed.
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        $source | Should -Match 'bootstrap-schema-tool\.psm1"?\)?\s*-Force' -Because "$Script must force-refresh the schema-tool module"
    }

    It "the topology resolver performs no CMS-connection or process-environment validation (single policy)" {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot "env-utility.psm1") -Raw
        $source | Should -Not -Match 'Assert-CmsConnectionStringTargetsConfigDatabase'
        $source | Should -Not -Match 'Assert-ConfigDatabaseProcessEnvironmentAgreement'
    }
}
