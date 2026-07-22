# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Semantic coverage for the two central boundaries the runtime contract and topology resolver route
# through: the single canonical engine-token (ConvertTo-CanonicalDatabaseEngine) and the single
# provider-aware database-identity policy (Get-DatabaseNameComparer / Test-DatabaseNameEquivalent),
# plus the separate-topology collision guard that consumes the identity policy. These are pure /
# file-based functions with no Docker or provider-tool dependency.

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
