# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:templatesDir "Template-RestoreCore.psm1") -Force

    function script:New-TestInventory {
        # A small but representative DMS-only inventory covering multiple schemas, object
        # types, and (optionally) principals, built deliberately out of order so canonical
        # sorting is exercised by default.
        param (
            [string[]]$Principal = @()
        )

        return @{
            schemas    = @(
                @{
                    schemaName = "edfi"
                    objects    = @(
                        @{ name = "School"; type = "table" },
                        @{ name = "AcademicWeek"; type = "table" }
                    )
                },
                @{
                    schemaName = "dms"
                    objects    = @(
                        @{ name = "uuidv5"; type = "function" },
                        @{ name = "Document"; type = "TABLE" },
                        @{ name = "EffectiveSchema"; type = "table" }
                    )
                }
            )
            principals = $Principal
        }
    }

    function script:New-ValidRestoreManifest {
        # Builds a fully valid version-1 restore manifest as an ordered hashtable. The
        # inventory hash is computed with the module's own canonical hash so shape tests
        # start from a passing baseline and mutate exactly one field at a time.
        param (
            [ValidateSet("postgresql", "mssql")]
            [string]$DatabaseEngine = "postgresql"
        )

        $inventory = New-TestInventory
        $manifest = [ordered]@{
            version                  = 1
            packageId                = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
            packageVersion           = "1.0.123"
            databaseEngine           = $DatabaseEngine
            templateKind             = "Minimal"
            dataStandardVersion      = "5.2.0"
            contentProfile           = "DmsDatastoreOnly"
            projects                 = @("ed-fi", "tpdm")
            apiSchemaFormatVersion   = "1.0.0"
            effectiveSchemaHash      = ("ab" * 32)
            resourceKeyCount         = 42
            resourceKeySeedHashB64   = [System.Convert]::ToBase64String([byte[]](1..32))
            relationalMappingVersion = "v2"
            engineVersion            = "16.8"
            documentJsonColumnType   = "jsonb"
            inventory                = $inventory
            inventorySha256          = (Get-CanonicalInventoryHash -Inventory $inventory)
            artifactFileName         = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql"
            artifactSha256           = ("cd" * 32)
        }

        if ($DatabaseEngine -eq "mssql") {
            $manifest.packageId = "EdFi.Api.Minimal.Template.MsSql.5.2.0"
            $manifest.engineVersion = "17.0.900.7"
            $manifest.databaseCompatibilityLevel = 170
            $manifest.documentJsonColumnType = "nvarchar"
            $manifest.artifactFileName = "EdFi.Api.Minimal.Template.MsSql.5.2.0.bak"
        }

        return $manifest
    }
}

Describe "Test-ReservedDatabaseName" {
    It "reports every reserved PostgreSQL system database, case-insensitively and whitespace-trimmed" {
        foreach ($name in @("postgres", "template0", "template1", "POSTGRES", "Template1", " postgres ")) {
            Test-ReservedDatabaseName -DatabaseEngine postgresql -DatabaseName $name | Should -BeTrue
        }
    }

    It "reports every reserved SQL Server system database, case-insensitively" {
        foreach ($name in @("master", "model", "msdb", "tempdb", "MASTER", "TempDb")) {
            Test-ReservedDatabaseName -DatabaseEngine mssql -DatabaseName $name | Should -BeTrue
        }
    }

    It "does not treat ordinary datastore names as reserved" {
        Test-ReservedDatabaseName -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice" | Should -BeFalse
        Test-ReservedDatabaseName -DatabaseEngine mssql -DatabaseName "edfi_datamanagementservice" | Should -BeFalse
    }

    It "applies each engine's own denylist, not the union" {
        Test-ReservedDatabaseName -DatabaseEngine postgresql -DatabaseName "master" | Should -BeFalse
        Test-ReservedDatabaseName -DatabaseEngine mssql -DatabaseName "postgres" | Should -BeFalse
    }
}

Describe "Assert-SafeRestoreDatabaseName" {
    It "accepts a plain safe datastore name on both engines" {
        { Assert-SafeRestoreDatabaseName -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice" } | Should -Not -Throw
        { Assert-SafeRestoreDatabaseName -DatabaseEngine mssql -DatabaseName "edfi_datamanagementservice" } | Should -Not -Throw
    }

    It "rejects an empty or whitespace name" {
        { Assert-SafeRestoreDatabaseName -DatabaseEngine postgresql -DatabaseName "" } | Should -Throw "*must not be empty*"
        { Assert-SafeRestoreDatabaseName -DatabaseEngine postgresql -DatabaseName "   " } | Should -Throw "*must not be empty*"
    }

    It "rejects names outside the safe identifier charset" {
        foreach ($name in @("bad-name", "bad name", "x';DROP DATABASE d;--", "name`n")) {
            { Assert-SafeRestoreDatabaseName -DatabaseEngine mssql -DatabaseName $name } | Should -Throw "*unsupported characters*"
        }
    }

    It "rejects every reserved name for the selected engine even though the charset is valid" {
        foreach ($name in @("postgres", "template0", "template1", "TEMPLATE0")) {
            { Assert-SafeRestoreDatabaseName -DatabaseEngine postgresql -DatabaseName $name } | Should -Throw "*reserved postgresql system database*"
        }
        foreach ($name in @("master", "model", "msdb", "tempdb", "Master")) {
            { Assert-SafeRestoreDatabaseName -DatabaseEngine mssql -DatabaseName $name } | Should -Throw "*reserved mssql system database*"
        }
    }

    It "names the caller-supplied purpose in the failure so a refusal identifies which selection was rejected" {
        { Assert-SafeRestoreDatabaseName -DatabaseEngine mssql -DatabaseName "master" -Purpose "restore target" } | Should -Throw "*restore target database name 'master' is a reserved*"
    }
}

Describe "New-RestoreGeneratedDatabaseName" {
    It "produces prefix plus a 12-hex-character unpredictable suffix" {
        $name = New-RestoreGeneratedDatabaseName -Prefix "edfi_dms_restore_scratch"
        $name | Should -Match '^edfi_dms_restore_scratch_[0-9a-f]{12}$'
    }

    It "produces a different name on each call" {
        (New-RestoreGeneratedDatabaseName -Prefix "edfi_test") | Should -Not -Be (New-RestoreGeneratedDatabaseName -Prefix "edfi_test")
    }

    It "rejects a prefix outside the lowercase safe charset" {
        { New-RestoreGeneratedDatabaseName -Prefix "Edfi_Bad" } | Should -Throw "*prefix*"
        { New-RestoreGeneratedDatabaseName -Prefix "1leading" } | Should -Throw "*prefix*"
        { New-RestoreGeneratedDatabaseName -Prefix "has-dash" } | Should -Throw "*prefix*"
    }

    It "rejects a prefix that would exceed the PostgreSQL identifier limit" {
        { New-RestoreGeneratedDatabaseName -Prefix ("a" * 60) } | Should -Throw "*63-character*"
    }

    It "provides the scratch and preflight product prefixes through dedicated wrappers" {
        (New-RestoreScratchDatabaseName) | Should -Match '^edfi_dms_restore_scratch_[0-9a-f]{12}$'
        (New-RestorePreflightDatabaseName) | Should -Match '^edfi_dms_restore_preflight_[0-9a-f]{12}$'
    }

    It "generates names that pass the safe-name assertion on both engines" {
        $name = New-RestorePreflightDatabaseName
        { Assert-SafeRestoreDatabaseName -DatabaseEngine postgresql -DatabaseName $name } | Should -Not -Throw
        { Assert-SafeRestoreDatabaseName -DatabaseEngine mssql -DatabaseName $name } | Should -Not -Throw
    }
}

Describe "Get-RestoreSchemaNameExclusion" {
    # The two purposes deliberately disagree on PostgreSQL "public" and SQL Server "dbo".
    # DumpDiscovery mirrors the existing template dump discovery: package contents are scoped
    # to discovered user schemas, so "public"/"dbo" are excluded. InventoryEnumeration feeds
    # the DMS-only content gates, which must SEE those always-present schemas so contamination
    # hidden inside them is visible; only permitted extension bootstrap (e.g. pgcrypto created
    # by the template's own CREATE EXTENSION line) is allowed there by the gate.
    It "excludes PostgreSQL 'public' from dump discovery, matching the existing template dump scope" {
        $exclusion = Get-RestoreSchemaNameExclusion -DatabaseEngine postgresql -Purpose DumpDiscovery
        $exclusion.ExcludedSchemaName | Should -Contain "public"
        $exclusion.ExcludedSchemaName | Should -Contain "information_schema"
        $exclusion.ExcludedSchemaNamePrefix | Should -Be @("pg_")
    }

    It "includes PostgreSQL 'public' in inventory enumeration so the DMS-only gate can see contamination there" {
        $exclusion = Get-RestoreSchemaNameExclusion -DatabaseEngine postgresql -Purpose InventoryEnumeration
        $exclusion.ExcludedSchemaName | Should -Not -Contain "public"
        $exclusion.ExcludedSchemaName | Should -Contain "information_schema"
        $exclusion.ExcludedSchemaNamePrefix | Should -Be @("pg_")
    }

    It "excludes SQL Server 'dbo' from dump discovery but includes it in inventory enumeration" {
        $dumpExclusion = Get-RestoreSchemaNameExclusion -DatabaseEngine mssql -Purpose DumpDiscovery
        $dumpExclusion.ExcludedSchemaName | Should -Contain "dbo"

        $inventoryExclusion = Get-RestoreSchemaNameExclusion -DatabaseEngine mssql -Purpose InventoryEnumeration
        $inventoryExclusion.ExcludedSchemaName | Should -Not -Contain "dbo"
    }

    It "always excludes the SQL Server built-in schemas and fixed-role schema prefix" {
        foreach ($purpose in @("DumpDiscovery", "InventoryEnumeration")) {
            $exclusion = Get-RestoreSchemaNameExclusion -DatabaseEngine mssql -Purpose $purpose
            foreach ($builtIn in @("guest", "sys", "INFORMATION_SCHEMA")) {
                $exclusion.ExcludedSchemaName | Should -Contain $builtIn
            }
            $exclusion.ExcludedSchemaNamePrefix | Should -Be @("db_")
        }
    }
}

Describe "Get-RestoreDocumentJsonBaselineType" {
    It "pins the current physical DocumentJson baseline per engine" {
        Get-RestoreDocumentJsonBaselineType -DatabaseEngine postgresql | Should -Be "jsonb"
        Get-RestoreDocumentJsonBaselineType -DatabaseEngine mssql | Should -Be "nvarchar"
    }
}

Describe "ConvertTo-CanonicalInventoryJson" {
    It "serializes to the exact canonical form: sorted schemas, type-then-name sorted objects, lowercased types, principals always present" {
        $json = ConvertTo-CanonicalInventoryJson -Inventory (New-TestInventory)

        $json | Should -Be ('{"schemas":[' +
            '{"schemaName":"dms","objects":[{"name":"uuidv5","type":"function"},{"name":"Document","type":"table"},{"name":"EffectiveSchema","type":"table"}]},' +
            '{"schemaName":"edfi","objects":[{"name":"AcademicWeek","type":"table"},{"name":"School","type":"table"}]}' +
            '],"principals":[]}')
    }

    It "produces identical output regardless of input ordering" {
        $reordered = @{
            schemas    = @(
                @{
                    schemaName = "dms"
                    objects    = @(
                        @{ name = "EffectiveSchema"; type = "table" },
                        @{ name = "Document"; type = "table" },
                        @{ name = "uuidv5"; type = "FUNCTION" }
                    )
                },
                @{
                    schemaName = "edfi"
                    objects    = @(
                        @{ name = "AcademicWeek"; type = "table" },
                        @{ name = "School"; type = "table" }
                    )
                }
            )
            principals = @()
        }

        (ConvertTo-CanonicalInventoryJson -Inventory $reordered) | Should -Be (ConvertTo-CanonicalInventoryJson -Inventory (New-TestInventory))
    }

    It "accepts ConvertFrom-Json output (PSCustomObject) and produces the same canonical form as hashtable input" {
        $roundTripped = (New-TestInventory | ConvertTo-Json -Depth 10) | ConvertFrom-Json
        (ConvertTo-CanonicalInventoryJson -Inventory $roundTripped) | Should -Be (ConvertTo-CanonicalInventoryJson -Inventory (New-TestInventory))
    }

    It "sorts principals ordinally" {
        $json = ConvertTo-CanonicalInventoryJson -Inventory (New-TestInventory -Principal @("zeta_reader", "alpha_writer"))
        $json.Contains('"principals":["alpha_writer","zeta_reader"]') | Should -BeTrue
    }

    It "escapes quotes, backslashes, and non-printable-ASCII characters deterministically" {
        $inventory = @{
            schemas = @(
                @{
                    schemaName = 'we"ird\schema'
                    objects    = @(@{ name = "nam`u{00E9}"; type = "table" })
                }
            )
        }

        $json = ConvertTo-CanonicalInventoryJson -Inventory $inventory
        # The e-acute input character must serialize as a lowercase four-digit unicode escape;
        # the expected text is concatenated so this test file itself stays ASCII.
        $expectedJson = '{"schemas":[{"schemaName":"we\"ird\\schema","objects":[{"name":"nam' + '\' + 'u00e9","type":"table"}]}],"principals":[]}'
        $json | Should -Be $expectedJson
    }

    It "throws on a duplicate schema name" {
        $inventory = @{
            schemas = @(
                @{ schemaName = "dms"; objects = @() },
                @{ schemaName = "dms"; objects = @() }
            )
        }
        { ConvertTo-CanonicalInventoryJson -Inventory $inventory } | Should -Throw "*duplicate schema entry 'dms'*"
    }

    It "throws on a duplicate (type, name) object pair, including across type casing" {
        $inventory = @{
            schemas = @(
                @{
                    schemaName = "dms"
                    objects    = @(
                        @{ name = "Document"; type = "table" },
                        @{ name = "Document"; type = "TABLE" }
                    )
                }
            )
        }
        { ConvertTo-CanonicalInventoryJson -Inventory $inventory } | Should -Throw "*duplicate object entry*"
    }

    It "throws on a duplicate or empty principal" {
        { ConvertTo-CanonicalInventoryJson -Inventory (New-TestInventory -Principal @("reader", "reader")) } | Should -Throw "*duplicate principal*"
        { ConvertTo-CanonicalInventoryJson -Inventory (New-TestInventory -Principal @(" ")) } | Should -Throw "*empty principal*"
    }

    It "throws when the schemas array or a required entry field is missing" {
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ principals = @() }) } | Should -Throw "*missing the required 'schemas'*"
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ schemaName = "dms"; objects = @(); extra = 1 }) }) } | Should -Not -Throw
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ objects = @() }) }) } | Should -Throw "*without a non-empty 'schemaName'*"
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ schemaName = "dms"; objects = @(@{ name = "x" }) }) }) } | Should -Throw "*both a non-empty 'name' and 'type'*"
    }

    It "rejects a scalar or singleton-object schemas value: the contract requires a real JSON array" {
        $singletonObject = @{ schemas = @{ schemaName = "dms"; objects = @() } }
        { ConvertTo-CanonicalInventoryJson -Inventory $singletonObject } | Should -Throw "*'schemas' must be a JSON array*"

        # And after a JSON round trip, where the singleton is a PSCustomObject.
        $roundTripped = ($singletonObject | ConvertTo-Json -Depth 10) | ConvertFrom-Json
        { ConvertTo-CanonicalInventoryJson -Inventory $roundTripped } | Should -Throw "*'schemas' must be a JSON array*"
    }

    It "rejects a schema entry with a missing, null, or non-array objects value" {
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ schemaName = "dms" }) }) } |
            Should -Throw "*schema 'dms' is missing its 'objects' array*"
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ schemaName = "dms"; objects = $null }) }) } |
            Should -Throw "*schema 'dms' is missing its 'objects' array*"
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ schemaName = "dms"; objects = @{ name = "x"; type = "table" } }) }) } |
            Should -Throw "*schema 'dms' 'objects' must be a JSON array*"
    }

    It "rejects a scalar principals value and null entries" {
        $scalarPrincipals = @{
            schemas    = @(@{ schemaName = "dms"; objects = @() })
            principals = "reader"
        }
        { ConvertTo-CanonicalInventoryJson -Inventory $scalarPrincipals } | Should -Throw "*'principals' must be a JSON array*"

        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @($null) }) } | Should -Throw "*null schema entry*"
        { ConvertTo-CanonicalInventoryJson -Inventory (@{ schemas = @(@{ schemaName = "dms"; objects = @($null) }) }) } |
            Should -Throw "*null object entry*"
        # Built untyped: a [string[]] helper parameter would coerce $null into "".
        $nullPrincipal = @{ schemas = @(@{ schemaName = "dms"; objects = @() }); principals = @($null) }
        { ConvertTo-CanonicalInventoryJson -Inventory $nullPrincipal } | Should -Throw "*null principal entry*"
    }
}

Describe "Get-CanonicalInventoryHash" {
    It "is the lowercase-hex SHA-256 of the canonical JSON's UTF-8 bytes" {
        $inventory = New-TestInventory
        $expectedBytes = [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-CanonicalInventoryJson -Inventory $inventory)))
        $expectedHash = [System.Convert]::ToHexString($expectedBytes).ToLowerInvariant()

        Get-CanonicalInventoryHash -Inventory $inventory | Should -Be $expectedHash
    }

    It "changes when any inventory entry changes" {
        $baseline = Get-CanonicalInventoryHash -Inventory (New-TestInventory)
        Get-CanonicalInventoryHash -Inventory (New-TestInventory -Principal @("reader")) | Should -Not -Be $baseline
    }
}

Describe "Assert-RestoreManifestShape" {
    It "accepts a fully valid PostgreSQL manifest" {
        { Assert-RestoreManifestShape -Manifest (New-ValidRestoreManifest -DatabaseEngine postgresql) } | Should -Not -Throw
    }

    It "accepts a fully valid MSSQL manifest" {
        { Assert-RestoreManifestShape -Manifest (New-ValidRestoreManifest -DatabaseEngine mssql) } | Should -Not -Throw
    }

    It "accepts the same manifest after a JSON round trip (PSCustomObject input)" {
        $roundTripped = (New-ValidRestoreManifest -DatabaseEngine mssql | ConvertTo-Json -Depth 10) | ConvertFrom-Json
        { Assert-RestoreManifestShape -Manifest $roundTripped } | Should -Not -Throw
    }

    It "rejects a manifest missing any required field, naming the field" {
        $requiredFields = @(
            "version", "packageId", "packageVersion", "databaseEngine", "templateKind",
            "dataStandardVersion", "contentProfile", "projects", "apiSchemaFormatVersion",
            "effectiveSchemaHash", "resourceKeyCount", "resourceKeySeedHashB64",
            "relationalMappingVersion", "engineVersion", "documentJsonColumnType",
            "inventory", "inventorySha256", "artifactFileName", "artifactSha256"
        )
        foreach ($field in $requiredFields) {
            $manifest = New-ValidRestoreManifest
            $manifest.Remove($field)
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'$field'*" -Because "field '$field' is required"
        }
    }

    It "rejects a non-integer or unsupported manifest version" {
        $manifest = New-ValidRestoreManifest
        $manifest.version = "1"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'version' must be an integer*"

        $manifest = New-ValidRestoreManifest
        $manifest.version = 2
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*only version 1 is supported*"
    }

    It "rejects an unknown databaseEngine and a wrongly cased one" {
        foreach ($engine in @("oracle", "PostgreSQL", "MSSQL")) {
            $manifest = New-ValidRestoreManifest
            $manifest.databaseEngine = $engine
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'databaseEngine' must be exactly*"
        }
    }

    It "rejects an unknown templateKind and a wrongly cased one" {
        foreach ($kind in @("Full", "minimal", "POPULATED")) {
            $manifest = New-ValidRestoreManifest
            $manifest.templateKind = $kind
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'templateKind' must be exactly*"
        }
    }

    It "rejects any contentProfile other than the exact DmsDatastoreOnly literal" {
        foreach ($contentProfileValue in @("dmsdatastoreonly", "Everything", "DmsDatastoreOnly ")) {
            $manifest = New-ValidRestoreManifest
            $manifest.contentProfile = $contentProfileValue
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'contentProfile' must be exactly 'DmsDatastoreOnly'*"
        }
    }

    It "rejects a scalar projects value: the contract requires a real JSON array" {
        $manifest = New-ValidRestoreManifest
        $manifest.projects = "ed-fi"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'projects' must be a non-empty JSON array*"

        # The same strictness must hold after a JSON round trip (PSCustomObject input).
        $roundTripped = ($manifest | ConvertTo-Json -Depth 10) | ConvertFrom-Json
        { Assert-RestoreManifestShape -Manifest $roundTripped } | Should -Throw "*'projects' must be a non-empty JSON array*"
    }

    It "rejects an empty, duplicate-bearing, or non-string projects array" {
        $manifest = New-ValidRestoreManifest
        $manifest.projects = @()
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'projects' must be a non-empty JSON array*"

        $manifest = New-ValidRestoreManifest
        $manifest.projects = @("ed-fi", "ED-FI")
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*duplicate entry*"

        $manifest = New-ValidRestoreManifest
        $manifest.projects = @("ed-fi", 5)
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*not a non-empty JSON string*"
    }

    It "rejects hash fields that are not 64-character lowercase hex, including a trailing newline" {
        foreach ($field in @("effectiveSchemaHash", "inventorySha256", "artifactSha256")) {
            $manifest = New-ValidRestoreManifest
            $manifest[$field] = ("AB" * 32)
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'$field' must be a 64-character lowercase hex*"

            $manifest = New-ValidRestoreManifest
            $manifest[$field] = "abc123"
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'$field' must be a 64-character lowercase hex*"

            # \z anchoring: the .NET $ anchor would tolerate a single trailing newline.
            $manifest = New-ValidRestoreManifest
            $manifest[$field] = ("ab" * 32) + "`n"
            { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'$field' must be a 64-character lowercase hex*"
        }
    }

    It "rejects a non-positive or non-integer resourceKeyCount" {
        $manifest = New-ValidRestoreManifest
        $manifest.resourceKeyCount = 0
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'resourceKeyCount' must be a positive integer*"

        $manifest = New-ValidRestoreManifest
        $manifest.resourceKeyCount = "42"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'resourceKeyCount' must be a positive integer*"
    }

    It "rejects a resourceKeySeedHashB64 that is not base64 or not 32 bytes" {
        $manifest = New-ValidRestoreManifest
        $manifest.resourceKeySeedHashB64 = "not base64!!"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*not valid base64*"

        $manifest = New-ValidRestoreManifest
        $manifest.resourceKeySeedHashB64 = [System.Convert]::ToBase64String([byte[]](1..16))
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*exactly 32 bytes*"
    }

    It "requires databaseCompatibilityLevel for mssql and forbids it for postgresql" {
        $manifest = New-ValidRestoreManifest -DatabaseEngine mssql
        $manifest.Remove("databaseCompatibilityLevel")
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'databaseCompatibilityLevel' is required for mssql*"

        $manifest = New-ValidRestoreManifest -DatabaseEngine mssql
        $manifest.databaseCompatibilityLevel = 80
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'databaseCompatibilityLevel' is required for mssql*"

        $manifest = New-ValidRestoreManifest -DatabaseEngine postgresql
        $manifest.databaseCompatibilityLevel = 160
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*must be omitted or null for postgresql*"
    }

    It "rejects a manifest whose recomputed canonical inventory hash differs from inventorySha256" {
        $manifest = New-ValidRestoreManifest
        $manifest.inventorySha256 = ("00" * 32)
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*internally inconsistent*"
    }

    It "rejects an artifactFileName carrying path separators or the wrong extension for the engine" {
        $manifest = New-ValidRestoreManifest
        $manifest.artifactFileName = "nested/dump.sql"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*'artifactFileName' contains unsupported characters*"

        $manifest = New-ValidRestoreManifest -DatabaseEngine postgresql
        $manifest.artifactFileName = "backup.bak"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*must end with '.sql' for databaseEngine 'postgresql'*"

        $manifest = New-ValidRestoreManifest -DatabaseEngine mssql
        $manifest.artifactFileName = "dump.sql"
        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Throw "*must end with '.bak' for databaseEngine 'mssql'*"
    }
}

Describe "Read-RestoreManifest" {
    It "reads and validates a manifest file round-tripped through JSON" {
        $manifestPath = Join-Path $TestDrive "restore-manifest.json"
        New-ValidRestoreManifest -DatabaseEngine mssql | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8

        $manifest = Read-RestoreManifest -Path $manifestPath
        $manifest.packageId | Should -Be "EdFi.Api.Minimal.Template.MsSql.5.2.0"
        $manifest.templateKind | Should -Be "Minimal"
    }

    It "fails with restore-eligibility guidance when the manifest file is absent (legacy package)" {
        { Read-RestoreManifest -Path (Join-Path $TestDrive "missing.json") } | Should -Throw "*not eligible for restore*"
    }

    It "fails on an empty or malformed manifest file" {
        $emptyPath = Join-Path $TestDrive "empty.json"
        Set-Content -LiteralPath $emptyPath -Value "" -Encoding utf8 -NoNewline
        { Read-RestoreManifest -Path $emptyPath } | Should -Throw "*is empty*"

        $malformedPath = Join-Path $TestDrive "malformed.json"
        Set-Content -LiteralPath $malformedPath -Value "{ not json" -Encoding utf8
        { Read-RestoreManifest -Path $malformedPath } | Should -Throw "*not valid JSON*"
    }
}

Describe "ConvertFrom-MssqlBackupFileList" {
    It "collects every data and log logical name, skipping blank and short rows" {
        $fileList = ConvertFrom-MssqlBackupFileList -BackupFileName "backup.bak" -FileListOutput @(
            "",
            "MyDb|/var/opt/mssql/data/MyDb.mdf|D|PRIMARY",
            "shortrow",
            "MyDb2|/var/opt/mssql/data/MyDb2.ndf|D|SECONDARY",
            "MyDb_log|/var/opt/mssql/data/MyDb_log.ldf|L|NULL",
            "MyDb_log2|/var/opt/mssql/data/MyDb_log2.ldf|L|NULL"
        )

        $fileList.DataLogicalNames | Should -Be @("MyDb", "MyDb2")
        $fileList.LogLogicalNames | Should -Be @("MyDb_log", "MyDb_log2")
    }

    It "throws the file-list diagnostic, naming the backup, when output is null, empty, or one-sided" {
        { ConvertFrom-MssqlBackupFileList -BackupFileName "b.bak" -FileListOutput $null } | Should -Throw "*Could not determine the data and log logical file names from backup 'b.bak'*"
        { ConvertFrom-MssqlBackupFileList -BackupFileName "b.bak" -FileListOutput @() } | Should -Throw "*Could not determine*"
        { ConvertFrom-MssqlBackupFileList -BackupFileName "b.bak" -FileListOutput @("OnlyData|/p|D|X") } | Should -Throw "*Could not determine*"
        { ConvertFrom-MssqlBackupFileList -BackupFileName "b.bak" -FileListOutput @("OnlyLog|/p|L|X") } | Should -Throw "*Could not determine*"
    }
}

Describe "New-MssqlRestoreMoveClause" {
    It "keeps the plain database-derived names for the primary data file and first log" {
        $clauses = New-MssqlRestoreMoveClause -DatabaseName "testdb" -DataLogicalNames @("MyDb") -LogLogicalNames @("MyDb_log") -BackupFileName "backup.bak"
        $clauses | Should -Be @(
            "MOVE N'MyDb' TO N'/var/opt/mssql/data/testdb.mdf'",
            "MOVE N'MyDb_log' TO N'/var/opt/mssql/data/testdb_log.ldf'"
        )
    }

    It "suffixes every additional file with its own logical name (multi-file golden)" {
        $clauses = New-MssqlRestoreMoveClause -DatabaseName "testdb" `
            -DataLogicalNames @("MyDb", "MyDb2") `
            -LogLogicalNames @("MyDb_log", "MyDb_log2") `
            -BackupFileName "backup.bak"

        $clauses | Should -Be @(
            "MOVE N'MyDb' TO N'/var/opt/mssql/data/testdb.mdf'",
            "MOVE N'MyDb2' TO N'/var/opt/mssql/data/testdb_MyDb2.ndf'",
            "MOVE N'MyDb_log' TO N'/var/opt/mssql/data/testdb_log.ldf'",
            "MOVE N'MyDb_log2' TO N'/var/opt/mssql/data/testdb_MyDb_log2.ldf'"
        )
    }

    It "escapes single quotes in a primary logical name inside the N'' literal" {
        $clauses = New-MssqlRestoreMoveClause -DatabaseName "testdb" -DataLogicalNames @("My'Db") -LogLogicalNames @("MyDb_log") -BackupFileName "backup.bak"
        $clauses[0] | Should -Be "MOVE N'My''Db' TO N'/var/opt/mssql/data/testdb.mdf'"
    }

    It "rejects an additional data or log logical name outside the safe path charset" {
        { New-MssqlRestoreMoveClause -DatabaseName "testdb" -DataLogicalNames @("MyDb", "bad'name") -LogLogicalNames @("MyDb_log") -BackupFileName "b.bak" } |
            Should -Throw "*Data file logical name*contains unsupported characters*"
        { New-MssqlRestoreMoveClause -DatabaseName "testdb" -DataLogicalNames @("MyDb") -LogLogicalNames @("MyDb_log", "bad log") -BackupFileName "b.bak" } |
            Should -Throw "*Log file logical name*contains unsupported characters*"
    }

    It "rejects an additional logical name with a trailing newline (\z anchoring)" {
        { New-MssqlRestoreMoveClause -DatabaseName "testdb" -DataLogicalNames @("MyDb", "MyDb2`n") -LogLogicalNames @("MyDb_log") -BackupFileName "b.bak" } |
            Should -Throw "*Data file logical name*contains unsupported characters*"
    }

    It "rejects an unsafe database name" {
        { New-MssqlRestoreMoveClause -DatabaseName "bad-db" -DataLogicalNames @("d") -LogLogicalNames @("l") -BackupFileName "b.bak" } |
            Should -Throw "*Database name 'bad-db' contains unsupported characters*"
    }
}

Describe "Get-SourceIdentityReseedSql" {
    It "builds the MSSQL reseed that requires exactly the singleton row, joined with LF" {
        $sql = Get-SourceIdentityReseedSql -DatabaseEngine mssql
        $sql | Should -Be (@(
                "SET NOCOUNT ON;",
                "UPDATE [dms].[DataStoreIdentity]",
                "SET [SourceIdentity] = NEWID()",
                "WHERE [DataStoreIdentitySingletonId] = 1;",
                "IF @@ROWCOUNT <> 1",
                "    THROW 50000, N'Restored database is missing the dms.DataStoreIdentity singleton row.', 1;"
            ) -join "`n")
        $sql | Should -Not -Match "`r"
    }

    It "builds the PostgreSQL reseed with gen_random_uuid and the row-count guard, joined with LF" {
        $sql = Get-SourceIdentityReseedSql -DatabaseEngine postgresql
        $sql | Should -Match 'UPDATE "dms"\."DataStoreIdentity"'
        $sql | Should -Match 'SET "SourceIdentity" = gen_random_uuid\(\)'
        $sql | Should -Match 'GET DIAGNOSTICS _updated_count = ROW_COUNT'
        $sql | Should -Match "RAISE EXCEPTION 'Restored database is missing the dms.DataStoreIdentity singleton row.'"
        $sql | Should -Not -Match "`r"
    }
}

Describe "Get-SourceIdentitySelectSql" {
    It "selects every SourceIdentity row as text per engine" {
        Get-SourceIdentitySelectSql -DatabaseEngine mssql | Should -Be "SET NOCOUNT ON; SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
        Get-SourceIdentitySelectSql -DatabaseEngine postgresql | Should -Be 'SELECT "SourceIdentity"::text FROM "dms"."DataStoreIdentity";'
    }
}

Describe "Test-RestoredSourceIdentityValue" {
    BeforeAll {
        $script:packageIdentity = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
    }

    It "is valid for exactly one well-formed UUID row that differs from the package value" {
        $verdict = Test-RestoredSourceIdentityValue -SourceIdentityRow @("7c9e6679-7425-40de-944b-e07fc1f90ae7") -PackageSourceIdentity $script:packageIdentity
        $verdict.IsValid | Should -BeTrue
    }

    It "ignores blank transport rows around the single value" {
        $verdict = Test-RestoredSourceIdentityValue -SourceIdentityRow @("", " 7c9e6679-7425-40de-944b-e07fc1f90ae7 ", "") -PackageSourceIdentity $script:packageIdentity
        $verdict.IsValid | Should -BeTrue
    }

    It "fails when zero rows are present" {
        $verdict = Test-RestoredSourceIdentityValue -SourceIdentityRow @() -PackageSourceIdentity $script:packageIdentity
        $verdict.IsValid | Should -BeFalse
        $verdict.Reason | Should -Be "Expected exactly one dms.DataStoreIdentity row after restore, found 0."
    }

    It "fails when more than one row is present" {
        $verdict = Test-RestoredSourceIdentityValue `
            -SourceIdentityRow @("7c9e6679-7425-40de-944b-e07fc1f90ae7", "9b2c1b0a-2f6a-4d5e-8b8e-0a1b2c3d4e5f") `
            -PackageSourceIdentity $script:packageIdentity
        $verdict.IsValid | Should -BeFalse
        $verdict.Reason | Should -Be "Expected exactly one dms.DataStoreIdentity row after restore, found 2."
    }

    It "fails when the stored value is not a UUID" {
        $verdict = Test-RestoredSourceIdentityValue -SourceIdentityRow @("not-a-uuid") -PackageSourceIdentity $script:packageIdentity
        $verdict.IsValid | Should -BeFalse
        $verdict.Reason | Should -Be "Restored dms.DataStoreIdentity.SourceIdentity is not a valid UUID."
    }

    It "fails when the stored value is the empty UUID" {
        $verdict = Test-RestoredSourceIdentityValue -SourceIdentityRow @("00000000-0000-0000-0000-000000000000") -PackageSourceIdentity $script:packageIdentity
        $verdict.IsValid | Should -BeFalse
        $verdict.Reason | Should -Be "Restored dms.DataStoreIdentity.SourceIdentity is the empty UUID."
    }

    It "fails when the stored value still equals the package value, regardless of casing or brace format" {
        foreach ($spelling in @($script:packageIdentity.ToUpperInvariant(), "{$($script:packageIdentity)}")) {
            $verdict = Test-RestoredSourceIdentityValue -SourceIdentityRow @($spelling) -PackageSourceIdentity $script:packageIdentity
            $verdict.IsValid | Should -BeFalse
            $verdict.Reason | Should -Be "Restored dms.DataStoreIdentity.SourceIdentity still matches the package value; the reseed did not take effect."
        }
    }

    It "throws when the package value itself is not a UUID (caller defect, not a target verdict)" {
        { Test-RestoredSourceIdentityValue -SourceIdentityRow @("7c9e6679-7425-40de-944b-e07fc1f90ae7") -PackageSourceIdentity "garbage" } |
            Should -Throw "*is not a valid UUID*"
    }
}

Describe "inventory catalog query SQL builders" {
    It "scopes the schema query to the purpose: dump discovery excludes public/dbo, inventory enumeration includes them" {
        $pgDump = Get-InventorySchemaQuerySql -DatabaseEngine postgresql -Purpose DumpDiscovery
        $pgDump | Should -BeLike "*'public'*"
        $pgDump | Should -BeLike "*'information_schema'*"

        $pgInventory = Get-InventorySchemaQuerySql -DatabaseEngine postgresql -Purpose InventoryEnumeration
        $pgInventory | Should -Not -BeLike "*'public'*"
        $pgInventory | Should -BeLike "*'information_schema'*"

        $mssqlDump = Get-InventorySchemaQuerySql -DatabaseEngine mssql -Purpose DumpDiscovery
        $mssqlDump | Should -BeLike "*'dbo'*"

        $mssqlInventory = Get-InventorySchemaQuerySql -DatabaseEngine mssql -Purpose InventoryEnumeration
        $mssqlInventory | Should -Not -BeLike "*'dbo'*"
        $mssqlInventory.Contains("NOT LIKE 'db[_]%'") | Should -BeTrue
    }

    It "filters only allow-listed extension objects in the PostgreSQL object query and keeps overloads and triggers distinct" {
        $sql = Get-InventoryObjectQuerySql -DatabaseEngine postgresql
        $sql | Should -BeLike "*pgcrypto*"
        $sql | Should -BeLike "*pg_get_function_identity_arguments*"
        $sql | Should -BeLike "*tgisinternal*"
        $sql | Should -BeLike "*deptype = 'e'*"
    }

    It "excludes shipped objects and maps type codes in the MSSQL object query" {
        $sql = Get-InventoryObjectQuerySql -DatabaseEngine mssql
        $sql | Should -BeLike "*is_ms_shipped = 0*"
        $sql | Should -BeLike "*WHEN 'U' THEN 'table'*"
        $sql | Should -BeLike "*WHEN 'TR' THEN 'trigger'*"
    }

    It "excludes the built-in public role and fixed roles from the MSSQL principal query" {
        $sql = Get-InventoryPrincipalQuerySql -DatabaseEngine mssql
        $sql | Should -BeLike "*is_fixed_role = 0*"
        $sql | Should -BeLike "*'public'*"
    }

    It "rejects an unsafe database name in the compatibility-level query" {
        { Get-DatabaseCompatibilityLevelQuerySql -DatabaseName "bad'name" } | Should -Throw "*unsupported characters*"
        (Get-DatabaseCompatibilityLevelQuerySql -DatabaseName "edfi_dms") | Should -BeLike "*N'edfi_dms'*"
    }

    It "enumerates every dms DocumentJson carrier from the live catalog rather than pinning one table" {
        $pgSql = Get-DocumentJsonColumnTypeQuerySql -DatabaseEngine postgresql
        $pgSql | Should -BeLike "*information_schema.columns*DocumentJson*"
        $pgSql | Should -BeLike "*table_schema = 'dms'*"
        $pgSql | Should -Not -BeLike "*table_name = *"

        $mssqlSql = Get-DocumentJsonColumnTypeQuerySql -DatabaseEngine mssql
        $mssqlSql | Should -BeLike "*sys.columns*DocumentJson*"
        $mssqlSql | Should -BeLike "*s.name = N'dms'*"
        $mssqlSql | Should -Not -BeLike "*OBJECT_ID(*"
    }
}

Describe "ConvertFrom-DocumentJsonColumnTypeRow" {
    It "returns the single physical type when dms.DocumentCache is the carrier" {
        ConvertFrom-DocumentJsonColumnTypeRow -Row @("DocumentCache|jsonb") | Should -Be "jsonb"
        ConvertFrom-DocumentJsonColumnTypeRow -Row @("", "DocumentCache|NVARCHAR") | Should -Be "nvarchar"
    }

    It "accepts additional carriers only when every carrier reports the same type" {
        ConvertFrom-DocumentJsonColumnTypeRow -Row @("Document|jsonb", "DocumentCache|jsonb") | Should -Be "jsonb"
        { ConvertFrom-DocumentJsonColumnTypeRow -Row @("Document|varchar", "DocumentCache|jsonb") } |
            Should -Throw "*do not share one physical storage type*Document=varchar*DocumentCache=jsonb*"
    }

    It "rejects an empty result, a missing DocumentCache carrier, and malformed rows" {
        { ConvertFrom-DocumentJsonColumnTypeRow -Row @() } | Should -Throw "*No DocumentJson column was found*"
        { ConvertFrom-DocumentJsonColumnTypeRow -Row @("SomethingElse|jsonb") } | Should -Throw "*dms.DocumentCache.DocumentJson was not found*"
        { ConvertFrom-DocumentJsonColumnTypeRow -Row @("DocumentCache") } | Should -Throw "*malformed row*"
    }
}

Describe "Assert-CompleteRestoreArtifactScope" {
    BeforeAll {
        $script:fullPartition = Get-TemplateProjectSchemaPartition -DatabaseEngine postgresql -SchemaName @(
            "dms", "auth", "edfi", "tpdm", "tracked_changes_edfi", "tracked_changes_tpdm", "public"
        )
    }

    It "accepts an artifact scope carrying the complete validated datastore" {
        { Assert-CompleteRestoreArtifactScope -Partition $script:fullPartition -ArtifactSchemaName @(
                "dms", "auth", "edfi", "tpdm", "tracked_changes_edfi", "tracked_changes_tpdm"
            ) } | Should -Not -Throw
    }

    It "accepts a dms-only artifact when the validated source has no resource schemas" {
        $dmsOnlyPartition = Get-TemplateProjectSchemaPartition -DatabaseEngine postgresql -SchemaName @("dms", "public")
        { Assert-CompleteRestoreArtifactScope -Partition $dmsOnlyPartition -ArtifactSchemaName @("dms") } | Should -Not -Throw
    }

    It "rejects an artifact scope missing resource schemas, companions, auth, or dms itself" {
        { Assert-CompleteRestoreArtifactScope -Partition $script:fullPartition -ArtifactSchemaName @("dms") } |
            Should -Throw "*would omit required DMS-owned schemas: auth, edfi, tpdm, tracked_changes_edfi, tracked_changes_tpdm*-DumpAllUserSchemas*"

        { Assert-CompleteRestoreArtifactScope -Partition $script:fullPartition -ArtifactSchemaName @(
                "dms", "auth", "edfi", "tpdm", "tracked_changes_edfi"
            ) } | Should -Throw "*would omit required DMS-owned schemas: tracked_changes_tpdm*"

        { Assert-CompleteRestoreArtifactScope -Partition $script:fullPartition -ArtifactSchemaName @(
                "auth", "edfi", "tpdm", "tracked_changes_edfi", "tracked_changes_tpdm"
            ) } | Should -Throw "*would omit required DMS-owned schemas: dms*"
    }
}

Describe "ConvertFrom-InventoryQueryRow" {
    It "builds the inventory from rows, preserving zero-object schemas" {
        $inventory = ConvertFrom-InventoryQueryRow `
            -SchemaRow @("dms", "edfi", "public", "") `
            -ObjectRow @("dms|Document|table", "edfi|School|table", "") `
            -PrincipalRow @()

        $schemaNames = @($inventory.schemas | ForEach-Object { $_.schemaName })
        $schemaNames | Should -Be @("dms", "edfi", "public")
        @(($inventory.schemas | Where-Object { $_.schemaName -eq "public" }).objects).Count | Should -Be 0
        @(($inventory.schemas | Where-Object { $_.schemaName -eq "dms" }).objects).Count | Should -Be 1
        @($inventory.principals).Count | Should -Be 0
    }

    It "rejects malformed object rows and objects in unreported schemas" {
        { ConvertFrom-InventoryQueryRow -SchemaRow @("dms") -ObjectRow @("dms|Document") -PrincipalRow @() } |
            Should -Throw "*malformed row*"
        { ConvertFrom-InventoryQueryRow -SchemaRow @("dms") -ObjectRow @("ghost|Thing|table") -PrincipalRow @() } |
            Should -Throw "*which the schema query did not report*"
        { ConvertFrom-InventoryQueryRow -SchemaRow @("dms", "dms") -ObjectRow @() -PrincipalRow @() } |
            Should -Throw "*duplicate schema*"
    }

    It "round-trips through the canonical inventory serializer" {
        $inventory = ConvertFrom-InventoryQueryRow `
            -SchemaRow @("edfi", "dms") `
            -ObjectRow @("dms|Document|table") `
            -PrincipalRow @("custom_reader")
        { ConvertTo-CanonicalInventoryJson -Inventory $inventory } | Should -Not -Throw
    }
}

Describe "Select-InventorySchemaScope" {
    BeforeAll {
        $script:scopedSource = @{
            schemas    = @(
                @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                @{ schemaName = "public"; objects = @() }
            )
            principals = @("someone")
        }
    }

    It "keeps only the named schemas and optionally drops principals" {
        $scoped = Select-InventorySchemaScope -Inventory $script:scopedSource -SchemaName @("dms", "public") -ExcludePrincipals
        @($scoped.schemas | ForEach-Object { $_.schemaName }) | Should -Be @("dms", "public")
        @($scoped.principals).Count | Should -Be 0

        $withPrincipals = Select-InventorySchemaScope -Inventory $script:scopedSource -SchemaName @("dms")
        @($withPrincipals.principals) | Should -Be @("someone")
    }

    It "silently skips scope names absent from the inventory" {
        $scoped = Select-InventorySchemaScope -Inventory $script:scopedSource -SchemaName @("dms", "absent")
        @($scoped.schemas | ForEach-Object { $_.schemaName }) | Should -Be @("dms")
    }
}

Describe "ConvertFrom-EffectiveSchemaRow" {
    It "parses the singleton row and converts the seed hash from hex to base64" {
        $parsed = ConvertFrom-EffectiveSchemaRow -Row @("", "1.0.0|$('ab' * 32)|42|$('CD' * 32)")
        $parsed.ApiSchemaFormatVersion | Should -Be "1.0.0"
        $parsed.EffectiveSchemaHash | Should -Be ("ab" * 32)
        $parsed.ResourceKeyCount | Should -Be 42
        $parsed.ResourceKeySeedHashB64 | Should -Be ([System.Convert]::ToBase64String([System.Convert]::FromHexString(("cd" * 32))))
    }

    It "rejects zero rows, multiple rows, and malformed fields" {
        { ConvertFrom-EffectiveSchemaRow -Row @() } | Should -Throw "*Expected exactly one dms.EffectiveSchema row, found 0*"
        { ConvertFrom-EffectiveSchemaRow -Row @("a|b|1|cc", "a|b|1|cc") } | Should -Throw "*found 2*"
        { ConvertFrom-EffectiveSchemaRow -Row @("1.0.0|$('ab' * 32)|42") } | Should -Throw "*malformed*"
        { ConvertFrom-EffectiveSchemaRow -Row @("1.0.0|NOTAHASH|42|$('cd' * 32)") } | Should -Throw "*EffectiveSchemaHash*"
        { ConvertFrom-EffectiveSchemaRow -Row @("1.0.0|$('ab' * 32)|zero|$('cd' * 32)") } | Should -Throw "*ResourceKeyCount*"
        { ConvertFrom-EffectiveSchemaRow -Row @("1.0.0|$('ab' * 32)|42|deadbeef") } | Should -Throw "*ResourceKeySeedHash*"
    }
}

Describe "Get-TemplateProjectSchemaPartition" {
    It "partitions the DMS-owned roles and orders projects core-first" {
        $partition = Get-TemplateProjectSchemaPartition -DatabaseEngine postgresql -SchemaName @(
            "tracked_changes_tpdm", "tpdm", "public", "edfi", "dms", "auth", "tracked_changes_edfi"
        )

        $partition.HasDms | Should -BeTrue
        $partition.HasAuth | Should -BeTrue
        $partition.AlwaysPresentSchemaName | Should -Be @("public")
        $partition.TrackedChangesProjectNames | Should -Be @("edfi", "tpdm")
        $partition.ResourceSchemaNames | Should -Be @("edfi", "tpdm")
        $partition.ProjectSchemaNames | Should -Be @("edfi", "tpdm")
    }

    It "partitions lookalike companion names as resource schemas so pairing can reject them" {
        $partition = Get-TemplateProjectSchemaPartition -DatabaseEngine mssql -SchemaName @(
            "dms", "dbo", "tracked_changesx", "tracked_changes_"
        )
        $partition.AlwaysPresentSchemaName | Should -Be @("dbo")
        $partition.ResourceSchemaNames | Should -Be @("tracked_changes_", "tracked_changesx")
        @($partition.TrackedChangesProjectNames).Count | Should -Be 0
    }
}

Describe "Assert-DmsOnlyInventory" {
    BeforeAll {
        function script:New-GateInventory {
            param (
                [object[]]$Schema,
                [string[]]$Principal = @()
            )
            return @{ schemas = @($Schema); principals = $Principal }
        }

        $script:cleanSchemas = @(
            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
            @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
            @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
            @{ schemaName = "public"; objects = @() }
        )
    }

    It "accepts a clean full-database inventory and returns the partition" {
        $partition = Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $script:cleanSchemas)
        $partition.ProjectSchemaNames | Should -Be @("edfi")
    }

    It "accepts a dms-plus-public artifact-scope inventory (no resource schemas)" {
        $schemas = @(
            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
            @{ schemaName = "public"; objects = @() }
        )
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $schemas) } | Should -Not -Throw
    }

    It "rejects the Configuration Service schema dmscs" {
        $schemas = $script:cleanSchemas + @(@{ schemaName = "dmscs"; objects = @(@{ name = "Application"; type = "table" }) })
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $schemas) } |
            Should -Throw "*contains the Configuration Service schema 'dmscs'*"
    }

    It "rejects OpenIddict identity-state objects anywhere, case-insensitively" {
        $schemas = @(
            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }, @{ name = "OPENIDDICTKey"; type = "table" }) },
            @{ schemaName = "public"; objects = @() }
        )
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $schemas) } |
            Should -Throw "*identity-state object 'dms.OPENIDDICTKey'*"
    }

    It "rejects lookalike and unpaired schemas through companion pairing" {
        $auth2 = $script:cleanSchemas + @(@{ schemaName = "auth2"; objects = @() })
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $auth2) } |
            Should -Throw "*schema 'auth2' has no tracked_changes_auth2 companion*"

        $lookalikeCompanion = $script:cleanSchemas + @(@{ schemaName = "tracked_changesx"; objects = @() })
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $lookalikeCompanion) } |
            Should -Throw "*schema 'tracked_changesx' has no tracked_changes_tracked_changesx companion*"

        $orphanCompanion = $script:cleanSchemas + @(@{ schemaName = "tracked_changes_ghost"; objects = @() })
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $orphanCompanion) } |
            Should -Throw "*companion schema 'tracked_changes_ghost' has no matching resource schema*"
    }

    It "rejects content hidden in the always-present public/dbo schemas" {
        $publicContaminated = @(
            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
            @{ schemaName = "public"; objects = @(@{ name = "evil"; type = "table" }) }
        )
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $publicContaminated) } |
            Should -Throw "*always-present 'public' schema contains unexpected objects*evil*"

        $dboContaminated = @(
            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
            @{ schemaName = "dbo"; objects = @(@{ name = "evil"; type = "table" }) }
        )
        { Assert-DmsOnlyInventory -DatabaseEngine mssql -Inventory (New-GateInventory -Schema $dboContaminated) } |
            Should -Throw "*always-present 'dbo' schema contains unexpected objects*evil*"
    }

    It "rejects unexpected database principals" {
        { Assert-DmsOnlyInventory -DatabaseEngine mssql -Inventory (New-GateInventory -Schema $script:cleanSchemas -Principal @("copied_cms_user")) } |
            Should -Throw "*unexpected database principals: copied_cms_user*"
    }

    It "rejects a missing or empty dms schema" {
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema @(@{ schemaName = "public"; objects = @() })) } |
            Should -Throw "*the 'dms' schema is missing*"
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema @(@{ schemaName = "dms"; objects = @() })) } |
            Should -Throw "*the 'dms' schema contains no objects*"
    }

    It "rejects resource schemas without the core edfi schema" {
        $schemas = @(
            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
            @{ schemaName = "tpdm"; objects = @() },
            @{ schemaName = "tracked_changes_tpdm"; objects = @() },
            @{ schemaName = "public"; objects = @() }
        )
        { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $schemas) } |
            Should -Throw "*the core resource schema 'edfi' is missing*"
    }

    It "aggregates multiple violations into one message with the source description" {
        $schemas = @(
            @{ schemaName = "dmscs"; objects = @() },
            @{ schemaName = "public"; objects = @(@{ name = "evil"; type = "table" }) }
        )
        $failure = { Assert-DmsOnlyInventory -DatabaseEngine postgresql -Inventory (New-GateInventory -Schema $schemas) -SourceDescription "Scratch database 'x'" }
        $failure | Should -Throw "*Scratch database 'x' is not a dedicated DMS datastore*"
        $failure | Should -Throw "*dmscs*"
        $failure | Should -Throw "*'dms' schema is missing*"
        $failure | Should -Throw "*unexpected objects*"
    }
}

Describe "Get-RelationalMappingVersionFromSource" {
    It "reads the authoritative constant from the real repo source file" {
        $constantsPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/core/EdFi.DataManagementService.Core/Utilities/SchemaHashConstants.cs"))
        Get-RelationalMappingVersionFromSource -SchemaHashConstantsPath $constantsPath | Should -Be "v2"
    }

    It "fails on a missing file or an ambiguous constant" {
        { Get-RelationalMappingVersionFromSource -SchemaHashConstantsPath (Join-Path $TestDrive "absent.cs") } |
            Should -Throw "*was not found*"

        $ambiguousPath = Join-Path $TestDrive "Ambiguous.cs"
        @(
            'public const string RelationalMappingVersion = "v2";',
            'public const string RelationalMappingVersion = "v3";'
        ) | Set-Content -LiteralPath $ambiguousPath -Encoding utf8
        { Get-RelationalMappingVersionFromSource -SchemaHashConstantsPath $ambiguousPath } |
            Should -Throw "*Expected exactly one RelationalMappingVersion constant*found 2*"
    }
}

Describe "New-TemplateRestoreManifest" {
    BeforeAll {
        function script:New-ManifestArgumentSet {
            param (
                [ValidateSet("postgresql", "mssql")]
                [string]$DatabaseEngine = "postgresql"
            )

            $arguments = @{
                PackageId                = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
                PackageVersion           = "1.0.123"
                DatabaseEngine           = $DatabaseEngine
                TemplateKind             = "Minimal"
                DataStandardVersion      = "5.2.0"
                ProjectName              = [string[]]@("edfi")
                ApiSchemaFormatVersion   = "1.0.0"
                EffectiveSchemaHash      = ("ab" * 32)
                ResourceKeyCount         = 42
                ResourceKeySeedHashB64   = [System.Convert]::ToBase64String([byte[]](1..32))
                RelationalMappingVersion = "v2"
                EngineVersion            = "16.8"
                DocumentJsonColumnType   = "jsonb"
                Inventory                = @{
                    schemas    = @(
                        @{ schemaName = "public"; objects = @() },
                        @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "TABLE" }) }
                    )
                    principals = @()
                }
                ArtifactFileName         = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql"
                ArtifactSha256           = ("cd" * 32)
            }
            if ($DatabaseEngine -eq "mssql") {
                $arguments.PackageId = "EdFi.Api.Minimal.Template.MsSql.5.2.0"
                $arguments.EngineVersion = "17.0.900.7"
                $arguments.DatabaseCompatibilityLevel = 170
                $arguments.DocumentJsonColumnType = "nvarchar"
                $arguments.ArtifactFileName = "EdFi.Api.Minimal.Template.MsSql.5.2.0.bak"
            }
            return $arguments
        }
    }

    It "produces a shape-valid manifest with the canonical inventory embedded in sorted order" {
        $arguments = New-ManifestArgumentSet
        $manifest = New-TemplateRestoreManifest @arguments

        { Assert-RestoreManifestShape -Manifest $manifest } | Should -Not -Throw

        # Embedded inventory is canonical: schemas sorted, types lowercased.
        @($manifest.inventory.schemas | ForEach-Object { $_.schemaName }) | Should -Be @("dms", "public")
        $manifest.inventory.schemas[0].objects[0].type | Should -Be "table"
        $manifest.inventorySha256 | Should -Be (Get-CanonicalInventoryHash -Inventory $arguments.Inventory)

        # And it survives a JSON round trip through the consumer-side validator.
        $roundTripped = ($manifest | ConvertTo-Json -Depth 10) | ConvertFrom-Json
        { Assert-RestoreManifestShape -Manifest $roundTripped } | Should -Not -Throw
    }

    It "enforces the engine-conditional compatibility-level rules at assembly time" {
        $missingCompatibility = New-ManifestArgumentSet -DatabaseEngine mssql
        $missingCompatibility.Remove("DatabaseCompatibilityLevel")
        { New-TemplateRestoreManifest @missingCompatibility } | Should -Throw "*DatabaseCompatibilityLevel is required for mssql*"

        $unwantedCompatibility = New-ManifestArgumentSet -DatabaseEngine postgresql
        $unwantedCompatibility.DatabaseCompatibilityLevel = 160
        { New-TemplateRestoreManifest @unwantedCompatibility } | Should -Throw "*must not be supplied for postgresql*"
    }
}

Describe "Assert-RestoreManifestMatchesDatabase" {
    BeforeAll {
        function script:New-MatchingFactsAndManifest {
            param (
                [ValidateSet("postgresql", "mssql")]
                [string]$DatabaseEngine = "postgresql"
            )

            $inventory = @{
                schemas    = @(
                    @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                    @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                    @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                    @{ schemaName = $(if ($DatabaseEngine -eq "mssql") { "dbo" } else { "public" }); objects = @() }
                )
                principals = @()
            }

            $facts = [pscustomobject]@{
                ApiSchemaFormatVersion     = "1.0.0"
                EffectiveSchemaHash        = ("ab" * 32)
                ResourceKeyCount           = 42
                ResourceKeySeedHashB64     = [System.Convert]::ToBase64String([byte[]](1..32))
                EngineVersion              = $(if ($DatabaseEngine -eq "mssql") { "17.0.900.7" } else { "16.8" })
                DatabaseCompatibilityLevel = $(if ($DatabaseEngine -eq "mssql") { 170 } else { $null })
                DocumentJsonColumnType     = (Get-RestoreDocumentJsonBaselineType -DatabaseEngine $DatabaseEngine)
                FullInventory              = $inventory
                ArtifactInventory          = $inventory
            }

            $manifestArguments = @{
                PackageId                = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
                PackageVersion           = "1.0.123"
                DatabaseEngine           = $DatabaseEngine
                TemplateKind             = "Minimal"
                DataStandardVersion      = "5.2.0"
                ProjectName              = [string[]]@("edfi")
                ApiSchemaFormatVersion   = $facts.ApiSchemaFormatVersion
                EffectiveSchemaHash      = $facts.EffectiveSchemaHash
                ResourceKeyCount         = $facts.ResourceKeyCount
                ResourceKeySeedHashB64   = $facts.ResourceKeySeedHashB64
                RelationalMappingVersion = "v2"
                EngineVersion            = $facts.EngineVersion
                DocumentJsonColumnType   = $facts.DocumentJsonColumnType
                Inventory                = $facts.ArtifactInventory
                ArtifactFileName         = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql"
                ArtifactSha256           = ("cd" * 32)
            }
            if ($DatabaseEngine -eq "mssql") {
                $manifestArguments.PackageId = "EdFi.Api.Minimal.Template.MsSql.5.2.0"
                $manifestArguments.DatabaseCompatibilityLevel = 170
                $manifestArguments.ArtifactFileName = "EdFi.Api.Minimal.Template.MsSql.5.2.0.bak"
            }

            return [pscustomobject]@{
                Facts    = $facts
                Manifest = (New-TemplateRestoreManifest @manifestArguments)
            }
        }
    }

    It "passes when the database facts match the manifest exactly on both engines" {
        foreach ($engine in @("postgresql", "mssql")) {
            $pair = New-MatchingFactsAndManifest -DatabaseEngine $engine
            { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine $engine } |
                Should -Not -Throw
        }
    }

    It "reports each effective-schema field mismatch by name" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.EffectiveSchemaHash = ("ee" * 32)
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*effectiveSchemaHash: manifest*"

        $pair = New-MatchingFactsAndManifest
        $pair.Facts.ApiSchemaFormatVersion = "9.9.9"
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*apiSchemaFormatVersion: manifest '1.0.0' vs database '9.9.9'*"

        $pair = New-MatchingFactsAndManifest
        $pair.Facts.ResourceKeyCount = 41
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*resourceKeyCount: manifest '42' vs database '41'*"

        $pair = New-MatchingFactsAndManifest
        $pair.Facts.ResourceKeySeedHashB64 = [System.Convert]::ToBase64String([byte[]](32..63))
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*resourceKeySeedHashB64*differs*"
    }

    It "reports the physical-baseline mismatches: DocumentJson type against the manifest and against the engine baseline, and the MSSQL compatibility level" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.DocumentJsonColumnType = "varchar"
        $failure = { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql }
        $failure | Should -Throw "*documentJsonColumnType: manifest 'jsonb' vs database 'varchar'*"
        $failure | Should -Throw "*database 'varchar' vs the postgresql baseline 'jsonb'*"

        $pair = New-MatchingFactsAndManifest -DatabaseEngine mssql
        $pair.Facts.DatabaseCompatibilityLevel = 160
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine mssql } |
            Should -Throw "*databaseCompatibilityLevel: manifest '170' vs database '160'*"
    }

    It "reports an inventory divergence through the independently recomputed canonical hash" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.ArtifactInventory = @{
            schemas    = @(
                @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }, @{ name = "Contaminant"; type = "table" }) },
                @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                @{ schemaName = "public"; objects = @() }
            )
            principals = @()
        }
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*inventorySha256: manifest*independently derived*"
    }

    It "aggregates multiple mismatches under the caller's database description" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.EffectiveSchemaHash = ("ee" * 32)
        $pair.Facts.ResourceKeyCount = 1
        $failure = { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql -DatabaseDescription "Scratch database 'x'" }
        $failure | Should -Throw "*Scratch database 'x' does not match the restore manifest*"
        $failure | Should -Throw "*effectiveSchemaHash*"
        $failure | Should -Throw "*resourceKeyCount*"
    }

    It "accepts a live server whose major version is at or above the manifest's engine major" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.EngineVersion = "17.2 (Debian 17.2-1.pgdg120+1)"
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Not -Throw
    }

    It "rejects a live server whose major version is below the manifest's engine major on both engines" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.EngineVersion = "15.4"
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*engineVersion: the live server major 15 is lower than the manifest's engine major 16*cannot be restored on an older server*"

        $pair = New-MatchingFactsAndManifest -DatabaseEngine mssql
        $pair.Facts.EngineVersion = "16.0.1000.6"
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine mssql } |
            Should -Throw "*engineVersion: the live server major 16 is lower than the manifest's engine major 17*"
    }

    It "rejects an unparsable engine version on either side instead of silently passing" {
        $pair = New-MatchingFactsAndManifest
        $pair.Facts.EngineVersion = "unknown"
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $pair.Facts -DatabaseEngine postgresql } |
            Should -Throw "*engineVersion: cannot parse a leading major version*"
    }

    It "reports a manifest whose engine differs from the selected engine" {
        $pair = New-MatchingFactsAndManifest -DatabaseEngine postgresql
        $mssqlFacts = (New-MatchingFactsAndManifest -DatabaseEngine mssql).Facts
        { Assert-RestoreManifestMatchesDatabase -Manifest $pair.Manifest -Facts $mssqlFacts -DatabaseEngine mssql } |
            Should -Throw "*databaseEngine: manifest 'postgresql' vs selected engine 'mssql'*"
    }
}
