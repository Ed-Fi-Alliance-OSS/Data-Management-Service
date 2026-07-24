# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Covers the PowerShell wrapper Invoke-ConnectionStringInspection's handling of the ADDITIVE, non-secret
# 'endpoint' classification the `connection inspect` verb emits (Iteration 2, no locality enforcement):
#   * compatibility - an older tool that predates the projection is tolerated by default (provisioning path);
#   * the -RequireEndpointIdentity version gate - the endpoint-aware consumer requires the projection;
#   * the typed/state contract - a valid result carries a complete, typed endpoint; an invalid result carries
#     a null endpoint; a malformed projection is a tool-contract/version failure, not data;
#   * secret safety - the projection (and the whole result) never carries the password.
# Contract cases use stub validators (canned JSON) for speed; a real-provider context runs the actual
# api-schema-tools verb, mirroring RuntimeConfigContract's fail-not-skip build prerequisite.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    $script:stubDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-endpoint-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:stubDir -Force | Out-Null

    # Writes a .ps1 "validator" that consumes the piped connection string and emits a fixed JSON line, so a
    # test can drive Invoke-ConnectionStringInspection's contract checks without the real tool. A .ps1 path is
    # dispatched via `pwsh -File` (env-utility.psm1), so the connection string arrives on stdin.
    function script:New-StubValidator {
        param([Parameter(Mandatory)][string]$Json)
        $path = Join-Path $script:stubDir ("stub-" + [guid]::NewGuid().ToString("N") + ".ps1")
        @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
`$null = @(`$input)
Write-Output '$Json'
exit 0
"@ | Set-Content -LiteralPath $path -Encoding utf8
        return $path
    }

    # Build the real exact-provider tool once (fail, never skip - the Bootstrap Pester lane has the SDK, as
    # RuntimeConfigContract also requires).
    $script:schemaProject = [System.IO.Path]::GetFullPath(
        (Join-Path $script:composeRoot "../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj")
    )
    if (-not (Test-Path -LiteralPath $script:schemaProject)) {
        throw "api-schema-tools project not found at '$script:schemaProject'; the real-provider endpoint tests cannot run."
    }
    & dotnet build $script:schemaProject -c Release --nologo 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build api-schema-tools (dotnet build exit $LASTEXITCODE); the real-provider endpoint tests must build, not skip."
    }
    $script:schemaTool = Get-ChildItem -Path (Join-Path (Split-Path $script:schemaProject) "bin/Release") -Recurse -File |
        Where-Object { $_.Name -eq "api-schema-tools.exe" -or $_.Name -eq "api-schema-tools" } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $script:schemaTool) {
        throw "api-schema-tools executable not found under bin/Release after build."
    }
}

AfterAll {
    if ($script:stubDir -and (Test-Path -LiteralPath $script:stubDir)) {
        Remove-Item -LiteralPath $script:stubDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe "Invoke-ConnectionStringInspection endpoint projection (contract, stubbed tool)" {
    It "tolerates an older tool that omits the endpoint projection when -RequireEndpointIdentity is not set" {
        $stub = New-StubValidator -Json '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null}'
        $result = Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub
        $result.valid | Should -BeTrue
        ($result.PSObject.Properties.Name -contains 'endpoint') | Should -BeFalse -Because "the old tool emitted no endpoint and the wrapper does not invent one"
    }

    It "fails when -RequireEndpointIdentity is set but the tool omits the endpoint projection" {
        $stub = New-StubValidator -Json '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null}'
        { Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub -RequireEndpointIdentity } |
            Should -Throw "*missing the 'endpoint' projection*"
    }

    It "returns a valid result carrying a well-formed endpoint (with and without -RequireEndpointIdentity)" {
        $stub = New-StubValidator -Json '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}'
        $result = Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub -RequireEndpointIdentity
        $result.endpoint.kind | Should -Be 'singleHost'
        $result.endpoint.host | Should -Be 'dms-postgresql'
        $result.endpoint.hasAlternateRouting | Should -BeFalse
        # Without the switch, the same well-formed projection is returned unchanged.
        (Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub).endpoint.kind | Should -Be 'singleHost'
    }

    It "accepts an invalid result that carries a null endpoint" {
        $stub = New-StubValidator -Json '{"valid":false,"database":null,"host":null,"port":null,"username":null,"error":"bad connection","endpoint":null}'
        $result = Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub -RequireEndpointIdentity
        $result.valid | Should -BeFalse
        $result.endpoint | Should -BeNullOrEmpty
    }

    It "rejects a valid result whose endpoint projection is null" {
        $stub = New-StubValidator -Json '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":null}'
        { Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub } |
            Should -Throw "*valid result with a null 'endpoint' projection*"
    }

    It "rejects a valid result whose endpoint kind is not a recognized classification" {
        $stub = New-StubValidator -Json '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"bogus","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}'
        { Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub } |
            Should -Throw "*endpoint.kind*not a recognized classification*"
    }

    It "rejects an invalid result that carries a non-null endpoint projection" {
        $stub = New-StubValidator -Json '{"valid":false,"database":null,"host":null,"port":null,"username":null,"error":"bad","endpoint":{"kind":"singleHost","protocol":"tcp","host":"h","port":1,"instance":null,"hasAlternateRouting":false}}'
        { Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub } |
            Should -Throw "*invalid result with a non-null 'endpoint' projection*"
    }
}

Describe "Invoke-ConnectionStringInspection endpoint projection (real api-schema-tools provider)" {
    It "classifies a PostgreSQL single TCP host and emits no secret" {
        $result = Invoke-ConnectionStringInspection `
            -Engine postgresql `
            -ConnectionString 'Host=dms-postgresql;Port=5432;Username=postgres;Password=sup3rSecretValue;Database=edfi' `
            -SchemaToolPath $script:schemaTool `
            -RequireEndpointIdentity
        $result.valid | Should -BeTrue
        $result.endpoint.kind | Should -Be 'singleHost'
        $result.endpoint.protocol | Should -Be 'tcp'
        $result.endpoint.host | Should -Be 'dms-postgresql'
        $result.endpoint.port | Should -Be 5432
        $result.endpoint.hasAlternateRouting | Should -BeFalse
        ($result | ConvertTo-Json -Depth 6) | Should -Not -Match 'sup3rSecretValue' -Because "the endpoint projection is non-secret"
    }

    It "splits the SQL Server host and port from the data source while the top-level port stays null" {
        $result = Invoke-ConnectionStringInspection `
            -Engine mssql `
            -ConnectionString 'Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=sup3rSecretValue;TrustServerCertificate=true' `
            -SchemaToolPath $script:schemaTool `
            -RequireEndpointIdentity
        $result.port | Should -BeNullOrEmpty -Because "SQL Server keeps the port inside the data source (existing contract)"
        $result.endpoint.kind | Should -Be 'singleHost'
        $result.endpoint.host | Should -Be 'dms-mssql'
        $result.endpoint.port | Should -Be 1433
    }

    It "flags SQL Server alternate routing when a failover partner is present" {
        $result = Invoke-ConnectionStringInspection `
            -Engine mssql `
            -ConnectionString 'Server=dms-mssql,1433;Failover Partner=remote-mssql;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true' `
            -SchemaToolPath $script:schemaTool `
            -RequireEndpointIdentity
        $result.endpoint.hasAlternateRouting | Should -BeTrue
        $result.endpoint.host | Should -Be 'dms-mssql'
    }
}

Describe "Invoke-ConnectionStringInspection endpoint state model (engine-aware, case-sensitive)" {
    # The endpoint-aware consumer trusts this version gate, so a self-contradictory or engine-invalid
    # projection must be rejected as a tool-contract failure rather than passed through as data. Each row is a
    # provider-VALID result (the six base fields are engine-consistent: PG carries an integer port, SQL Server
    # a null port) whose endpoint projection is internally incoherent or invalid for the engine.
    It "rejects <Case>" -ForEach @(
        @{ Case = "singleHost with a null host (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":null,"port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "missing with a populated host and port (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"missing","protocol":"default","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "a named instance on PostgreSQL (engine-invalid)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"namedInstance","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":"X","hasAlternateRouting":false}}' }
        @{ Case = "a multi-host list on SQL Server (engine-invalid)"; Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"dms-mssql,1433","port":null,"username":"sa","error":null,"endpoint":{"kind":"multiHost","protocol":"tcp","host":null,"port":null,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "an unsupported kind paired with the tcp protocol (SQL Server)"; Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"dms-mssql,1433","port":null,"username":"sa","error":null,"endpoint":{"kind":"unsupported","protocol":"tcp","host":null,"port":null,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "a wrong-case kind token (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"SingleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "a wrong-case protocol token (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"TCP","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "singleHost with a whitespace host (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"   ","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "singleHost with a zero port (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":0,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "singleHost with an out-of-range port (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":70000,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "singleHost with the default protocol on PostgreSQL (must be tcp)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"default","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "a PostgreSQL result carrying alternate routing"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":true}}' }
        @{ Case = "namedInstance with a blank instance (SQL Server)"; Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"dms-mssql","port":null,"username":"sa","error":null,"endpoint":{"kind":"namedInstance","protocol":"tcp","host":"dms-mssql","port":null,"instance":"   ","hasAlternateRouting":false}}' }
        @{ Case = "namedInstance with an out-of-range port (SQL Server)"; Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"dms-mssql","port":null,"username":"sa","error":null,"endpoint":{"kind":"namedInstance","protocol":"tcp","host":"dms-mssql","port":70000,"instance":"SQLEXPRESS","hasAlternateRouting":false}}' }
        @{ Case = "missing with the tcp protocol (SQL Server)"; Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"dms-mssql,1433","port":null,"username":"sa","error":null,"endpoint":{"kind":"missing","protocol":"tcp","host":null,"port":null,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "an array-valued kind that stringifies to a valid token (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":["singleHost"],"protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "an array-valued protocol (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":["tcp"],"host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
        @{ Case = "singleHost with an empty (non-null) instance string (PostgreSQL)"; Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":"","hasAlternateRouting":false}}' }
    ) {
        $stub = New-StubValidator -Json $Json
        { Invoke-ConnectionStringInspection -Engine $Engine -ConnectionString 'x' -SchemaToolPath $stub -RequireEndpointIdentity } |
            Should -Throw
    }

    It "reports a controlled tool-contract failure (not a raw conversion error) for a port beyond Int32" {
        # A [long] port past Int32 must be compared in range and rejected with the rebuild/re-publish guidance,
        # never a raw .NET conversion error from narrowing the value to [int].
        $stub = New-StubValidator -Json '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":2147483648,"instance":null,"hasAlternateRouting":false}}'
        { Invoke-ConnectionStringInspection -Engine postgresql -ConnectionString 'x' -SchemaToolPath $stub -RequireEndpointIdentity } |
            Should -Throw "*re-publish api-schema-tools*"
    }

    It "accepts the coherent per-engine shapes" {
        # Positive controls the negative table must not over-reject: PG single-host, PG multi-host, MSSQL
        # named instance, and MSSQL unsupported (non-TCP) all validate.
        $accepts = @(
            @{ Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"dms-postgresql","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"singleHost","protocol":"tcp","host":"dms-postgresql","port":5432,"instance":null,"hasAlternateRouting":false}}' }
            @{ Engine = 'postgresql'; Json = '{"valid":true,"database":"edfi","host":"primary,standby","port":5432,"username":"postgres","error":null,"endpoint":{"kind":"multiHost","protocol":"tcp","host":null,"port":null,"instance":null,"hasAlternateRouting":false}}' }
            @{ Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"dms-mssql\\SQLEXPRESS","port":null,"username":"sa","error":null,"endpoint":{"kind":"namedInstance","protocol":"default","host":"dms-mssql","port":null,"instance":"SQLEXPRESS","hasAlternateRouting":false}}' }
            @{ Engine = 'mssql'; Json = '{"valid":true,"database":"edfi","host":"np:dms-mssql","port":null,"username":"sa","error":null,"endpoint":{"kind":"unsupported","protocol":"namedPipes","host":null,"port":null,"instance":null,"hasAlternateRouting":false}}' }
        )
        foreach ($row in $accepts) {
            $stub = New-StubValidator -Json $row.Json
            { Invoke-ConnectionStringInspection -Engine $row.Engine -ConnectionString 'x' -SchemaToolPath $stub -RequireEndpointIdentity } |
                Should -Not -Throw -Because "the coherent shape for $($row.Engine) must be accepted"
        }
    }
}
