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
        param([string[]]$ComposeFiles, [string]$EnvironmentFile, [hashtable]$ShellOverrides = @{}, [string]$InfrastructureEngine)
        $saved = @{}
        foreach ($k in $ShellOverrides.Keys) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            Set-Item "Env:$k" -Value $ShellOverrides[$k]
        }
        try {
            $composeArgs = @{ ComposeFiles = $ComposeFiles; EnvironmentFile = $EnvironmentFile; ProjectName = "dms-contract-oracle" }
            if (-not [string]::IsNullOrWhiteSpace($InfrastructureEngine)) { $composeArgs.InfrastructureEngine = $InfrastructureEngine }
            return Get-ComposeResolvedConfiguration @composeArgs
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
        @{ Case = "PG trims leading whitespace around the Database value (motivates the registered-target trim guard)";   Engine = "postgresql"; Conn = "host=h;username=u;password=p;database= edfi_configurationservice";                       Expect = "edfi_configurationservice" }
        @{ Case = "MSSQL trims trailing whitespace around the Database value (motivates the registered-target trim guard)"; Engine = "mssql";      Conn = "Server=x;Database=edfi_configurationservice ;User Id=sa;Password=p;TrustServerCertificate=true";       Expect = "edfi_configurationservice" }
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
        $script:pgConfigConn = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_configurationservice;'
        $script:mssqlConfigConn = 'Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
        # Local db endpoints matching the connection hosts/ports above, so the full-stack lanes that reach the
        # endpoint-locality check (accepted cases) pass it (the connections dial the in-network name at the
        # container port). Cases that fail an earlier invariant (wrong db name, provider mismatch, blank anchor)
        # throw before the endpoint check and do not require one.
        $script:pgEndpoint = [pscustomobject]@{ ServiceName = 'db'; ContainerName = 'dms-postgresql'; Hostname = 'dms-postgresql'; InNetworkNames = @('db', 'dms-postgresql'); ContainerPort = 5432; PublishedHost = '127.0.0.1'; PublishedPort = 5435; PostgresAdminUser = 'postgres' }
        $script:mssqlEndpoint = [pscustomobject]@{ ServiceName = 'db'; ContainerName = 'dms-mssql'; Hostname = 'dms-mssql'; InNetworkNames = @('db', 'dms-mssql'); ContainerPort = 1433; PublishedHost = '127.0.0.1'; PublishedPort = 1435; PostgresAdminUser = $null }
    }

    It "<Category>: <Case>" -ForEach @(
        @{ Category = "finding-4 unsupported provider"; Case = "mysql rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'mysql'; ResolvedCmsConnectionString = 'host=h;database=edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "finding-4 unsupported provider"; Case = "blank provider rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = ''; ResolvedCmsConnectionString = 'host=h;database=edfi_datamanagementservice' }; ShouldThrow = $true }
        @{ Category = "provider-vs-engine split"; Case = "shell mssql provider on a postgresql run"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=p'; ResolvedMssqlSaPassword = 'p' }; ShouldThrow = $true }
        @{ Category = "finding-2 shared-alias PostgreSQL"; Case = "Server=/User Id= accepted on postgresql"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgShared' }; ShouldThrow = $false }
        @{ Category = "finding-1 opaque shell terminal"; Case = "unexpanded reference target rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=${OTHER_DB};' }; ShouldThrow = $true }
        @{ Category = "wrong-engine connection"; Case = "PostgreSQL Host= on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'host=dms-postgresql;database=edfi_datamanagementservice'; ResolvedMssqlSaPassword = 'p' }; ShouldThrow = $true }
        @{ Category = "empty-connection fail-fast"; Case = "empty connection on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = ''; ResolvedMssqlSaPassword = 'p' }; ShouldThrow = $true }
        @{ Category = "no-database connection"; Case = "connection targeting no database rejected"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'host=dms-postgresql;username=postgres;password=p' }; ShouldThrow = $true }
        @{ Category = "SA password presence"; Case = "blank SA password on a SQL Server stack rejected"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = '' }; ShouldThrow = $true }
        @{ Category = "happy path PostgreSQL"; Case = "valid standalone contract resolves"; ContractArgs = @{ InfrastructureEngine = 'postgresql'; ResolvedConfigProvider = 'postgresql'; ResolvedCmsConnectionString = 'PLACEHOLDER_pgConn' }; ShouldThrow = $false }
        @{ Category = "happy path SQL Server"; Case = "valid standalone contract resolves"; ContractArgs = @{ InfrastructureEngine = 'mssql'; ResolvedConfigProvider = 'mssql'; ResolvedCmsConnectionString = 'PLACEHOLDER_mssqlConn'; ResolvedMssqlSaPassword = 'abcdefgh1!' }; ShouldThrow = $false }
    ) {
        # This matrix exercises the CMS invariants in isolation, so DMS participation is off (standalone
        # lane): the resolved connection is authoritative and its single target IS the effective name. The
        # topology relationship (full-stack, expected against the datastore anchor) and the DMS provider have
        # their own dedicated contexts below.
        $resolvedArgs = @{ SchemaToolPath = $script:schemaTool; ConfigServiceIncluded = $true; DmsServiceIncluded = $false; OpenIddictIncluded = $false }
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
        $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $true -ResolvedConfigProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedDbLocalEndpoint $script:pgEndpoint
        $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        $contract.OpenIddict.DbName | Should -Be 'edfi_datamanagementservice'
    }

    Context "topology relationship (full-stack: DMS + CMS participate, expected computed from the anchor)" {
        # The expected configuration database is computed INDEPENDENTLY of the connection - shared topology
        # uses the DMS topology datastore anchor, -SeparateConfigDatabase the dedicated
        # 'edfi_configurationservice'. A caller-authored connection can never redefine the topology; it must
        # agree with the anchor/literal.
        It "shared: accepts a CMS connection targeting the datastore anchor" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
            $contract.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "shared: rejects a CMS connection targeting a database other than the anchor (wrong target)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'mssql' -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString 'Server=dms-mssql,1433;Database=wrong_db;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true' -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*wrong_db*effective configuration database is 'edfi_datamanagementservice'*"
        }

        It "separate: accepts a CMS connection targeting edfi_configurationservice, distinct from the anchor" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -SeparateConfigDatabase -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConfigConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_configurationservice'
        }

        It "separate: rejects a CMS connection targeting the datastore anchor instead of edfi_configurationservice" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -SeparateConfigDatabase -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*effective configuration database is 'edfi_configurationservice'*"
        }

        It "separate: rejects a datastore anchor that collides with edfi_configurationservice (not separate in name only)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -SeparateConfigDatabase -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConfigConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_configurationservice' } |
                Should -Throw "*same physical database*topology would not be separate*"
        }

        It "rejects a blank topology datastore anchor when the DMS service participates" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName '' } |
                Should -Throw "*topology datastore database is blank*"
        }

        It "rejects an unexpanded (opaque) topology datastore anchor" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName '${OTHER_DB}' } |
                Should -Throw "*unexpanded variable reference*"
        }
    }

    Context "provider-aware database identity at the contract boundary (finding 2, real parser)" {
        # The anchor-vs-CMS-target comparison routes through the one provider-aware identity policy
        # (Test-DatabaseNameEquivalent): PostgreSQL case-sensitive (a case-only difference is a DIFFERENT
        # physical database and must fail), SQL Server case-insensitive (a case variant is the SAME database).
        # The connection carries a case-variant database name; the exact provider builder preserves it, so the
        # equality policy decides pass/fail.
        It "shared: rejects a PostgreSQL CMS target that differs from the anchor only by case" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=p;database=EdFi_DataManagementService;' -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*EdFi_DataManagementService*"
        }

        It "shared: accepts a SQL Server CMS target that differs from the anchor only by case" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'mssql' -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString 'Server=dms-mssql,1433;Database=EdFi_DataManagementService;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;' -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:mssqlEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "separate: a SQL Server anchor colliding with edfi_configurationservice only by case is rejected (case-insensitive identity)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -SeparateConfigDatabase -ResolvedConfigProvider 'mssql' -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString $script:mssqlConfigConn -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -ResolvedTopologyDatastoreDatabaseName 'EDFI_ConfigurationService' } |
                Should -Throw "*same physical database*"
        }
    }

    Context "Configuration Service participation split (Keycloak without a local config service)" {
        # Blocker regression: a supported Keycloak start that omits the local config service (external
        # CONFIG_SERVICE_URL, or none) composes no config service, so Compose exposes no provider/connection.
        # The contract must SKIP the CMS invariants for that shape instead of throwing on the legitimately
        # absent values - while still enforcing the stack invariants, because the DMS datastore still starts.
        It "skips the CMS provider/connection/OpenIddict invariants when the config service does not participate" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool
            $contract.ConfigProvider | Should -BeNullOrEmpty
            $contract.CmsDatabaseName | Should -BeNullOrEmpty
            $contract.OpenIddict | Should -BeNullOrEmpty
        }

        It "does not reject an unsupported/absent CMS provider when the config service does not participate" {
            # With config participating this same 'mysql' provider is rejected (see the regression matrix);
            # without it, the CMS provider is never read.
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider 'mysql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool } |
                Should -Not -Throw
        }

        It "still validates the DMS topology datastore anchor (blank rejected) when config is absent but the DMS service participates (published Keycloak)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $null -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName '' } |
                Should -Throw "*topology datastore database is blank*"
        }

        It "still rejects a blank SQL Server SA password when neither the config nor the DMS service participates" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword '' } |
                Should -Throw "*MSSQL_SA_PASSWORD resolves to a blank value*"
        }

        It "still returns the SA password when neither the config nor the DMS service participates" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!'
            $contract.MssqlSaPassword | Should -Be 'abcdefgh1!'
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
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $false -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedDmsProvider $DmsProvider -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool @saArgs } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }

        It "rejects an unsupported/blank/whitespace DMS provider '<Label>' when the DMS service participates" -ForEach @(
            @{ Label = 'mysql';      DmsProvider = 'mysql' }
            @{ Label = 'blank';      DmsProvider = '' }
            @{ Label = 'whitespace'; DmsProvider = ' postgresql ' }
        ) {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedDmsProvider $DmsProvider -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool } |
                Should -Throw "*DMS runtime provider*not a supported engine*"
        }

        It "accepts a DMS provider that matches the selected engine" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice'
            $contract.DmsProvider | Should -Be 'postgresql'
        }

        It "accepts an absent DMS provider ONLY when the DMS service does not participate (standalone lane)" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider $null -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool
            $contract.DmsProvider | Should -BeNullOrEmpty
            $contract.ConfigProvider | Should -Be 'postgresql'
        }

        It "validates the DMS provider independently of the CMS provider: matching CMS + mismatched DMS is rejected" {
            # CMS provider matches the PostgreSQL engine, but the DMS provider is MSSQL - the reverse-mismatch
            # class the CMS-only check missed. The DMS invariant rejects it even though the CMS invariant passes.
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }
    }

    Context "endpoint locality enforcement (iteration 4, real parser)" {
        # Targeting the right database NAME is necessary but not sufficient: when the Configuration Service must
        # reach the LOCAL db container (full-stack, or self-contained OpenIddict), its connection ENDPOINT must
        # resolve to that container - the in-network host at the CONTAINER port, no alternate routing - or the
        # service could connect to a FOREIGN database while OpenIddict/the datastore initialize the local one.
        # Acceptance is fail-closed; a standalone Keycloak Configuration Service is exempt (external db allowed).
        It "full-stack: accepts a CMS connection dialing the in-network host at the container port (<Engine>)" -ForEach @(
            @{ Engine = 'postgresql'; Conn = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_datamanagementservice' }
            @{ Engine = 'mssql'; Conn = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true' }
        ) {
            $endpoint = if ($Engine -eq 'mssql') { $script:mssqlEndpoint } else { $script:pgEndpoint }
            $saArgs = if ($Engine -eq 'mssql') { @{ ResolvedMssqlSaPassword = 'abcdefgh1!' } } else { @{} }
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $Engine -ResolvedDmsProvider $Engine -ResolvedCmsConnectionString $Conn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $endpoint @saArgs
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "standalone self-contained: accepts the local in-network host at the container port (OpenIddict gates enforcement)" {
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $true -ResolvedConfigProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedDbLocalEndpoint $script:pgEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "rejects <Case>" -ForEach @(
            @{ Case = 'a foreign host (targets the right database name on the wrong server)'; Engine = 'postgresql'; Conn = 'host=remote-db;port=5432;username=postgres;password=p;database=edfi_datamanagementservice'; Throw = '*is not the local*' }
            @{ Case = 'a host-loopback endpoint the containerized service cannot use'; Engine = 'postgresql'; Conn = 'host=127.0.0.1;port=5432;username=postgres;password=p;database=edfi_datamanagementservice'; Throw = '*never a host-loopback*' }
            @{ Case = 'the host-published port instead of the container port'; Engine = 'postgresql'; Conn = 'host=dms-postgresql;port=5435;username=postgres;password=p;database=edfi_datamanagementservice'; Throw = '*not the host-published port*' }
            @{ Case = 'a multi-host PostgreSQL endpoint (no single resolvable target)'; Engine = 'postgresql'; Conn = 'host=dms-postgresql,standby;port=5432;username=postgres;password=p;database=edfi_datamanagementservice'; Throw = '*single host*' }
            @{ Case = 'a SQL Server Failover Partner (alternate routing off the local container)'; Engine = 'mssql'; Conn = 'Server=dms-mssql,1433;Failover Partner=standby;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true'; Throw = '*alternate routing*' }
        ) {
            $endpoint = if ($Engine -eq 'mssql') { $script:mssqlEndpoint } else { $script:pgEndpoint }
            $saArgs = if ($Engine -eq 'mssql') { @{ ResolvedMssqlSaPassword = 'abcdefgh1!' } } else { @{} }
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $Engine -ResolvedDmsProvider $Engine -ResolvedCmsConnectionString $Conn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $endpoint @saArgs } |
                Should -Throw $Throw
        }

        It "does NOT enforce endpoint locality for a standalone Keycloak Configuration Service (external database allowed)" {
            # No DMS service and no self-contained OpenIddict: the Configuration Service may legitimately use an
            # external database (Keycloak owns identity; there is no local OpenIddict key store to diverge from).
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedCmsConnectionString 'host=external-db.example.org;port=5432;username=postgres;password=p;database=edfi_configurationservice' -SchemaToolPath $script:schemaTool -ResolvedDbLocalEndpoint $script:pgEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_configurationservice'
        }

        It "fail-closed: rejects when locality is required but no local endpoint was resolved" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' } |
                Should -Throw "*no local database endpoint was resolved*"
        }

        It "accepts <Case> in a local-required lane" -ForEach @(
            @{ Case = 'the db service name as the host'; Engine = 'postgresql'; Conn = 'host=db;port=5432;username=postgres;password=p;database=edfi_datamanagementservice' }
            @{ Case = 'SQL Server with an omitted (default 1433) port'; Engine = 'mssql'; Conn = 'Server=dms-mssql;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true' }
            @{ Case = 'a SQL Server tcp: prefix at the container port'; Engine = 'mssql'; Conn = 'Server=tcp:dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true' }
        ) {
            $endpoint = if ($Engine -eq 'mssql') { $script:mssqlEndpoint } else { $script:pgEndpoint }
            $saArgs = if ($Engine -eq 'mssql') { @{ ResolvedMssqlSaPassword = 'abcdefgh1!' } } else { @{} }
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $Engine -ResolvedDmsProvider $Engine -ResolvedCmsConnectionString $Conn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $endpoint @saArgs
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "accepts a shared-network alias as the reachable host" {
            $aliasEndpoint = [pscustomobject]@{ ServiceName = 'db'; ContainerName = 'dms-postgresql'; Hostname = 'dms-postgresql'; InNetworkNames = @('db', 'dms-postgresql', 'pg-primary'); ContainerPort = 5432; PublishedHost = '127.0.0.1'; PublishedPort = 5435; PostgresAdminUser = 'postgres' }
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString 'host=pg-primary;port=5432;username=postgres;password=p;database=edfi_datamanagementservice' -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $aliasEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "rejects a SQL Server named instance in a local-required lane (not a single resolvable host at the container port)" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'mssql' -ResolvedDmsProvider 'mssql' -ResolvedCmsConnectionString 'Server=dms-mssql\SQLEXPRESS;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true' -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword 'abcdefgh1!' -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:mssqlEndpoint } |
                Should -Throw "*single host*"
        }

        It "standalone Keycloak accepts <Case> that a local-required lane rejects (external database allowed)" -ForEach @(
            @{ Case = 'a SQL Server named instance'; Engine = 'mssql'; Conn = 'Server=external-sql\SQLEXPRESS;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true'; Sa = $true }
            @{ Case = 'a PostgreSQL multi-host list'; Engine = 'postgresql'; Conn = 'host=primary.example.org,standby.example.org;port=5432;username=postgres;password=p;database=edfi_configurationservice'; Sa = $false }
        ) {
            $saArgs = if ($Sa) { @{ ResolvedMssqlSaPassword = 'abcdefgh1!' } } else { @{} }
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider $Engine -ResolvedCmsConnectionString $Conn -SchemaToolPath $script:schemaTool @saArgs
            $contract.CmsDatabaseName | Should -Be 'edfi_configurationservice'
        }
    }

    Context "endpoint locality - controlled inspector (iteration 4)" {
        # A crafted inspector double lets us pin behavior a real, coherent tool cannot produce: a single result
        # whose database and endpoint disagree; a caller-controlled host carrying control characters; and an old
        # tool whose output predates the endpoint projection.
        BeforeAll {
            $script:stubDir = Join-Path ([System.IO.Path]::GetTempPath()) "dms-inspect-stub-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $script:stubDir -Force | Out-Null
            function script:New-InspectStubTool {
                param([Parameter(Mandatory)][string]$InspectJson)
                $id = [Guid]::NewGuid().ToString('N')
                $jsonFile = Join-Path $script:stubDir "$id.json"
                Set-Content -LiteralPath $jsonFile -Value $InspectJson -Encoding utf8 -NoNewline
                $tool = Join-Path $script:stubDir "$id.ps1"
                # Ignore stdin; emit the crafted inspect result verbatim. Stands in for a divergent/old build.
                Set-Content -LiteralPath $tool -Encoding utf8 -Value "`$null = @(`$input)`nGet-Content -LiteralPath '$jsonFile' -Raw`nexit 0"
                return $tool
            }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:stubDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        It "derives the database from the SAME inspect result as the endpoint (a divergent database is rejected, not paired with a local endpoint)" {
            # The inspect result reports a LOCAL endpoint but a WRONG database. A contract that trusted a separate
            # validate call for the database (the expected name) plus this inspect for the endpoint would pass;
            # using the single result, the wrong database is caught.
            $tool = New-InspectStubTool -InspectJson '{"valid":true,"database":"wrong_db","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}'
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_datamanagementservice' -SchemaToolPath $tool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint } |
                Should -Throw "*wrong_db*effective configuration database is 'edfi_datamanagementservice'*"
        }

        It "sanitizes a caller-controlled endpoint host in the diagnostic (no control characters, no secret leak)" {
            $tool = New-InspectStubTool -InspectJson '{"valid":true,"database":"edfi_datamanagementservice","host":"remote\r\nINJECTED-LOGLINE","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"remote\r\nINJECTED-LOGLINE","port":5432,"instance":null,"hasAlternateRouting":false}}'
            $message = $null
            try {
                Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=SuperSecretPw1!;database=edfi_datamanagementservice' -SchemaToolPath $tool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint
            }
            catch { $message = $_.Exception.Message }
            $message | Should -Not -BeNullOrEmpty -Because "the injected foreign host is not a local in-network name"
            $message | Should -Not -Match "[`r`n]" -Because "control characters are stripped so a crafted host cannot forge log lines"
            $message | Should -Not -Match 'SuperSecretPw1' -Because "no secret is disclosed in the diagnostic"
            $message | Should -Match 'remoteINJECTED-LOGLINE' -Because "the sanitized host is still shown for diagnosis"
        }

        It "sanitizes a caller-controlled database name in the diagnostic (no control characters, no secret leak)" {
            # The database name is also parsed from the caller connection; a CR/LF in it must not forge log lines.
            $tool = New-InspectStubTool -InspectJson '{"valid":true,"database":"wrong_db\r\nINJECTED-DBLINE","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}'
            $message = $null
            try {
                Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=SuperSecretPw1!;database=wrong_db' -SchemaToolPath $tool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint
            }
            catch { $message = $_.Exception.Message }
            $message | Should -Not -BeNullOrEmpty -Because "the inspected database is not the effective configuration database"
            $message | Should -Not -Match "[`r`n]" -Because "control characters in the database name are stripped"
            $message | Should -Not -Match 'SuperSecretPw1' -Because "no secret is disclosed in the diagnostic"
            $message | Should -Match 'wrong_dbINJECTED-DBLINE' -Because "the sanitized database name is still shown for diagnosis"
        }

        It "fails as a version error (not a datastore error) when a local-required inspect predates the endpoint projection" {
            $tool = New-InspectStubTool -InspectJson '{"valid":true,"database":"edfi_datamanagementservice","host":"dms-postgresql","port":5432,"username":"postgres","error":null}'
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_datamanagementservice' -SchemaToolPath $tool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint } |
                Should -Throw "*missing the 'endpoint' projection*"
        }
    }

    Context "OpenIddict participation invariants (iteration 4)" {
        It "rejects OpenIddict participation without the Configuration Service" {
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $false -OpenIddictIncluded $true -ResolvedConfigProvider $null -ResolvedCmsConnectionString $null -SchemaToolPath $script:schemaTool } |
                Should -Throw "*without the Configuration Service*"
        }

        It "populates the OpenIddict coordinates only when OpenIddict participates" {
            $keycloak = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint
            $keycloak.OpenIddict | Should -BeNullOrEmpty -Because "a Keycloak Configuration Service runs no self-contained OpenIddict"

            $selfContained = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $true -ResolvedConfigProvider 'postgresql' -ResolvedDmsProvider 'postgresql' -ResolvedCmsConnectionString $script:pgConn -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName 'edfi_datamanagementservice' -ResolvedDbLocalEndpoint $script:pgEndpoint
            $selfContained.OpenIddict.DbName | Should -Be 'edfi_datamanagementservice' -Because "self-contained OpenIddict initializes the local configuration database"
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
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool } | Should -Throw
    }

    It "passes an unsupported provider through unchanged (finding 4), which the contract rejects" {
        $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_CONFIG_DATASTORE = 'mysql' }
        $r.ConfigProvider | Should -Be 'mysql'
        { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $false -OpenIddictIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool } | Should -Throw "*not a supported engine*"
    }

    Context "topology datastore anchor is sourced from the db service by the explicit engine, not the admin connection (finding 1)" {
        BeforeAll {
            $script:fullFiles = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            $script:mssqlFullFiles = @("-f", (Join-Path $script:composeRoot "mssql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            $script:mssqlAnchorWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-anchor-mssql-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $script:mssqlAnchorWork -Force | Out-Null
            $script:mssqlAnchorEnv = Join-Path $script:mssqlAnchorWork ".env.mssql.merged"
            ((Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.example') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)) | Set-Content -LiteralPath $script:mssqlAnchorEnv -NoNewline
        }
        AfterAll {
            Remove-Item -LiteralPath $script:mssqlAnchorWork -Recurse -Force -ErrorAction SilentlyContinue
        }

        It "PostgreSQL: a shell override of POSTGRES_DB_NAME moves the topology anchor" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:fullFiles -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql' -ShellOverrides @{ POSTGRES_DB_NAME = 'shell_datastore' }
            $resolved.TopologyDatastoreDatabaseName | Should -Be 'shell_datastore' -Because "the anchor reads the Compose-resolved db-service datastore key at shell-over-env-file precedence"
        }

        It "PostgreSQL: a change to DATABASE_CONNECTION_STRING_ADMIN.database does NOT move the topology anchor" {
            # DATABASE_CONNECTION_STRING_ADMIN is a readiness/admin connection (run.sh uses only host/port/user);
            # its database is deliberately NOT the datastore-name oracle. Overriding it to the documented admin
            # 'postgres' database must leave the anchor on the datastore key - guarding the finding-27 regression.
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:fullFiles -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql' -ShellOverrides @{ DATABASE_CONNECTION_STRING_ADMIN = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=postgres;' }
            $resolved.DmsAdminConnectionString | Should -Match 'database=postgres'
            $resolved.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice' -Because "the anchor is the db-service datastore key, never the admin connection's database"
        }

        It "PostgreSQL: the anchor carries the compose-file default when POSTGRES_DB_NAME is unset in the env file" {
            $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-anchor-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            try {
                $envFile = Join-Path $work ".env.nodatastore"
                $baseEnv = (Get-Content -LiteralPath $script:baseEnvFile -Raw) -replace '(?m)^POSTGRES_DB_NAME=.*$', ''
                Set-Content -LiteralPath $envFile -Value $baseEnv -NoNewline
                $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:fullFiles -EnvironmentFile $envFile -InfrastructureEngine 'postgresql'
                $resolved.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice' -Because "postgresql.yml supplies the datastore default, so the anchor is never blank"
            }
            finally {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "SQL Server: the default anchor resolves from the mssql db service and validates through the contract" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:mssqlFullFiles -EnvironmentFile $script:mssqlAnchorEnv -InfrastructureEngine 'mssql'
            $resolved.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice'
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $true -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword $resolved.MssqlSaPassword -ResolvedTopologyDatastoreDatabaseName $resolved.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $resolved.DbLocalEndpoint
            $contract.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
            $contract.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice'
        }

        It "SQL Server: a shell override of MSSQL_DB_NAME moves the topology anchor" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:mssqlFullFiles -EnvironmentFile $script:mssqlAnchorEnv -InfrastructureEngine 'mssql' -ShellOverrides @{ MSSQL_DB_NAME = 'shell_mssql_datastore' }
            $resolved.TopologyDatastoreDatabaseName | Should -Be 'shell_mssql_datastore'
        }

        It "SQL Server: an unrelated POSTGRES_DB_NAME shell override cannot become the anchor (engine-keyed selection)" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:mssqlFullFiles -EnvironmentFile $script:mssqlAnchorEnv -InfrastructureEngine 'mssql' -ShellOverrides @{ POSTGRES_DB_NAME = 'pg_rogue' }
            $resolved.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice' -Because "the anchor is selected by the explicit engine (MSSQL_DB_NAME), never positionally, so a PostgreSQL key cannot win on an MSSQL invocation"
        }
    }

    Context "the local db endpoint and OpenIddict coordinates converge on the real Compose db service (iteration 3)" {
        # The self-contained OpenIddict host-side initialization must target EXACTLY the engine, host dial
        # address, published port, container name, and admin user Compose renders for the `db` service - not an
        # ENV: sentinel the start scripts re-expand independently. These run the real `docker compose config`,
        # derive DbLocalEndpoint, feed it into the runtime contract, and assert the OpenIddict block carries the
        # concrete Compose coordinates and follows a shell override at shell-over-env-file precedence. Endpoint
        # CLASSIFICATION/enforcement is exercised by the sibling "endpoint locality enforcement" Context; here
        # the coordinates only have to converge.
        BeforeAll {
            $script:i3PgFiles = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            $script:i3MssqlFiles = @("-f", (Join-Path $script:composeRoot "mssql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            $script:i3Work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-endpoint-i3-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $script:i3Work -Force | Out-Null
            $script:i3MssqlEnv = Join-Path $script:i3Work ".env.mssql.merged"
            ((Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.example') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)) | Set-Content -LiteralPath $script:i3MssqlEnv -NoNewline
        }
        AfterAll {
            Remove-Item -LiteralPath $script:i3Work -Recurse -Force -ErrorAction SilentlyContinue
        }

        It "PostgreSQL: DbLocalEndpoint resolves the container, loopback dial address, and admin user from real Compose" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:i3PgFiles -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql'
            $resolved.DbLocalEndpoint | Should -Not -BeNullOrEmpty
            $resolved.DbLocalEndpoint.ContainerName | Should -Be 'dms-postgresql'
            $resolved.DbLocalEndpoint.ContainerPort | Should -Be 5432
            $resolved.DbLocalEndpoint.PublishedHost | Should -Be '127.0.0.1'
            $resolved.DbLocalEndpoint.PublishedPort | Should -BeGreaterThan 0
            $resolved.DbLocalEndpoint.PostgresAdminUser | Should -Be 'postgres'
            $resolved.DbLocalEndpoint.InNetworkNames | Should -Contain 'dms-postgresql'
        }

        It "PostgreSQL: the OpenIddict block carries the concrete Compose coordinates (no ENV sentinel)" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:i3PgFiles -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql'
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $true -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName $resolved.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $resolved.DbLocalEndpoint
            $contract.OpenIddict.DbType | Should -Be 'Postgresql'
            $contract.OpenIddict.DbUser | Should -Be 'postgres'
            $contract.OpenIddict.DbHost | Should -Be $resolved.DbLocalEndpoint.PublishedHost
            $contract.OpenIddict.DbPort | Should -Be $resolved.DbLocalEndpoint.PublishedPort
            $contract.OpenIddict.DbContainerName | Should -Be 'dms-postgresql'
            # The port is a concrete integer, never an unresolved '${...}' / 'ENV:' sentinel the caller re-expands.
            [string]$contract.OpenIddict.DbPort | Should -Not -Match '\$\{|ENV:'
            [string]$contract.OpenIddict.DbHost | Should -Not -Match '\$\{|ENV:'
        }

        It "PostgreSQL: a shell POSTGRES_PORT / POSTGRES_USER override flows through the endpoint into the OpenIddict block" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:i3PgFiles -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql' -ShellOverrides @{ POSTGRES_PORT = '5599'; POSTGRES_USER = 'alt_admin' }
            $resolved.DbLocalEndpoint.PublishedPort | Should -Be 5599
            $resolved.DbLocalEndpoint.PostgresAdminUser | Should -Be 'alt_admin'
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $true -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName $resolved.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $resolved.DbLocalEndpoint
            $contract.OpenIddict.DbPort | Should -Be 5599 -Because "the OpenIddict dial port is the Compose-resolved published port at shell-over-env-file precedence"
            $contract.OpenIddict.DbUser | Should -Be 'alt_admin' -Because "the PostgreSQL admin user is the Compose-resolved POSTGRES_USER, not a hardcoded 'postgres'"
        }

        It "SQL Server: DbLocalEndpoint uses the 1433 container port and the OpenIddict block targets the mssql container as 'sa'" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:i3MssqlFiles -EnvironmentFile $script:i3MssqlEnv -InfrastructureEngine 'mssql'
            $resolved.DbLocalEndpoint.ContainerName | Should -Be 'dms-mssql'
            $resolved.DbLocalEndpoint.ContainerPort | Should -Be 1433
            $resolved.DbLocalEndpoint.PostgresAdminUser | Should -BeNullOrEmpty -Because "SQL Server's admin user is the image-fixed 'sa', never read from POSTGRES_USER"
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $true -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword $resolved.MssqlSaPassword -ResolvedTopologyDatastoreDatabaseName $resolved.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $resolved.DbLocalEndpoint
            $contract.OpenIddict.DbType | Should -Be 'MSSQL'
            $contract.OpenIddict.DbUser | Should -Be 'sa'
            $contract.OpenIddict.DbContainerName | Should -Be 'dms-mssql'
        }

        It "SQL Server: a shell MSSQL_PORT override moves the OpenIddict dial port" {
            $resolved = Invoke-ComposeConfigResolution -ComposeFiles $script:i3MssqlFiles -EnvironmentFile $script:i3MssqlEnv -InfrastructureEngine 'mssql' -ShellOverrides @{ MSSQL_PORT = '1599' }
            $resolved.DbLocalEndpoint.PublishedPort | Should -Be 1599
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $true -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword $resolved.MssqlSaPassword -ResolvedTopologyDatastoreDatabaseName $resolved.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $resolved.DbLocalEndpoint
            $contract.OpenIddict.DbPort | Should -Be 1599
        }
    }

    Context "every switch-capable full-stack base resolves the CMS database through real Compose (shared and separate)" {
        # Durable per-profile guard (not one-time evidence): for EVERY dynamically discovered switch-capable
        # base profile (Get-ConfigProfileInventory - the same single authority the static seam guard uses),
        # run the real `docker compose config` in shared and separate topology using the profile's declared
        # engine, and assert the rendered CMS connection resolves to the datastore anchor (shared) or
        # edfi_configurationservice (separate). The database is extracted with the EXACT-provider oracle
        # (Get-CmsConnectionStringDatabaseName), never a regex. Compose-resolution only: no `up`, no image
        # build, no start-script preflight - so this guards the interpolation/default/shell semantics for
        # every profile without multiplying heavy lifecycle runs. All switch-capable bases are PostgreSQL
        # full profiles; the SQL Server engine is exercised by the sibling anchor Context via the .env.mssql
        # overlay composed onto a base.
        BeforeDiscovery {
            Import-Module (Join-Path $PSScriptRoot "ConfigProfileInventory.psm1") -Force
            $discoveryComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
            $discoveryRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $discoveryComposeRoot "../.."))
            $discoveryInventory = Get-ConfigProfileInventory -RepoRoot $discoveryRepoRoot -DockerComposeRoot $discoveryComposeRoot
            # Script-scoped so PSScriptAnalyzer sees it as used (it cannot follow the Pester -ForEach reference).
            # The shared-topology expected database is NOT hardcoded - it is derived at run time from the
            # profile's Compose-resolved datastore anchor, so a future profile selecting a different datastore
            # still passes as long as CMS follows the anchor. Only the separate-topology contract literal is fixed.
            $script:composeProfileCases = foreach ($profileName in @($discoveryInventory.SwitchCapableBases)) {
                @{ Profile = $profileName; Topology = 'shared';   Separate = $false }
                @{ Profile = $profileName; Topology = 'separate'; Separate = $true }
            }
        }

        BeforeAll {
            Import-Module (Join-Path $PSScriptRoot "ConfigProfileInventory.psm1") -Force
            $script:composeGuardFiles = @(
                "-f", (Join-Path $script:composeRoot "postgresql.yml"),
                "-f", (Join-Path $script:composeRoot "local-dms.yml"),
                "-f", (Join-Path $script:composeRoot "local-config.yml")
            )
            $script:composeGuardWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-profile-compose-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $script:composeGuardWork -Force | Out-Null
            $script:composeGuardInventory = Get-ConfigProfileInventory `
                -RepoRoot ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot "../.."))) `
                -DockerComposeRoot $script:composeRoot
        }

        AfterAll {
            if ($script:composeGuardWork) {
                Remove-Item -LiteralPath $script:composeGuardWork -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "establishes the switch-capable base inventory for the Compose guard (fails closed)" {
            $script:composeGuardInventory.Error | Should -BeNullOrEmpty -Because "the inventory must be established, not silently empty"
            @($script:composeGuardInventory.SwitchCapableBases).Count | Should -BeGreaterThan 0 -Because "there must be at least one switch-capable base to resolve through Compose"
        }

        It "<Profile> (<Topology>): real Compose renders the CMS database to match the topology contract" -ForEach $script:composeProfileCases {
            $base = Join-Path $script:composeRoot $Profile
            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile `
                -BaseEnvironmentFile $base `
                -DockerComposeRoot $script:composeGuardWork `
                -DatabaseEngine 'postgresql' `
                -SeparateConfigDatabase:$Separate
            $resolved = Get-ComposeResolvedConfiguration `
                -ComposeFiles $script:composeGuardFiles `
                -EnvironmentFile $resolvedEnv `
                -ProjectName "dms-profile-compose" `
                -InfrastructureEngine 'postgresql'

            # Shared topology means the CMS target EQUALS the Compose-resolved datastore anchor (not a
            # hardcoded literal); separate topology uses the dedicated configuration-database contract literal.
            $anchor = $resolved.TopologyDatastoreDatabaseName
            $anchor | Should -Not -BeNullOrEmpty -Because "$Profile must resolve a single concrete DMS datastore anchor"
            $anchor | Should -Not -Match '\$\{' -Because "$Profile must resolve the datastore anchor to a concrete value, not an unexpanded reference"
            $expectedDatabase = if ($Separate) { 'edfi_configurationservice' } else { $anchor }

            # Extract the rendered database with the exact-provider oracle, never a regex.
            $targets = @(Get-CmsConnectionStringDatabaseName -Engine 'postgresql' -ConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool)
            $targets | Should -HaveCount 1 -Because "$Profile must render a single concrete CMS database in $Topology mode"
            # PostgreSQL identity is case-sensitive: compare exactly, never case-insensitively.
            $targets[0] | Should -BeExactly $expectedDatabase -Because "$Profile in $Topology mode must render the CMS database as the $Topology topology target"
        }
    }

    Context "E2E safety-field resolution (the DMS_CONFIG_DATABASE_NAME topology seam) and local/published parity" {
        # provision-e2e-database.ps1's DROP-DATABASE guard reads the protected CMS and DMS-admin targets from
        # Compose (never a PowerShell ${...} parser). These prove Compose resolves the default-bearing seam
        # DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME:-edfi_datamanagementservice} - through the config
        # connection's NESTED ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} - the same way the running stack
        # does, and that the safety-relevant fields are byte-identical across the local and published lanes (so
        # the guard's fixed local compose set renders the same protected names regardless of image lane).
        BeforeAll {
            $script:pgLocal = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            $script:pgPublished = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "published-dms.yml"), "-f", (Join-Path $script:composeRoot "published-config.yml"))
            $script:mssqlLocal = @("-f", (Join-Path $script:composeRoot "mssql.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot "local-config.yml"))
            $script:mssqlPublished = @("-f", (Join-Path $script:composeRoot "mssql.yml"), "-f", (Join-Path $script:composeRoot "published-dms.yml"), "-f", (Join-Path $script:composeRoot "published-config.yml"))
            $script:safetyWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-safety-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $script:safetyWork -Force | Out-Null
            $script:mssqlSafetyEnv = Join-Path $script:safetyWork ".env.mssql.merged"
            ((Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.example') -Raw) + "`n" + (Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.mssql') -Raw)) | Set-Content -LiteralPath $script:mssqlSafetyEnv -NoNewline
        }
        AfterAll {
            Remove-Item -LiteralPath $script:safetyWork -Recurse -Force -ErrorAction SilentlyContinue
        }

        It "PostgreSQL: the CMS and DMS-admin targets resolve to the datastore key when POSTGRES_DB_NAME is present" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgLocal -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql'
            $r.CmsConnectionString | Should -Match 'database=edfi_datamanagementservice'
            $r.DmsAdminConnectionString | Should -Match 'database=edfi_datamanagementservice'
        }

        It "PostgreSQL: the CMS target falls back through ':-edfi_datamanagementservice' when POSTGRES_DB_NAME is omitted" {
            $envFile = Join-Path $script:safetyWork ".env.omitted"
            ((Get-Content -LiteralPath $script:baseEnvFile -Raw) -replace '(?m)^POSTGRES_DB_NAME=.*$', '') | Set-Content -LiteralPath $envFile -NoNewline
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgLocal -EnvironmentFile $envFile -InfrastructureEngine 'postgresql'
            $r.CmsConnectionString | Should -Match 'database=edfi_datamanagementservice' -Because "the nested ':-edfi_datamanagementservice' default applies when the key is omitted"
        }

        It "PostgreSQL: the CMS target falls back through ':-' when POSTGRES_DB_NAME is blank" {
            $envFile = Join-Path $script:safetyWork ".env.blank"
            ((Get-Content -LiteralPath $script:baseEnvFile -Raw) -replace '(?m)^POSTGRES_DB_NAME=.*$', 'POSTGRES_DB_NAME=') | Set-Content -LiteralPath $envFile -NoNewline
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgLocal -EnvironmentFile $envFile -InfrastructureEngine 'postgresql'
            $r.CmsConnectionString | Should -Match 'database=edfi_datamanagementservice' -Because "Compose ':-' uses the default when the variable is blank"
        }

        It "PostgreSQL: a shell POSTGRES_DB_NAME override moves the CMS and DMS-admin targets (shell precedence)" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:pgLocal -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql' -ShellOverrides @{ POSTGRES_DB_NAME = 'shell_seam_db' }
            $r.CmsConnectionString | Should -Match 'database=shell_seam_db'
            $r.DmsAdminConnectionString | Should -Match 'database=shell_seam_db'
            $r.TopologyDatastoreDatabaseName | Should -Be 'shell_seam_db'
        }

        It "the Compose-rendered safety fields are identical between the local and published lanes for <Engine> (<Scenario>)" -ForEach @(
            @{ Engine = 'postgresql'; Scenario = 'default'; Overrides = @{} }
            @{ Engine = 'postgresql'; Scenario = 'shell override'; Overrides = @{ POSTGRES_DB_NAME = 'parity_pg' } }
            @{ Engine = 'mssql'; Scenario = 'default'; Overrides = @{} }
            @{ Engine = 'mssql'; Scenario = 'shell override'; Overrides = @{ MSSQL_DB_NAME = 'parity_mssql' } }
        ) {
            $localFiles = if ($Engine -eq 'mssql') { $script:mssqlLocal } else { $script:pgLocal }
            $publishedFiles = if ($Engine -eq 'mssql') { $script:mssqlPublished } else { $script:pgPublished }
            $envFile = if ($Engine -eq 'mssql') { $script:mssqlSafetyEnv } else { $script:baseEnvFile }

            $local = Invoke-ComposeConfigResolution -ComposeFiles $localFiles -EnvironmentFile $envFile -InfrastructureEngine $Engine -ShellOverrides $Overrides
            $published = Invoke-ComposeConfigResolution -ComposeFiles $publishedFiles -EnvironmentFile $envFile -InfrastructureEngine $Engine -ShellOverrides $Overrides

            $published.TopologyDatastoreDatabaseName | Should -Be $local.TopologyDatastoreDatabaseName -Because "the topology anchor must not differ by image lane"
            $published.CmsConnectionString | Should -Be $local.CmsConnectionString -Because "the CMS persistence connection must not differ by image lane"
            $published.DmsAdminConnectionString | Should -Be $local.DmsAdminConnectionString -Because "the DMS admin connection must not differ by image lane"
            $published.ConfigProvider | Should -Be $local.ConfigProvider
            $published.DmsProvider | Should -Be $local.DmsProvider
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
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName $r.TopologyDatastoreDatabaseName } |
                Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
        }

        It "rejects the DMS-provider mismatch on a published Keycloak start without a local config service" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:publishedNoConfigFiles -EnvironmentFile $script:baseEnvFile -ShellOverrides @{ DMS_DATASTORE = 'mssql' }
            $r.ConfigProvider | Should -BeNullOrEmpty
            $r.DmsProvider | Should -Be 'mssql'
            { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $false -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName $r.TopologyDatastoreDatabaseName } |
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
                { Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'mssql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedMssqlSaPassword $r.MssqlSaPassword -ResolvedTopologyDatastoreDatabaseName $r.TopologyDatastoreDatabaseName } |
                    Should -Throw "*DMS runtime provider*Unset the conflicting DMS_DATASTORE*"
            }
            finally {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "accepts when the DMS and CMS providers both match the selected engine" {
            $r = Invoke-ComposeConfigResolution -ComposeFiles $script:localFullFiles -EnvironmentFile $script:baseEnvFile -InfrastructureEngine 'postgresql'
            $r.DmsProvider | Should -Be 'postgresql'
            $r.ConfigProvider | Should -Be 'postgresql'
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine 'postgresql' -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -ResolvedConfigProvider $r.ConfigProvider -ResolvedDmsProvider $r.DmsProvider -ResolvedCmsConnectionString $r.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName $r.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $r.DbLocalEndpoint
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
            $files = @("-f", (Join-Path $script:composeRoot "local-config.yml"), "-f", (Join-Path $script:composeRoot "local-dms.yml"), "-f", (Join-Path $script:composeRoot $DbFile))
            $resolved = Get-ComposeResolvedConfiguration -ComposeFiles $files -EnvironmentFile $resolvedEnv -ProjectName "dms-cell-oracle" -InfrastructureEngine $Engine

            $resolved.ConfigProvider | Should -Be $Engine
            $resolved.DmsProvider | Should -Be $Engine
            $resolved.TopologyDatastoreDatabaseName | Should -Be 'edfi_datamanagementservice' -Because "the datastore anchor is the datastore database in both topologies"
            @(Get-CmsConnectionStringDatabaseName -Engine $Engine -ConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool) | Should -Contain $ExpectedConfigDb

            # End-to-end: the full-stack contract accepts the cell and resolves the expected configuration db,
            # exercising the anchor (both engines) through the same policy the start scripts run.
            $saArgs = if ($Engine -eq 'mssql') { @{ ResolvedMssqlSaPassword = $resolved.MssqlSaPassword } } else { @{} }
            $contract = Resolve-EffectiveConfigRuntimeContract -InfrastructureEngine $Engine -ConfigServiceIncluded $true -DmsServiceIncluded $true -OpenIddictIncluded $false -SeparateConfigDatabase:$Separate -ResolvedConfigProvider $resolved.ConfigProvider -ResolvedDmsProvider $resolved.DmsProvider -ResolvedCmsConnectionString $resolved.CmsConnectionString -SchemaToolPath $script:schemaTool -ResolvedTopologyDatastoreDatabaseName $resolved.TopologyDatastoreDatabaseName -ResolvedDbLocalEndpoint $resolved.DbLocalEndpoint @saArgs
            $contract.CmsDatabaseName | Should -Be $ExpectedConfigDb
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

    It "<Script> resolves and validates the runtime contract before the first stack-lifecycle mutation (network create, build, up, keycloak)" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
        @{ Script = 'start-local-config.ps1' }
    ) {
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot $Script) -Raw
        $contractIndex = $source.IndexOf('$contract = Resolve-EffectiveConfigRuntimeContract')
        $networkIndex = $source.IndexOf('docker network create dms')

        $contractIndex | Should -BeGreaterThan -1 -Because "$Script resolves the runtime contract"
        $networkIndex | Should -BeGreaterThan -1
        $contractIndex | Should -BeLessThan $networkIndex -Because "$Script must validate before 'docker network create' - the first stack-lifecycle mutation - which precedes image build, container up, and Keycloak/OpenIddict"
    }

    It "build-dms.ps1 StartEnvironment runs the wrapper-owned preflight (staging + contract) before image build and teardown (finding 1)" {
        # The outer StartEnvironment orchestration builds images and tears down volumes BEFORE it reaches a
        # full wrapper run. It must first run the bootstrap WRAPPER in -PreflightOnly mode, which stages and
        # completes the bootstrap workspace and then runs the start script's own -PreflightOnly runtime-contract
        # validation - so an incompletable workspace or an invalid provider/connection string is reported
        # before existing databases are deleted. One shared wrapper argument record drives both the preflight
        # (with -PreflightOnly) and the full run (untouched).
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

        $bodyAst = $orchestrator.Body

        # Preflight commands: bootstrap-(local|published)-dms.ps1 invoked WITH a -PreflightOnly parameter.
        # (The full-run invocations of the same scripts carry no -PreflightOnly, so they are excluded.)
        $preflightCommands = @($bodyAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    ($null -ne $node.GetCommandName()) -and
                    ($node.GetCommandName() -match 'bootstrap-(local|published)-dms\.ps1$') -and
                    (@($node.CommandElements | Where-Object {
                        $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq 'PreflightOnly'
                    }).Count -gt 0)
                }, $true))

        # The mutation command nodes (DockerBuild lives inside Invoke-Step { DockerBuild }).
        $dockerBuildCommand = @($bodyAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'DockerBuild' }, $true)) | Select-Object -First 1
        $stopCommand = @($bodyAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Stop-DockerEnvironment' }, $true)) | Select-Object -First 1

        $preflightCommands.Count | Should -Be 2 -Because "the outer preflight runs the local and published wrapper in -PreflightOnly mode"
        $dockerBuildCommand | Should -Not -BeNullOrEmpty -Because "the orchestration builds images"
        $stopCommand | Should -Not -BeNullOrEmpty -Because "the orchestration tears down (compose down -v / volume deletion)"

        # Compare REAL command source offsets (not a substring search that a comment mentioning
        # -PreflightOnly could satisfy): every preflight command must precede BOTH mutation nodes.
        $lastPreflightEnd = ($preflightCommands | ForEach-Object { $_.Extent.EndOffset } | Measure-Object -Maximum).Maximum
        $lastPreflightEnd | Should -BeLessThan $dockerBuildCommand.Extent.StartOffset -Because "both preflight commands must precede image build"
        $lastPreflightEnd | Should -BeLessThan $stopCommand.Extent.StartOffset -Because "both preflight commands must precede teardown and volume deletion"

        # One shared wrapper argument record drives both the preflight (a copy with -PreflightOnly) and the
        # full run (untouched).
        ([regex]::Matches($bodyAst.Extent.Text, '\$bootstrapArgs = @\{')).Count |
            Should -Be 1 -Because "a single wrapper argument record is built once and shared by the preflight copy and the full run"
    }

    It "build-dms.ps1 and start-local-config.ps1 describe the preflight boundary as stack-lifecycle mutation, not 'external' action (finding 3)" {
        $buildScript = Get-Content -LiteralPath ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot "../../build-dms.ps1"))) -Raw
        $startLocalConfig = Get-Content -LiteralPath (Join-Path $script:composeRoot "start-local-config.ps1") -Raw

        $buildScript | Should -Not -Match 'external mutation' -Because "the preflight stages and validates but is not 'before any external mutation'"
        $buildScript | Should -Match 'stack-lifecycle mutation' -Because "the established boundary is stack-lifecycle mutation"
        $startLocalConfig | Should -Not -Match 'BEFORE any external' -Because "resolving/building the validator writes persistent state, so 'before any external action' is inaccurate"
        $startLocalConfig | Should -Match 'stack-lifecycle mutation' -Because "the established boundary is stack-lifecycle mutation"
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

    It "start-published-dms.ps1 resolves the connection validator with an in-image fallback (finding 4)" {
        # Published startup must resolve a host-exe OR a container validator (the DMS image that bundles the
        # tool) so a clean Docker/PowerShell-only host validates connection strings with the exact runtime
        # providers without a host .NET SDK or source build. A revert to Resolve-DmsSchemaTool (host-only)
        # would reintroduce the failure this guards.
        $source = Get-Content -LiteralPath (Join-Path $script:composeRoot 'start-published-dms.ps1') -Raw
        $source | Should -Match 'Resolve-DmsConnectionValidator' -Because "published startup must resolve a host-exe-or-container validator"
        $source | Should -Match 'Resolve-DmsConnectionValidator[^\r\n]*-DmsImage \$resolvedCompose\.DmsImage' -Because "the resolved DMS image must be passed so the validator can run inside it"

        # The image must be resolved (Get-ComposeResolvedConfiguration) before the validator, so the fallback
        # has an image to run in.
        $composeIndex = $source.IndexOf('$resolvedCompose = Get-ComposeResolvedConfiguration')
        $validatorIndex = $source.IndexOf('Resolve-DmsConnectionValidator')
        $composeIndex | Should -BeGreaterThan -1
        $composeIndex | Should -BeLessThan $validatorIndex -Because "the DMS image must be resolved before the validator fallback needs it"
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
