# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Semantic coverage for the two central boundaries the runtime contract and topology resolver route
# through: the single canonical engine-token (ConvertTo-CanonicalDatabaseEngine) and the single
# provider-aware database-identity policy (Get-DatabaseNameComparer / Test-DatabaseNameEquivalent),
# plus the topology resolver's Compose-reference materialization (it no longer interpolates datastore
# values in PowerShell, and the separate-topology collision guard now lives in the runtime contract).
# These are pure / file-based functions with no Docker or provider-tool dependency.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    function Get-TopologyEnvFixture {
        param([string]$DatastoreKey, [string]$DatastoreName)
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-topo-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $envFile = Join-Path $dir "base.env"
        Set-Content -LiteralPath $envFile -Value "$DatastoreKey=$DatastoreName"
        return [pscustomobject]@{ Dir = $dir; EnvFile = $envFile }
    }
}

Describe "ConvertTo-CanonicalDatabaseEngine (single engine-token boundary)" {
    It "resolves the case variant '<Variant>' to canonical '<Expected>'" -ForEach @(
        @{ Variant = 'postgresql'; Expected = 'postgresql' }
        @{ Variant = 'PostgreSQL'; Expected = 'postgresql' }
        @{ Variant = 'POSTGRESQL'; Expected = 'postgresql' }
        @{ Variant = 'mssql';      Expected = 'mssql' }
        @{ Variant = 'MSSQL';      Expected = 'mssql' }
        @{ Variant = 'MsSql';      Expected = 'mssql' }
    ) {
        ConvertTo-CanonicalDatabaseEngine -Engine $Variant | Should -BeExactly $Expected
    }

    It "rejects the unsupported or whitespace-padded value '<Variant>'" -ForEach @(
        @{ Variant = 'mysql' }
        @{ Variant = ' mssql ' }
        @{ Variant = 'postgres' }
        @{ Variant = '' }
    ) {
        { ConvertTo-CanonicalDatabaseEngine -Engine $Variant } | Should -Throw
    }
}

Describe "Provider-aware database identity policy (Test-DatabaseNameEquivalent / Get-DatabaseNameComparer)" {
    Context "PostgreSQL uses ordinal (case-sensitive) identity" {
        It "treats an exact match as the same database" {
            Test-DatabaseNameEquivalent -Engine postgresql -Left "SchoolDb" -Right "SchoolDb" | Should -BeTrue
        }
        It "treats a case-only difference as distinct databases" {
            Test-DatabaseNameEquivalent -Engine postgresql -Left "SchoolDb" -Right "schooldb" | Should -BeFalse
        }
        It "exposes an Ordinal comparer" {
            (Get-DatabaseNameComparer -Engine postgresql) | Should -Be ([System.StringComparer]::Ordinal)
        }
    }

    Context "SQL Server uses case-insensitive identity (conservative default collation)" {
        It "treats a case-only difference as the same database" {
            Test-DatabaseNameEquivalent -Engine mssql -Left "SchoolDb" -Right "schooldb" | Should -BeTrue
        }
        It "still distinguishes genuinely different names" {
            Test-DatabaseNameEquivalent -Engine mssql -Left "SchoolDb" -Right "OtherDb" | Should -BeFalse
        }
        It "exposes an OrdinalIgnoreCase comparer" {
            (Get-DatabaseNameComparer -Engine mssql) | Should -Be ([System.StringComparer]::OrdinalIgnoreCase)
        }
    }

    It "accepts a case variant engine and applies the canonical policy" {
        # The identity policy routes its engine through the same canonical boundary.
        Test-DatabaseNameEquivalent -Engine "MSSQL" -Left "SchoolDb" -Right "schooldb" | Should -BeTrue
        Test-DatabaseNameEquivalent -Engine "PostgreSQL" -Left "SchoolDb" -Right "schooldb" | Should -BeFalse
    }
}

Describe "Resolve-ConfigDatabaseTopologyEnvironmentFile no longer performs separate-mode collision validation (single policy)" {
    # The separate-mode datastore-vs-configuration distinctness moved to the runtime contract, which enforces
    # it against Docker Compose's own resolution of the topology datastore anchor (RuntimeConfigContract's
    # "topology relationship" context) using the same provider-aware identity policy. The resolver now only
    # materializes the effective configuration-database name; it does not read an env-file projection of the
    # datastore or throw a collision - so a shell override could no longer bypass the check and a blank
    # datastore no longer silently skips it.
    It "materializes the dedicated configuration database in separate mode even when the datastore name collides (<Engine>)" -ForEach @(
        @{ Engine = 'postgresql'; Key = 'POSTGRES_DB_NAME' }
        @{ Engine = 'mssql';      Key = 'MSSQL_DB_NAME' }
    ) {
        $fixture = Get-TopologyEnvFixture -DatastoreKey $Key -DatastoreName "edfi_configurationservice"
        try {
            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $fixture.EnvFile -DockerComposeRoot $fixture.Dir -DatabaseEngine $Engine -SeparateConfigDatabase
            (ReadValuesFromEnvFile $resolvedEnv)['DMS_CONFIG_DATABASE_NAME'] | Should -Be 'edfi_configurationservice' -Because "the resolver materializes the effective name; the contract owns the collision check"
        }
        finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It "does not inspect the datastore or throw in separate mode for a SQL Server case variant of the dedicated name" {
        $fixture = Get-TopologyEnvFixture -DatastoreKey "MSSQL_DB_NAME" -DatastoreName "EdFi_ConfigurationService"
        try {
            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $fixture.EnvFile -DockerComposeRoot $fixture.Dir -DatabaseEngine mssql -SeparateConfigDatabase } |
                Should -Not -Throw
        }
        finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It "materializes the shared-topology datastore name unchanged (no collision inspection)" {
        $fixture = Get-TopologyEnvFixture -DatastoreKey "POSTGRES_DB_NAME" -DatastoreName "edfi_configurationservice"
        try {
            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $fixture.EnvFile -DockerComposeRoot $fixture.Dir -DatabaseEngine postgresql } |
                Should -Not -Throw
        }
        finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }
}

Describe "Resolve-ConfigDatabaseTopologyEnvironmentFile materializes a default-bearing Compose reference in shared mode (no PowerShell datastore interpolation)" {
    It "shared mode materializes DMS_CONFIG_DATABASE_NAME as a default-bearing reference to the datastore key on <Engine>" -ForEach @(
        @{ Engine = 'postgresql'; Key = 'POSTGRES_DB_NAME'; Expected = '${POSTGRES_DB_NAME:-edfi_datamanagementservice}' }
        @{ Engine = 'mssql';      Key = 'MSSQL_DB_NAME';    Expected = '${MSSQL_DB_NAME:-edfi_datamanagementservice}' }
    ) {
        $fixture = Get-TopologyEnvFixture -DatastoreKey $Key -DatastoreName "edfi_datamanagementservice"
        try {
            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $fixture.EnvFile -DockerComposeRoot $fixture.Dir -DatabaseEngine $Engine
            # The default matches the db service's own datastore default, so an omitted key resolves the CMS
            # seam to the same anchor rather than blank. Docker Compose - not PowerShell - resolves it.
            (ReadValuesFromEnvFile $resolvedEnv)['DMS_CONFIG_DATABASE_NAME'] | Should -BeExactly $Expected
        }
        finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It "shared mode does NOT interpolate a Compose default expression in the datastore value (finding 2: no throw, default-bearing reference materialized)" {
        # The old resolver threw on '${LOCAL_DB:-...}' via the handwritten Resolve-EnvValueReference. The
        # resolver must no longer parse datastore values in PowerShell: it materializes the seam reference and
        # lets Docker Compose resolve the ${VAR:-default} expression.
        $fixture = Get-TopologyEnvFixture -DatastoreKey 'POSTGRES_DB_NAME' -DatastoreName '${LOCAL_DB:-edfi_datamanagementservice}'
        try {
            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $fixture.EnvFile -DockerComposeRoot $fixture.Dir -DatabaseEngine postgresql
            (ReadValuesFromEnvFile $resolvedEnv)['DMS_CONFIG_DATABASE_NAME'] | Should -BeExactly '${POSTGRES_DB_NAME:-edfi_datamanagementservice}'
        }
        finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It "separate mode materializes the dedicated configuration-database literal" {
        $fixture = Get-TopologyEnvFixture -DatastoreKey 'POSTGRES_DB_NAME' -DatastoreName 'edfi_datamanagementservice'
        try {
            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $fixture.EnvFile -DockerComposeRoot $fixture.Dir -DatabaseEngine postgresql -SeparateConfigDatabase
            (ReadValuesFromEnvFile $resolvedEnv)['DMS_CONFIG_DATABASE_NAME'] | Should -BeExactly 'edfi_configurationservice'
        }
        finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It "shared mode is an idempotent no-op when the base already carries the default-bearing seam reference" {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-topo-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        try {
            $envFile = Join-Path $dir "base.env"
            Set-Content -LiteralPath $envFile -Value "POSTGRES_DB_NAME=edfi_datamanagementservice`nDMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME:-edfi_datamanagementservice}"
            $resolvedEnv = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $envFile -DockerComposeRoot $dir -DatabaseEngine postgresql
            $resolvedEnv | Should -BeExactly $envFile -Because "the base already carries the default-bearing seam reference, so no derived file is written"
        }
        finally {
            Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
        }
    }
}

Describe "Resolve-RegisteredDatastoreTarget (single registered-datastore-target authority, pure)" {
    It "omission (blank replacement) converges on the Compose-resolved topology anchor" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '' -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' | Should -BeExactly 'edfi_datamanagementservice'
    }

    It "an explicit replacement overrides the anchor (E2E database)" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName 'edfi_datamanagementservice_e2e' -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' | Should -BeExactly 'edfi_datamanagementservice_e2e'
    }

    It "a shell-moved anchor flows through on omission (the caller resolves the anchor with shell precedence)" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '' -TopologyDatastoreDatabaseName 'shell_datastore' | Should -BeExactly 'shell_datastore'
    }

    It "throws when omitted and the anchor is blank (no database to register)" {
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '' -TopologyDatastoreDatabaseName '' } |
            Should -Throw "*topology datastore database resolved by Docker Compose is blank*"
    }

    It "separate topology rejects a <Engine> replacement colliding with edfi_configurationservice (<Replacement>)" -ForEach @(
        @{ Engine = 'postgresql'; Replacement = 'edfi_configurationservice' }
        @{ Engine = 'mssql';      Replacement = 'EDFI_ConfigurationService' }
    ) {
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine $Engine -RequestedDatabaseName $Replacement -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' -SeparateConfigDatabase } |
            Should -Throw "*same physical database as the dedicated configuration database*"
    }

    It "separate topology accepts a PostgreSQL case-variant that is a genuinely distinct database (case-sensitive identity)" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName 'EDFI_ConfigurationService' -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' -SeparateConfigDatabase | Should -BeExactly 'EDFI_ConfigurationService'
    }

    It "shared topology does not apply the collision guard to a replacement" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName 'edfi_configurationservice' -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' | Should -BeExactly 'edfi_configurationservice'
    }

    It "separate topology rejects a colliding OMITTED anchor on <Engine> (<Anchor>)" -ForEach @(
        @{ Engine = 'postgresql'; Anchor = 'edfi_configurationservice' }
        @{ Engine = 'mssql';      Anchor = 'EDFI_ConfigurationService' }
    ) {
        # The effective target IS the anchor (no replacement); the collision guard must fire on it, so a
        # colliding anchor cannot slip through in direct/manual configure - PostgreSQL exact, SQL Server case.
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine $Engine -RequestedDatabaseName '' -TopologyDatastoreDatabaseName $Anchor -SeparateConfigDatabase } |
            Should -Throw "*same physical database as the dedicated configuration database*"
    }

    It "separate topology accepts an omitted PostgreSQL anchor that is a genuinely distinct case variant" {
        # PostgreSQL identity is case-sensitive, so 'EDFI_ConfigurationService' is a DIFFERENT database from
        # 'edfi_configurationservice' and is not a collision.
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '' -TopologyDatastoreDatabaseName 'EDFI_ConfigurationService' -SeparateConfigDatabase | Should -BeExactly 'EDFI_ConfigurationService'
    }

    It "whitespace-only -DataStoreDatabaseName is treated as omitted (converges on the anchor)" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '   ' -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' | Should -BeExactly 'edfi_datamanagementservice'
    }

    It "rejects a connection-string-injection replacement (<Engine>) before the collision check: '<Payload>'" -ForEach @(
        # The effective target is concatenated verbatim as the connection-string Database keyword; a value with
        # a ';' + '=' would inject a second Database keyword (last-wins), silently redirecting the stored
        # connection past the equivalence check below. Rejected as an unsafe identifier, on both engines.
        @{ Engine = 'postgresql'; Payload = 'safe;Database=edfi_configurationservice' }
        @{ Engine = 'mssql'; Payload = 'safe;Database=edfi_configurationservice' }
        @{ Engine = 'postgresql'; Payload = 'x;Host=evil-postgresql' }
        @{ Engine = 'mssql'; Payload = 'x${INJECT}' }
    ) {
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine $Engine -RequestedDatabaseName $Payload -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' -SeparateConfigDatabase } |
            Should -Throw "*not valid in a database identifier*"
    }

    It "rejects a connection-string-injection ANCHOR (not just the replacement)" {
        # The safety check applies to the effective target regardless of source, so a Compose-resolved anchor
        # carrying an injected keyword (e.g. a shell POSTGRES_DB_NAME override) is caught too.
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '' -TopologyDatastoreDatabaseName 'anchor;Database=edfi_configurationservice' } |
            Should -Throw "*not valid in a database identifier*"
    }

    It "rejects a leading/trailing-whitespace effective target (<Engine>, <Position>) the providers would trim: '<Payload>'" -ForEach @(
        # Npgsql / Microsoft.Data.SqlClient trim whitespace around a keyword value, so 'Database= edfi_configurationservice'
        # parses as 'edfi_configurationservice'. An untrimmed value would pass the collision check (compared
        # un-trimmed) yet target the trimmed CMS database at runtime - rejected here, on both engines.
        @{ Engine = 'postgresql'; Position = 'leading'; Payload = ' edfi_configurationservice' }
        @{ Engine = 'postgresql'; Position = 'trailing'; Payload = 'edfi_configurationservice ' }
        @{ Engine = 'mssql'; Position = 'leading'; Payload = ' edfi_configurationservice' }
        @{ Engine = 'mssql'; Position = 'trailing'; Payload = 'edfi_configurationservice ' }
    ) {
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine $Engine -RequestedDatabaseName $Payload -TopologyDatastoreDatabaseName 'edfi_datamanagementservice' -SeparateConfigDatabase } |
            Should -Throw "*leading or trailing whitespace*"
    }

    It "rejects a leading/trailing-whitespace ANCHOR (not just the replacement)" {
        { Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName '' -TopologyDatastoreDatabaseName 'edfi_datamanagementservice ' } |
            Should -Throw "*leading or trailing whitespace*"
    }

    It "still accepts a legitimate name containing spaces, hyphens, and periods (identifier-safety, not a strict whitelist)" {
        Resolve-RegisteredDatastoreTarget -InfrastructureEngine postgresql -RequestedDatabaseName 'edfi-data.management service' -TopologyDatastoreDatabaseName 'anchor' | Should -BeExactly 'edfi-data.management service'
    }
}
