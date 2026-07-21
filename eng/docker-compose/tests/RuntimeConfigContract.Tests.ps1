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
        @{ Category = "finding-4 unsupported provider"; Case = "mysql rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'mysql'; ResolvedCmsConnectionString = 'host=h;database=edfi_datamanagementservice'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-4 unsupported provider"; Case = "blank provider rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = ''; ResolvedCmsConnectionString = 'host=h;database=edfi_datamanagementservice'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "provider-vs-engine split"; Case = "shell mssql provider on a postgresql run"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=p'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-2 shared-alias PostgreSQL"; Case = "Server=/User Id= accepted on postgresql"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgShared'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "finding-1 opaque shell terminal"; Case = "unexpanded reference target rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=${OTHER_DB};'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "wrong-engine connection"; Case = "PostgreSQL Host= on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=edfi_datamanagementservice'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "empty-connection fail-fast"; Case = "empty connection on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = ''; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "no-database connection"; Case = "connection targeting no database rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;username=postgres;password=p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "wrong-target-db"; Case = "connection to a different database rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=wrong_db;User Id=sa;Password=p;TrustServerCertificate=true'; ResolvedMssqlSaPassword = 'p'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "SA password presence"; Case = "blank SA password on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = ''; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "happy path PostgreSQL"; Case = "valid full-stack contract resolves"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgConn'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
        @{ Category = "happy path SQL Server"; Case = "valid full-stack contract resolves"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = 'abcdefgh1!'; ConfigDatabaseName = 'edfi_datamanagementservice' }; ShouldThrow = $false }
    ) {
        $resolvedArgs = @{ SchemaToolPath = $script:schemaTool }
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
            $contract.Provider | Should -Be $resolvedArgs.InfrastructureEngine
        }
    }

    It "derives the effective configuration database from the connection when no -ConfigDatabaseName is supplied (standalone lane)" {
        $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool
        $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        $contract.OpenIddict.DbName | Should -Be 'edfi_datamanagementservice'
    }

    Context "datastore-name agreement (container Compose-resolved vs host env-file)" {
        It "rejects a datastore database the containers receive that differs from the env-file value (direct or indirect override)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' -ResolvedDatastoreConnectionString 'host=dms-postgresql;username=postgres;database=rogue_database' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' } } |
                Should -Throw "*datastore database the containers receive*rogue_database*"
        }

        It "accepts when the container datastore database matches the env-file value" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' -ResolvedDatastoreConnectionString 'host=dms-postgresql;username=postgres;database=edfi_datamanagementservice' -EnvValues @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice' }
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }
    }
}

Describe "Docker Compose behavioral oracle (live) - Compose is the authority" {
    BeforeAll {
        if (-not (Test-DockerComposeAvailable)) {
            throw "docker compose is required for the Compose oracle and must not be skipped; it is unavailable in this environment."
        }
        $script:pgFiles = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot "postgresql.yml"))
        $script:envDefault = Join-Path $script:composeRoot ".env"
    }

    It "resolves the PostgreSQL CMS connection and provider from the real files" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:envDefault
        $r.Provider | Should -Be 'postgresql'
        $r.CmsConnectionString | Should -Match 'database=edfi_datamanagementservice'
    }

    It "keeps a shell-substituted terminal OPAQUE (finding 1): the container receives the literal reference, which the contract rejects" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:envDefault -ShellOverrides @{ DMS_CONFIG_DATABASE_NAME = '${OTHER_DB}' }
        $r.CmsConnectionString | Should -Match 'database=\$\{OTHER_DB\}'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider $r.Provider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' } | Should -Throw
    }

    It "passes an unsupported provider through unchanged (finding 4), which the contract rejects" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:envDefault -ShellOverrides @{ DMS_CONFIG_DATASTORE = 'mysql' }
        $r.Provider | Should -Be 'mysql'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider $r.Provider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ConfigDatabaseName 'edfi_datamanagementservice' } | Should -Throw "*not a supported engine*"
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
                $baseEnv = Get-Content -LiteralPath $script:envDefault -Raw
                # Route POSTGRES_DB_NAME through an indirect variable defined in the same file.
                $baseEnv = $baseEnv -replace '(?m)^POSTGRES_DB_NAME=.*$', "DATASTORE_NAME=edfi_datamanagementservice`nPOSTGRES_DB_NAME=`${DATASTORE_NAME}"
                Set-Content -LiteralPath $envFile -Value $baseEnv -NoNewline

                # Materialize the separate-topology config database (as the start scripts do).
                $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $envFile -DockerComposeRoot $work -DatabaseEngine "postgresql" -SeparateConfigDatabase
                $envValues = ReadValuesFromEnvFile $resolvedEnv
                $envValues['DMS_CONFIG_DATABASE_NAME'] | Should -Be 'edfi_configurationservice'

                $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:fullFiles -EnvironmentFile $resolvedEnv -ShellOverrides @{ DATASTORE_NAME = 'rogue_database' }
                $resolved.DatastoreConnectionString | Should -Match 'database=rogue_database'

                { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ResolvedProvider $resolved.Provider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedDatastoreConnectionString $resolved.DatastoreConnectionString -ConfigDatabaseName $envValues['DMS_CONFIG_DATABASE_NAME'] -EnvValues $envValues } |
                    Should -Throw "*datastore database the containers receive*rogue_database*"
            }
            finally {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            }
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
                ((Get-Content -LiteralPath (Join-Path $script:composeRoot '.env') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)) | Set-Content -LiteralPath $merged -NoNewline
                $merged
            }
            else { Join-Path $script:composeRoot '.env' }

            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $baseEnv -DockerComposeRoot $work -DatabaseEngine $Engine -SeparateConfigDatabase:$Separate
            $files = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot $DbFile))
            $resolved = Get-ComposeResolvedConfiguration -ComposeFiles $files -EnvironmentFile $resolvedEnv -ProjectName "dms-cell-oracle"

            $resolved.Provider | Should -Be $Engine
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

    It "<Script> imports bootstrap-schema-tool before bootstrap-manifest (avoids the -Force re-home that breaks the env snapshot)" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        # bootstrap-schema-tool.psm1 re-imports bootstrap-manifest with -Force at load; if the script imports
        # it AFTER its own bootstrap-manifest import, the -Force re-homes bootstrap-manifest out of script
        # scope and Get-/Restore-BootstrapEnvSnapshot / Invoke-BootstrapStartupConfiguration break. The
        # schema-tool import must come first so the script's bootstrap-manifest import is the last one.
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        $schemaToolImport = $source.IndexOf('Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-tool.psm1")')
        $manifestImport = $source.IndexOf('Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1")')
        $schemaToolImport | Should -BeGreaterThan -1
        $manifestImport | Should -BeGreaterThan -1
        $schemaToolImport | Should -BeLessThan $manifestImport
    }

    It "the topology resolver performs no CMS-connection or process-environment validation (single policy)" {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot "env-utility.psm1") -Raw
        $source | Should -Not -Match 'Assert-CmsConnectionStringTargetsConfigDatabase'
        $source | Should -Not -Match 'Assert-ConfigDatabaseProcessEnvironmentAgreement'
    }
}
