# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The Instance Management E2E orchestration threads the engine and resolved
# environment, provisions the three route-context databases in a fixed order, registers a suite-owned
# CMS fixture, restarts DMS exactly once after registration, and runs the routed tests inside a
# test-process context. These tests AST-extract the build-dms.ps1 orchestration functions and invoke
# them against mocked leaf boundaries, and assert the setup/dispatch ordering from source where the
# call site is a non-executed orchestration line.

param()

Describe "Get-InstanceE2ETestEnvironmentContext resolves engine, databases, and registration strings (DMS-1284)" {
    BeforeAll {
        function Get-BuildScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        # Import the real modules so the env/connection helper commands exist and can be mocked; the
        # extracted function's own Import-Module calls are mocked to no-ops in BeforeEach. The real
        # Get-EnvValue is used against the mocked ReadValuesFromEnvFile output. database-safety is
        # imported for real so the up-front Assert-E2EDatabaseIsDedicated gate runs against the real
        # safe-name / dedicated-database rules rather than a stub.
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))) -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../env-utility.psm1"))) -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../database-safety.psm1"))) -Force
        # Get-InstanceE2ETestEnvironmentContext resolves its base env file through
        # Resolve-InstanceE2EBaseEnvironmentFile, so that helper must be defined too; with an explicit
        # -EnvironmentFile it delegates to the mocked Resolve-LocalSettingsEnvironmentFile leaf.
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Resolve-InstanceE2EBaseEnvironmentFile")))
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Get-InstanceE2ETestEnvironmentContext")))
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
        Remove-Module database-safety -Force -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $script:contextEnvValues = @{
            "INSTANCE_E2E_DATABASE_1_NAME" = "edfi_datamanagementservice_d255901_sy2024"
            "INSTANCE_E2E_DATABASE_2_NAME" = "edfi_datamanagementservice_d255901_sy2025"
            "INSTANCE_E2E_DATABASE_3_NAME" = "edfi_datamanagementservice_d255902_sy2024"
        }

        Mock Import-Module { }
        Mock Resolve-LocalSettingsEnvironmentFile { "/resolved/base.env" }
        Mock Resolve-DataStandardEnvironmentFile { $BaseEnvironmentFile }
        Mock Resolve-DatabaseEngineEnvironmentFile { $BaseEnvironmentFile }
        Mock ReadValuesFromEnvFile { $script:contextEnvValues }
        Mock New-E2EDataStoreConnectionStrings {
            [pscustomobject]@{
                AdminConnectionString        = "admin:${DatabaseEngine}:$DatabaseName"
                RegistrationConnectionString = "reg:${DatabaseEngine}:$DatabaseName"
            }
        }
    }

    It "normalizes an omitted engine to postgresql" {
        $context = Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine ""
        $context.DatabaseEngine | Should -Be "postgresql"
    }

    It "carries the explicit mssql engine and builds mssql registration strings" {
        $context = Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "mssql"
        $context.DatabaseEngine | Should -Be "mssql"
        $context.RegistrationConnectionStrings | Should -HaveCount 3
        $context.RegistrationConnectionStrings[0] | Should -Be "reg:mssql:edfi_datamanagementservice_d255901_sy2024"
        $context.RegistrationConnectionStrings[2] | Should -Be "reg:mssql:edfi_datamanagementservice_d255902_sy2024"
    }

    It "returns the three resolved database names in order" {
        $context = Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql"
        $context.DatabaseNames | Should -Be @(
            "edfi_datamanagementservice_d255901_sy2024",
            "edfi_datamanagementservice_d255901_sy2025",
            "edfi_datamanagementservice_d255902_sy2024"
        )
    }

    It "honors custom route database names from the resolved environment" {
        $script:contextEnvValues["INSTANCE_E2E_DATABASE_1_NAME"] = "custom_route_one"
        $script:contextEnvValues["INSTANCE_E2E_DATABASE_3_NAME"] = "custom_route_three"

        $context = Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql"

        $context.DatabaseNames[0] | Should -Be "custom_route_one"
        $context.DatabaseNames[2] | Should -Be "custom_route_three"
        $context.RegistrationConnectionStrings[0] | Should -Be "reg:postgresql:custom_route_one"
    }

    It "throws when a route database name is missing" {
        $script:contextEnvValues.Remove("INSTANCE_E2E_DATABASE_2_NAME")
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw -ExpectedMessage "*INSTANCE_E2E_DATABASE_2_NAME*"
    }

    It "throws when the route database names are not distinct" {
        $script:contextEnvValues["INSTANCE_E2E_DATABASE_2_NAME"] = $script:contextEnvValues["INSTANCE_E2E_DATABASE_1_NAME"]
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw -ExpectedMessage "*must be distinct*"
    }

    It "rejects a route database name with unsafe characters before building any connection string" {
        $script:contextEnvValues["INSTANCE_E2E_DATABASE_1_NAME"] = "bad-name!"
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw -ExpectedMessage "*unsupported characters*"
        Should -Invoke New-E2EDataStoreConnectionStrings -Times 0 -Exactly
    }

    It "rejects a reserved PostgreSQL system database name" {
        $script:contextEnvValues["INSTANCE_E2E_DATABASE_2_NAME"] = "postgres"
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw -ExpectedMessage "*reserved PostgreSQL*"
        Should -Invoke New-E2EDataStoreConnectionStrings -Times 0 -Exactly
    }

    It "rejects a reserved SQL Server system database name" {
        $script:contextEnvValues["INSTANCE_E2E_DATABASE_3_NAME"] = "master"
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "mssql" } |
            Should -Throw -ExpectedMessage "*reserved SQL Server*"
        Should -Invoke New-E2EDataStoreConnectionStrings -Times 0 -Exactly
    }

    It "rejects a route database name that matches the protected primary database name" {
        $script:contextEnvValues["POSTGRES_DB_NAME"] = $script:contextEnvValues["INSTANCE_E2E_DATABASE_1_NAME"]
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw -ExpectedMessage "*must be dedicated*"
        Should -Invoke New-E2EDataStoreConnectionStrings -Times 0 -Exactly
    }

    It "rejects a route database name embedded in the protected CMS connection string" {
        $script:contextEnvValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] =
            "Host=dms-postgresql;Port=5432;Database=$($script:contextEnvValues['INSTANCE_E2E_DATABASE_2_NAME']);Username=postgres;Password=abcdefgh1!"
        { Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw -ExpectedMessage "*must stay separate*"
        Should -Invoke New-E2EDataStoreConnectionStrings -Times 0 -Exactly
    }

    It "builds all three registration strings when every route name is safe and dedicated" {
        Get-InstanceE2ETestEnvironmentContext -EnvironmentFile "./.env.routeContext.e2e" -DatabaseEngine "postgresql" | Out-Null
        Should -Invoke New-E2EDataStoreConnectionStrings -Times 3 -Exactly
    }
}

Describe "Register-InstanceE2EFixture registers the canonical suite-owned fixture (DMS-1284)" {
    BeforeAll {
        function Get-BuildScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        # Import the real modules so the CMS/env helper commands exist and can be mocked; the extracted
        # function's own Import-Module calls are shadowed to no-ops in BeforeEach.
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))) -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../env-utility.psm1"))) -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../database-safety.psm1"))) -Force
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Register-InstanceE2EFixture")))
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
        Remove-Module database-safety -Force -ErrorAction SilentlyContinue
    }

    BeforeEach {
        Mock Import-Module { }
        Mock Resolve-CmsBaseUrl { "http://localhost:8081" }
        Mock Resolve-BootstrapAdminClient { [pscustomobject]@{ ClientId = "dms-data-store-admin"; ClientSecret = "secret" } }
        Mock Add-CmsClient { }
        Mock Get-CmsToken { "fake-access-token" }
        Mock Get-EnvValue { $DefaultValue }
        # Mocked so an ambient POSTGRES_* value on the test host cannot leak into the fixture run.
        Mock Get-ComposeResolvedEnvValue { $DefaultValue }
        Mock ConvertTo-PostgresCredential { [System.Management.Automation.PSCredential]::new("postgres", [System.Security.SecureString]::new()) }
        Mock Add-Tenant { 1 }
        $script:vendorSeq = 0
        Mock Add-Vendor { $script:vendorSeq++; [long](100 + $script:vendorSeq) }
        $script:dataStoreSeq = 0
        Mock Add-DataStore { $script:dataStoreSeq++; [long](200 + $script:dataStoreSeq) }
        $script:contextSeq = 0
        Mock Add-DataStoreContext { $script:contextSeq++; [long](400 + $script:contextSeq) }
        $script:appSeq = 0
        Mock Add-Application { $script:appSeq++; @{ Id = [long](300 + $script:appSeq); Key = "key$($script:appSeq)"; Secret = "secret$($script:appSeq)" } }

        $script:settings = [pscustomobject]@{
            DatabaseEngine                = "mssql"
            DatabaseNames                 = @("db1", "db2", "db3")
            RegistrationConnectionStrings = @("reg-1", "reg-2", "reg-3")
            EnvironmentValues             = @{}
        }
    }

    It "creates the two canonical tenants" {
        Register-InstanceE2EFixture -InstanceE2ESettings $script:settings | Out-Null
        Should -Invoke Add-Tenant -Times 1 -Exactly -ParameterFilter { $TenantName -eq "Tenant_255901" }
        Should -Invoke Add-Tenant -Times 1 -Exactly -ParameterFilter { $TenantName -eq "Tenant_255902" }
    }

    It "registers exactly one vendor per tenant with a distinct, deterministic company name" {
        # CMS enforces a global UX_Vendor_Company uniqueness constraint, so the two fixture tenants must
        # register different company names. Capture the invoked companies to prove distinctness at runtime
        # rather than by matching the source text.
        $script:capturedVendorCompanies = @{}
        Mock Add-Vendor {
            $script:capturedVendorCompanies[$Tenant] = $Company
            $script:vendorSeq++
            [long](100 + $script:vendorSeq)
        }

        Register-InstanceE2EFixture -InstanceE2ESettings $script:settings | Out-Null

        Should -Invoke Add-Vendor -Times 2 -Exactly
        Should -Invoke Add-Vendor -Times 1 -Exactly -ParameterFilter {
            $Tenant -eq "Tenant_255901" -and $Company -eq "Instance E2E Fixture Vendor Tenant_255901"
        }
        Should -Invoke Add-Vendor -Times 1 -Exactly -ParameterFilter {
            $Tenant -eq "Tenant_255902" -and $Company -eq "Instance E2E Fixture Vendor Tenant_255902"
        }

        $script:capturedVendorCompanies["Tenant_255901"] |
            Should -Not -Be $script:capturedVendorCompanies["Tenant_255902"]
        ($script:capturedVendorCompanies.Values | Select-Object -Unique) | Should -HaveCount 2
    }

    It "registers exactly three data stores with the engine-correct registration strings" {
        Register-InstanceE2EFixture -InstanceE2ESettings $script:settings | Out-Null
        Should -Invoke Add-DataStore -Times 3 -Exactly
        Should -Invoke Add-DataStore -Times 1 -Exactly -ParameterFilter { $ConnectionString -eq "reg-1" -and $Tenant -eq "Tenant_255901" }
        Should -Invoke Add-DataStore -Times 1 -Exactly -ParameterFilter { $ConnectionString -eq "reg-2" -and $Tenant -eq "Tenant_255901" }
        Should -Invoke Add-DataStore -Times 1 -Exactly -ParameterFilter { $ConnectionString -eq "reg-3" -and $Tenant -eq "Tenant_255902" }
    }

    It "registers the districtId and schoolYear route contexts for each store (six total)" {
        Register-InstanceE2EFixture -InstanceE2ESettings $script:settings | Out-Null
        Should -Invoke Add-DataStoreContext -Times 6 -Exactly
        Should -Invoke Add-DataStoreContext -Times 1 -Exactly -ParameterFilter { $ContextKey -eq "districtId" -and $ContextValue -eq "255902" }
        Should -Invoke Add-DataStoreContext -Times 1 -Exactly -ParameterFilter { $ContextKey -eq "schoolYear" -and $ContextValue -eq "2025" }
    }

    It "creates one application per tenant with the E2E claim set" {
        Register-InstanceE2EFixture -InstanceE2ESettings $script:settings | Out-Null
        Should -Invoke Add-Application -Times 2 -Exactly
        Should -Invoke Add-Application -Times 2 -Exactly -ParameterFilter { $ClaimSetName -eq "E2E-NoFurtherAuthRequiredClaimSet" }
    }

    It "returns the fixture metadata and per-tenant credentials" {
        $fixture = Register-InstanceE2EFixture -InstanceE2ESettings $script:settings

        $fixture.Tenants | Should -HaveCount 2
        $fixture.Tenants[0].TenantName | Should -Be "Tenant_255901"
        $fixture.Tenants[0].DataStoreIds | Should -HaveCount 2
        $fixture.Tenants[1].TenantName | Should -Be "Tenant_255902"
        $fixture.Tenants[1].DataStoreIds | Should -HaveCount 1
        $fixture.Tenants[0].ClientKey | Should -Not -BeNullOrEmpty
        $fixture.Tenants[0].ClientSecret | Should -Not -BeNullOrEmpty
        $fixture.DataStoreIds | Should -HaveCount 3
        $fixture.ApplicationIds | Should -HaveCount 2
    }

    It "returns a structured route record per data store mapping tenant/district/schoolYear to the resolved database" {
        $fixture = Register-InstanceE2EFixture -InstanceE2ESettings $script:settings

        $fixture.Routes | Should -HaveCount 3

        $fixture.Routes[0].TenantName | Should -Be "Tenant_255901"
        $fixture.Routes[0].DistrictId | Should -Be "255901"
        $fixture.Routes[0].SchoolYear | Should -Be "2024"
        $fixture.Routes[0].DatabaseOrdinal | Should -Be 1
        $fixture.Routes[0].DatabaseName | Should -Be "db1"
        $fixture.Routes[0].DataStoreId | Should -Be 201

        $fixture.Routes[1].SchoolYear | Should -Be "2025"
        $fixture.Routes[1].DatabaseName | Should -Be "db2"

        $fixture.Routes[2].TenantName | Should -Be "Tenant_255902"
        $fixture.Routes[2].DistrictId | Should -Be "255902"
        $fixture.Routes[2].DatabaseName | Should -Be "db3"
        $fixture.Routes[2].DataStoreId | Should -Be 203
    }

    It "captures both route-context ids per route" {
        $fixture = Register-InstanceE2EFixture -InstanceE2ESettings $script:settings

        foreach ($route in $fixture.Routes) {
            $route.DistrictContextId | Should -BeGreaterThan 0
            $route.SchoolYearContextId | Should -BeGreaterThan 0
            $route.DistrictContextId | Should -Not -Be $route.SchoolYearContextId
        }
    }
}

Describe "Invoke-WithInstanceE2ETestProcessContext restores prior environment state exactly (DMS-1284)" {
    BeforeAll {
        function Get-BuildScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Invoke-WithInstanceE2ETestProcessContext")))

        $script:settings = [pscustomobject]@{
            DatabaseEngine                = "mssql"
            DatabaseNames                 = @("db1", "db2", "db3")
            RegistrationConnectionStrings = @(
                "Server=dms-mssql,1433;Database=db1;User Id=sa;Password=cs-secret1;TrustServerCertificate=true;",
                "Server=dms-mssql,1433;Database=db2;User Id=sa;Password=cs-secret2;TrustServerCertificate=true;",
                "Server=dms-mssql,1433;Database=db3;User Id=sa;Password=cs-secret3;TrustServerCertificate=true;"
            )
        }
        $script:fixture = [pscustomobject]@{
            Tenants        = @(
                [pscustomobject]@{ TenantName = "Tenant_255901"; VendorId = 101; ApplicationId = 301; ClientKey = "key1"; ClientSecret = "secret1" },
                [pscustomobject]@{ TenantName = "Tenant_255902"; VendorId = 102; ApplicationId = 302; ClientKey = "key2"; ClientSecret = "secret2" }
            )
            DataStoreIds   = @(201, 202, 203)
            ApplicationIds = @(301, 302)
            Routes         = @(
                [pscustomobject]@{ TenantName = "Tenant_255901"; DistrictId = "255901"; SchoolYear = "2024"; DatabaseOrdinal = 1; DatabaseName = "db1"; DataStoreId = 201; DistrictContextId = 401; SchoolYearContextId = 402 },
                [pscustomobject]@{ TenantName = "Tenant_255901"; DistrictId = "255901"; SchoolYear = "2025"; DatabaseOrdinal = 2; DatabaseName = "db2"; DataStoreId = 202; DistrictContextId = 403; SchoolYearContextId = 404 },
                [pscustomobject]@{ TenantName = "Tenant_255902"; DistrictId = "255902"; SchoolYear = "2024"; DatabaseOrdinal = 3; DatabaseName = "db3"; DataStoreId = 203; DistrictContextId = 405; SchoolYearContextId = 406 }
            )
        }
        $script:managedVariables = @(
            "INSTANCE_E2E_DATABASE_ENGINE", "INSTANCE_E2E_DATABASE_1_NAME", "INSTANCE_E2E_DATABASE_2_NAME",
            "INSTANCE_E2E_DATABASE_3_NAME",
            "INSTANCE_E2E_DATABASE_1_CONNECTION_STRING", "INSTANCE_E2E_DATABASE_2_CONNECTION_STRING",
            "INSTANCE_E2E_DATABASE_3_CONNECTION_STRING", "INSTANCE_E2E_ROUTE_MANIFEST",
            "INSTANCE_E2E_FIXTURE_TENANT_1_NAME", "INSTANCE_E2E_FIXTURE_TENANT_1_VENDOR_ID",
            "INSTANCE_E2E_FIXTURE_TENANT_1_APPLICATION_ID", "INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_KEY",
            "INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_SECRET", "INSTANCE_E2E_FIXTURE_TENANT_2_NAME",
            "INSTANCE_E2E_FIXTURE_TENANT_2_VENDOR_ID", "INSTANCE_E2E_FIXTURE_TENANT_2_APPLICATION_ID",
            "INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_KEY", "INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_SECRET",
            "INSTANCE_E2E_FIXTURE_DATASTORE_IDS"
        )
    }

    AfterEach {
        foreach ($name in $script:managedVariables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    }

    It "sets the engine, database names, and fixture credentials for the action" {
        $observed = $null
        Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action {
            $script:observed = [pscustomobject]@{
                Engine = $env:INSTANCE_E2E_DATABASE_ENGINE
                Db1    = $env:INSTANCE_E2E_DATABASE_1_NAME
                Key1   = $env:INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_KEY
                Secret2 = $env:INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_SECRET
                Stores = $env:INSTANCE_E2E_FIXTURE_DATASTORE_IDS
            }
        }

        $script:observed.Engine | Should -Be "mssql"
        $script:observed.Db1 | Should -Be "db1"
        $script:observed.Key1 | Should -Be "key1"
        $script:observed.Secret2 | Should -Be "secret2"
        $script:observed.Stores | Should -Be "201,202,203"
    }

    It "sets the three opaque engine-correct connection strings verbatim for the action" {
        $script:observedConnectionStrings = $null
        Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action {
            $script:observedConnectionStrings = @(
                $env:INSTANCE_E2E_DATABASE_1_CONNECTION_STRING
                $env:INSTANCE_E2E_DATABASE_2_CONNECTION_STRING
                $env:INSTANCE_E2E_DATABASE_3_CONNECTION_STRING
            )
        }

        $script:observedConnectionStrings[0] | Should -Be $script:settings.RegistrationConnectionStrings[0]
        $script:observedConnectionStrings[1] | Should -Be $script:settings.RegistrationConnectionStrings[1]
        $script:observedConnectionStrings[2] | Should -Be $script:settings.RegistrationConnectionStrings[2]
    }

    It "never leaks the secret-bearing connection strings into the non-secret route manifest" {
        $script:manifestForConnectionCheck = $null
        Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action {
            $script:manifestForConnectionCheck = $env:INSTANCE_E2E_ROUTE_MANIFEST
        }

        $script:manifestForConnectionCheck | Should -Not -Match "cs-secret1"
        $script:manifestForConnectionCheck | Should -Not -Match "cs-secret2"
        $script:manifestForConnectionCheck | Should -Not -Match "cs-secret3"
        $script:manifestForConnectionCheck | Should -Not -Match "Password="
    }

    It "restores prior connection-string states (absent, empty, whitespace, valued) verbatim after the action throws" {
        Remove-Item Env:INSTANCE_E2E_DATABASE_1_CONNECTION_STRING -ErrorAction SilentlyContinue
        $env:INSTANCE_E2E_DATABASE_2_CONNECTION_STRING = "   "
        $env:INSTANCE_E2E_DATABASE_3_CONNECTION_STRING = "prior-connection-string"

        { Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:INSTANCE_E2E_DATABASE_1_CONNECTION_STRING) | Should -BeFalse
        $env:INSTANCE_E2E_DATABASE_2_CONNECTION_STRING | Should -Be "   "
        $env:INSTANCE_E2E_DATABASE_3_CONNECTION_STRING | Should -Be "prior-connection-string"
    }

    It "exposes the three route mappings as a non-secret JSON manifest for the action" {
        $script:manifestJson = $null
        Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action {
            $script:manifestJson = $env:INSTANCE_E2E_ROUTE_MANIFEST
        }

        $manifest = $script:manifestJson | ConvertFrom-Json
        $manifest | Should -HaveCount 3
        $manifest[0].tenant | Should -Be "Tenant_255901"
        $manifest[0].districtId | Should -Be "255901"
        $manifest[0].schoolYear | Should -Be "2024"
        $manifest[0].databaseName | Should -Be "db1"
        $manifest[0].dataStoreId | Should -Be 201
        $manifest[0].districtContextId | Should -Be 401
        $manifest[2].tenant | Should -Be "Tenant_255902"
        $manifest[2].databaseName | Should -Be "db3"

        # The manifest is non-secret: it never carries client keys or secrets.
        $script:manifestJson | Should -Not -Match "key1"
        $script:manifestJson | Should -Not -Match "secret1"
    }

    It "restores a prior route manifest value verbatim after the action throws" {
        $env:INSTANCE_E2E_ROUTE_MANIFEST = "prior-manifest"

        { Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action { throw "boom" } } |
            Should -Throw

        $env:INSTANCE_E2E_ROUTE_MANIFEST | Should -Be "prior-manifest"
    }

    It "restores an absent variable to absent even when the action throws" {
        Remove-Item Env:INSTANCE_E2E_DATABASE_ENGINE -ErrorAction SilentlyContinue

        { Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:INSTANCE_E2E_DATABASE_ENGINE) | Should -BeFalse
    }

    It "restores empty, whitespace, and valued prior states verbatim after the action throws" {
        $env:INSTANCE_E2E_DATABASE_1_NAME = ""
        $env:INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_KEY = "   "
        $env:INSTANCE_E2E_FIXTURE_DATASTORE_IDS = "prior-stores"
        $env:NODE_OPTIONS = "--max-old-space-size=4096"

        { Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $script:settings -Fixture $script:fixture -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:INSTANCE_E2E_DATABASE_1_NAME) | Should -BeTrue
        $env:INSTANCE_E2E_DATABASE_1_NAME | Should -Be ""
        $env:INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_KEY | Should -Be "   "
        $env:INSTANCE_E2E_FIXTURE_DATASTORE_IDS | Should -Be "prior-stores"
        $env:NODE_OPTIONS | Should -Be "--max-old-space-size=4096"
    }
}

Describe "Instance E2E orchestration and setup ordering (DMS-1284)" {
    BeforeAll {
        function Get-BuildScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        $script:buildSource = Get-Content -LiteralPath $script:buildScript -Raw
        $script:setupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"))
        $script:setupSource = Get-Content -LiteralPath $script:setupScript -Raw
        $script:instanceE2ETests = Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "InstanceE2ETests"
        $script:instanceContext = Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Get-InstanceE2ETestEnvironmentContext"
    }

    It "registers the fixture, then restarts DMS exactly once after registration, then runs the tests" {
        $registerIndex = $script:instanceE2ETests.IndexOf("Register-InstanceE2EFixture -InstanceE2ESettings")
        $restartIndex = $script:instanceE2ETests.IndexOf("Restart-DmsContainer -Reason")
        $runIndex = $script:instanceE2ETests.IndexOf("RunInstanceE2E -TestFilter")

        $registerIndex | Should -BeGreaterThan -1
        $restartIndex | Should -BeGreaterThan $registerIndex
        $runIndex | Should -BeGreaterThan $restartIndex
        ([regex]::Matches($script:instanceE2ETests, "Restart-DmsContainer -Reason")).Count | Should -Be 1
        $script:instanceE2ETests | Should -Match "Invoke-WithInstanceE2ETestProcessContext"
    }

    It "forwards the engine, base, and resolved environment to the instance setup script" {
        $script:instanceE2ETests | Should -Match "DatabaseEngine\s*=\s*\`$instanceSettings.DatabaseEngine"
        $script:instanceE2ETests | Should -Match "EnvironmentFile\s*=\s*\`$instanceSettings.EnvironmentFile"
        $script:instanceE2ETests | Should -Match "ResolvedEnvironmentFile\s*=\s*\`$instanceSettings.ResolvedEnvironmentFile"
    }

    It "tears down the prior stack for the same engine and resolved environment before setup (C1)" {
        $teardownIndex = $script:instanceE2ETests.IndexOf("Invoke-E2EEngineAwareTeardown")
        $setupInvokeIndex = $script:instanceE2ETests.IndexOf("& `$instanceSetupScript @setupParameters")

        $teardownIndex | Should -BeGreaterThan -1
        $setupInvokeIndex | Should -BeGreaterThan $teardownIndex
        $script:instanceE2ETests | Should -Match "Invoke-E2EEngineAwareTeardown[\s\S]*?-DatabaseEngine \`$instanceSettings.DatabaseEngine"
        # Pre-clean composes the SAME final resolved environment that setup/registration consume.
        $script:instanceE2ETests | Should -Match "Invoke-E2EEngineAwareTeardown[\s\S]*?-EnvironmentFile \`$instanceSettings.ResolvedEnvironmentFile"
        $script:instanceE2ETests | Should -Match "Invoke-E2EEngineAwareTeardown[\s\S]*?-SkipLocalImageRemoval:\`$SkipDockerBuild"
    }

    It "resolves the environment exactly once (data standard then engine) on the build path (C4)" {
        ([regex]::Matches($script:instanceContext, "Resolve-DataStandardEnvironmentFile")).Count | Should -Be 1
        ([regex]::Matches($script:instanceContext, "Resolve-DatabaseEngineEnvironmentFile")).Count | Should -Be 1
    }

    It "uses the supplied ResolvedEnvironmentFile verbatim and never recomposes on the build path (C4)" {
        $script:setupSource | Should -Match "if \(\[string\]::IsNullOrWhiteSpace\(\`$ResolvedEnvironmentFile\)\)"
        $script:setupSource | Should -Match "\`$resolvedEnvironmentFile = \`$ResolvedEnvironmentFile"

        # The only overlay composition in setup lives inside the not-supplied branch, so a supplied
        # resolved file is never recomposed. Guard index precedes both Resolve calls.
        $guardIndex = $script:setupSource.IndexOf("IsNullOrWhiteSpace(`$ResolvedEnvironmentFile)")
        $dataStandardIndex = $script:setupSource.IndexOf("Resolve-DataStandardEnvironmentFile")
        $engineResolveIndex = $script:setupSource.IndexOf("Resolve-DatabaseEngineEnvironmentFile")
        $guardIndex | Should -BeGreaterThan -1
        $dataStandardIndex | Should -BeGreaterThan $guardIndex
        $engineResolveIndex | Should -BeGreaterThan $guardIndex
    }

    It "validates all three route database names before InfraOnly and provisioning on the standalone path (C5)" {
        $script:setupSource | Should -Match "Import-Module ./database-safety.psm1"
        # The call site (not the function definition) is anchored on its -DatabaseNames argument.
        $validateCallIndex = $script:setupSource.IndexOf("-DatabaseNames `$databases")
        $infraOnlyIndex = $script:setupSource.IndexOf("-InfraOnly -EnableConfig")
        $provisionIndex = $script:setupSource.IndexOf("& `$provisionE2EDatabaseScript")

        $validateCallIndex | Should -BeGreaterThan -1
        $infraOnlyIndex | Should -BeGreaterThan $validateCallIndex
        $provisionIndex | Should -BeGreaterThan $validateCallIndex
    }

    It "forwards the database engine through the InstanceE2ETest dispatch" {
        $script:buildSource | Should -Match "DatabaseEngine\s*=\s*\`$DatabaseEngine"
        $script:buildSource | Should -Match "if \(\`$environmentFileSupplied\)"
    }

    It "runs the setup in InfraOnly, then provisions three databases, then DmsOnly order" {
        # Match the actual invocation lines (not the doc-comment mentions of the phase switches).
        $infraOnlyIndex = $script:setupSource.IndexOf("-InfraOnly -EnableConfig")
        $provisionIndex = $script:setupSource.IndexOf("& `$provisionE2EDatabaseScript")
        $dmsOnlyIndex = $script:setupSource.IndexOf("-DmsOnly -EnableConfig")

        $infraOnlyIndex | Should -BeGreaterThan -1
        $provisionIndex | Should -BeGreaterThan $infraOnlyIndex
        $dmsOnlyIndex | Should -BeGreaterThan $provisionIndex
    }

    It "dispatches schema verification by engine so only the selected provider command runs" {
        $script:setupSource | Should -Match "if \(\`$DatabaseEngine -eq ""mssql""\)\s*\{\s*Assert-MssqlRouteContextSchema"
        $script:setupSource | Should -Match "docker exec dms-postgresql psql"
        $script:setupSource | Should -Match "docker exec dms-mssql"
    }

    It "verifies PostgreSQL as the resolved POSTGRES_USER rather than a hardcoded superuser (C2)" {
        $script:setupSource | Should -Match "psql -U \`$PostgresUser"
        $script:setupSource | Should -Not -Match "psql -U postgres"
        # Compose interpolates POSTGRES_USER into the container with ambient precedence, and the
        # same run's provisioning/registration reads resolve ambient-first, so the verification
        # role must come from the shared Compose-equivalent resolver, not a file-only read.
        $script:setupSource | Should -Match "Get-ComposeResolvedEnvValue[^\n]*-Name ""POSTGRES_USER"""
    }

    It "verifies SQL Server with an in-container SQLCMDPASSWORD and never exposes the SA password on the host (C3)" {
        # Password is exported inside dms-mssql from the container-resident MSSQL_SA_PASSWORD; -b makes
        # a SQL error non-zero so a failed query is never read as a schema result.
        $script:setupSource | Should -Match 'SQLCMDPASSWORD="\$MSSQL_SA_PASSWORD"'
        $script:setupSource | Should -Match "-W -b -Q"

        # The SA password never appears on the host process argument list (no sqlcmd -P <secret>) and is
        # never injected through the exec environment (no docker exec -e SQLCMDPASSWORD=<secret>).
        $script:setupSource | Should -Not -Match "sqlcmd[^\n]*-P "
        $script:setupSource | Should -Not -Match "docker exec -e SQLCMDPASSWORD"
        $script:setupSource | Should -Not -Match "-SaPassword"
    }
}

Describe "Instance E2E schema verification is engine-isolated and secret-safe when invoked (DMS-1284)" {
    BeforeAll {
        function Get-SetupScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:setupScriptPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"))
        foreach ($functionName in @("Assert-PostgresRouteContextSchema", "Assert-MssqlRouteContextSchema", "Assert-RouteContextSchemaProvisioned")) {
            . ([scriptblock]::Create((Get-SetupScriptFunctionText -ScriptPath $script:setupScriptPath -FunctionName $functionName)))
        }

        # Shim the docker CLI so the verification functions can be invoked without a real container. Each
        # call is recorded verbatim; "1" satisfies both the singleton COUNT check and every relation
        # existence probe, so a healthy verification path runs end to end.
        function docker {
            $script:dockerInvocations.Add(($args -join " "))
            "1"
        }
    }

    BeforeEach {
        $script:dockerInvocations = [System.Collections.Generic.List[string]]::new()
        $global:LASTEXITCODE = 0
    }

    It "runs only psql as the resolved nondefault user for PostgreSQL and never touches SQL Server (C2)" {
        Assert-RouteContextSchemaProvisioned -Database "edfi_route_db" -DatabaseEngine "postgresql" -PostgresUser "custom_pg_user"

        $invoked = $script:dockerInvocations -join "`n"
        $invoked | Should -Match "exec dms-postgresql psql -U custom_pg_user"
        $invoked | Should -Not -Match "-U postgres\b"
        $invoked | Should -Not -Match "dms-mssql"
        $invoked | Should -Not -Match "sqlcmd"
    }

    It "runs only sqlcmd inside dms-mssql and never puts the SA password on the host or runs psql (C3)" {
        Assert-RouteContextSchemaProvisioned -Database "edfi_route_db" -DatabaseEngine "mssql"

        $invoked = $script:dockerInvocations -join "`n"
        $invoked | Should -Match "exec dms-mssql bash -c"
        $invoked | Should -Match 'SQLCMDPASSWORD="\$MSSQL_SA_PASSWORD"'
        $invoked | Should -Match "-W -b -Q"
        $invoked | Should -Not -Match "-P "
        $invoked | Should -Not -Match "-e SQLCMDPASSWORD"
        $invoked | Should -Not -Match "dms-postgresql"
        $invoked | Should -Not -Match "psql"
    }
}

Describe "Instance standalone setup validates every route database name before any mutation (DMS-1284)" {
    BeforeAll {
        function Get-SetupScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        # Import the real safety module so the extracted setup guard runs against the actual safe-name /
        # dedicated-database rules, and dot-source the setup guard that the standalone path invokes.
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../database-safety.psm1"))) -Force
        $script:setupScriptPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"))
        . ([scriptblock]::Create((Get-SetupScriptFunctionText -ScriptPath $script:setupScriptPath -FunctionName "Assert-RouteContextDatabaseNamesAreDedicated")))
    }

    AfterAll {
        Remove-Module database-safety -Force -ErrorAction SilentlyContinue
    }

    It "accepts three safe, dedicated names" {
        {
            Assert-RouteContextDatabaseNamesAreDedicated `
                -DatabaseNames @("edfi_route_one", "edfi_route_two", "edfi_route_three") `
                -EnvironmentValues @{} `
                -EnvironmentFilePath "/resolved/route.env"
        } | Should -Not -Throw
    }

    It "throws on a later unsafe/reserved/protected name so an earlier database is never provisioned first" {
        # The second name is a reserved PostgreSQL system database; validating all three up front means
        # the guard rejects it before database 1 could ever be provisioned by the surrounding script.
        {
            Assert-RouteContextDatabaseNamesAreDedicated `
                -DatabaseNames @("edfi_route_one", "postgres", "edfi_route_three") `
                -EnvironmentValues @{} `
                -EnvironmentFilePath "/resolved/route.env"
        } | Should -Throw -ExpectedMessage "*reserved PostgreSQL*"
    }

    It "throws when a later name collides with the protected primary database name" {
        {
            Assert-RouteContextDatabaseNamesAreDedicated `
                -DatabaseNames @("edfi_route_one", "edfi_route_two", "edfi_primary") `
                -EnvironmentValues @{ POSTGRES_DB_NAME = "edfi_primary" } `
                -EnvironmentFilePath "/resolved/route.env"
        } | Should -Throw -ExpectedMessage "*must be dedicated*"
    }
}

Describe "Instance E2E route-context schema packages match the local image surface (DMS-1284)" {
    BeforeAll {
        # Parse the actual SCHEMA_PACKAGES JSON value from the route-context env file (the env-file line
        # reader does not span the multi-line quoted value), then assert on the parsed package list rather
        # than matching the source text. The route-context databases must be provisioned with exactly the
        # DS 5.2 core + TPDM surface the local DMS image bakes, or every routed data request returns 503.
        $envFilePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.env.routeContext.e2e"))
        $envFileContent = Get-Content -LiteralPath $envFilePath -Raw
        $match = [regex]::Match($envFileContent, "SCHEMA_PACKAGES='([\s\S]*?)'")
        if (-not $match.Success) { throw "SCHEMA_PACKAGES was not found in '$envFilePath'." }
        $script:schemaPackages = @($match.Groups[1].Value | ConvertFrom-Json)
        $script:schemaPackageNames = @($script:schemaPackages | ForEach-Object { $_.name })
    }

    It "declares exactly the core Ed-Fi and TPDM packages in order" {
        $script:schemaPackageNames | Should -HaveCount 2
        $script:schemaPackageNames[0] | Should -Be "EdFi.DataStandard52.ApiSchema"
        $script:schemaPackageNames[1] | Should -Be "EdFi.DataStandard52.TPDM.ApiSchema"
    }

    It "does not provision the Sample or Homograph extensions the local image does not carry" {
        $script:schemaPackageNames | Should -Not -Contain "EdFi.DataStandard52.Sample.ApiSchema"
        $script:schemaPackageNames | Should -Not -Contain "EdFi.DataStandard52.Homograph.ApiSchema"
        @($script:schemaPackages | ForEach-Object { $_.extensionName }) | Should -Not -Contain "Sample"
        @($script:schemaPackages | ForEach-Object { $_.extensionName }) | Should -Not -Contain "Homograph"
    }
}

Describe "Resolve-InstanceE2EBaseEnvironmentFile default environment-file resolution (DMS-1284)" {
    BeforeAll {
        function Get-BuildScriptFunctionText {
            param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
            return $functionAst.Extent.Text
        }

        $script:buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        # Import env-utility so Resolve-LocalSettingsEnvironmentFile exists and can be mocked for the
        # explicit-path delegation tests.
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../env-utility.psm1"))) -Force
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Resolve-InstanceE2EBaseEnvironmentFile")))
    }

    AfterAll {
        Remove-Module env-utility -Force -ErrorAction SilentlyContinue
    }

    Context "with no explicit environment file (the InstanceE2ETest default)" {
        It "resolves the tracked route-context env file at the docker-compose root regardless of CWD" {
            $root = Join-Path $TestDrive "dc-root"
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            $expected = Join-Path $root ".env.routeContext.e2e"
            Set-Content -LiteralPath $expected -Value "ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear"

            Resolve-InstanceE2EBaseEnvironmentFile -EnvironmentFile "" -DockerComposeRoot $root |
                Should -Be $expected
        }

        It "treats whitespace the same as omitted" {
            $root = Join-Path $TestDrive "dc-root-ws"
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            $expected = Join-Path $root ".env.routeContext.e2e"
            Set-Content -LiteralPath $expected -Value "x=1"

            Resolve-InstanceE2EBaseEnvironmentFile -EnvironmentFile "   " -DockerComposeRoot $root |
                Should -Be $expected
        }

        It "fails fast when the default route-context env file is missing" {
            $root = Join-Path $TestDrive "dc-root-missing"
            New-Item -ItemType Directory -Path $root -Force | Out-Null

            { Resolve-InstanceE2EBaseEnvironmentFile -EnvironmentFile "" -DockerComposeRoot $root } |
                Should -Throw -ExpectedMessage "*default environment file not found*"
        }
    }

    Context "with an explicit environment file" {
        BeforeEach {
            Mock Resolve-LocalSettingsEnvironmentFile { "RESOLVED::$Path" }
        }

        It "delegates an explicit relative path to the caller-relative resolver" {
            $result = Resolve-InstanceE2EBaseEnvironmentFile -EnvironmentFile "./custom.e2e" -DockerComposeRoot "C:\dc"

            $result | Should -Be "RESOLVED::./custom.e2e"
            Should -Invoke Resolve-LocalSettingsEnvironmentFile -Times 1 -Exactly -ParameterFilter { $Path -eq "./custom.e2e" }
        }

        It "delegates an explicit absolute path to the resolver (kept verbatim by the resolver)" {
            $absolute = "C:\abs\file.env"
            Resolve-InstanceE2EBaseEnvironmentFile -EnvironmentFile $absolute -DockerComposeRoot "C:\dc" |
                Should -Be "RESOLVED::$absolute"
        }
    }
}

Describe "InstanceE2ETest environment-file wiring (DMS-1284)" {
    BeforeAll {
        $script:buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        $script:ast = [System.Management.Automation.Language.Parser]::ParseFile($script:buildScript, [ref]$null, [ref]$null)
        $script:buildScriptText = Get-Content -LiteralPath $script:buildScript -Raw
    }

    It "declares an empty InstanceE2ETests -EnvironmentFile default (no CWD-relative default)" {
        $instanceE2ETests = $script:ast.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "InstanceE2ETests" },
            $true) | Select-Object -First 1
        $instanceE2ETests | Should -Not -BeNullOrEmpty

        $parameter = $instanceE2ETests.Body.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq "EnvironmentFile" }
        $parameter | Should -Not -BeNullOrEmpty
        $parameter.DefaultValue.Extent.Text | Should -Be '""'
    }

    It "forwards -EnvironmentFile to InstanceE2ETests only when explicitly supplied (never the ./.env.e2e default)" {
        $dispatch = [regex]::Match($script:buildScriptText, "(?s)InstanceE2ETest\s*\{.*?Invoke-Step\s*\{\s*InstanceE2ETests")
        $dispatch.Success | Should -BeTrue
        $dispatchText = $dispatch.Value

        $dispatchText | Should -Match 'if\s*\(\s*\$environmentFileSupplied\s*\)'
        $dispatchText | Should -Match '\$instanceE2EArguments\.EnvironmentFile\s*=\s*\$EnvironmentFile'
        # The forward must use the $EnvironmentFile variable (guarded), never a hard-coded env-file
        # literal such as the standard suite's ./.env.e2e default.
        $dispatchText | Should -Not -Match 'EnvironmentFile\s*=\s*"[^"]*\.e2e"'
    }
}
