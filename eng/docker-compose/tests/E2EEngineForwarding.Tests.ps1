# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1284: the standard DMS E2E orchestration resolves the environment once (data-standard overlay
# first, then the database-engine overlay) and builds two opaque connection strings from the resolved
# values. These tests invoke the module-level primitives that orchestration composes (rather than
# asserting on build-dms.ps1 text) to prove default/explicit engine behavior, that the engine overlay
# is applied on top of a prior (data-standard) composition, and that the resolved environment's values
# flow into the connection strings the test process consumes.

param()

Describe "E2E engine resolution and connection-string forwarding (DMS-1284)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))) -Force
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
    }

    Context "engine defaulting" {
        It "produces the PostgreSQL connection strings when no engine is specified" {
            $result = New-E2EDataStoreConnectionStrings -EnvironmentValues @{} -DatabaseName "edfi_e2e"

            $result.AdminConnectionString | Should -Match "^host=localhost;"
            $result.RegistrationConnectionString | Should -Match "^host=dms-postgresql;"
        }
    }

    Context "resolve-then-build flow with the engine overlay applied on top of a prior composition" {
        BeforeEach {
            $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-e2e-engine-$([Guid]::NewGuid().ToString('N'))"
            $script:composeRoot = Join-Path $script:work "compose"
            New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null

            # A base that already carries a data-standard marker (as if Resolve-DataStandardEnvironmentFile
            # had run first) plus the E2E database name. The engine overlay must be applied on top and must
            # preserve the prior values.
            $script:basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $script:basePath -NoNewline -Value @"
DMS_DATASTORE=postgresql
DMS_CONFIG_DATASTORE=postgresql
DATA_STANDARD_MARKER=ds61
E2E_DATABASE_NAME=edfi_datamanagementservice_e2e
E2E_SNAPSHOT_DATABASE_NAME=edfi_datamanagementservice_e2e_snapshot
POSTGRES_DB_NAME=edfi_datamanagementservice
"@

            # Minimal stand-in for the real .env.mssql overlay (the real key set is covered by
            # DatabaseEngineEnvironmentFile.Tests.ps1); it flips the datastore keys to mssql.
            Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -NoNewline -Value @"
MSSQL_SA_PASSWORD=Abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
MSSQL_PORT=1435
DMS_DATASTORE=mssql
DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@
        }

        AfterEach {
            if (Test-Path -LiteralPath $script:work) {
                Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "flips the datastore to mssql while preserving the prior data-standard values" {
            $resolved = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation

            $values = ReadValuesFromEnvFile $resolved
            $values["DMS_DATASTORE"] | Should -Be "mssql"
            $values["DATA_STANDARD_MARKER"] | Should -Be "ds61"
            $values["E2E_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice_e2e"
        }

        It "builds mssql connection strings from the resolved environment and the E2E database name" {
            $resolved = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation
            $values = ReadValuesFromEnvFile $resolved

            $connectionStrings = New-E2EDataStoreConnectionStrings `
                -DatabaseEngine mssql `
                -EnvironmentValues $values `
                -DatabaseName ([string]$values["E2E_DATABASE_NAME"])

            $connectionStrings.AdminConnectionString |
                Should -Be "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true"
            $connectionStrings.RegistrationConnectionString |
                Should -Be "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true"
        }

        It "keeps the datastore as mssql when the engine overlay is composed again (idempotent)" {
            $resolvedOnce = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation
            $resolvedTwice = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $resolvedOnce `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation

            (ReadValuesFromEnvFile $resolvedTwice)["DMS_DATASTORE"] | Should -Be "mssql"
        }

        It "leaves the base file unchanged for the postgresql engine (no-op overlay)" {
            $resolved = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine postgresql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot

            $resolved | Should -Be $script:basePath
            (ReadValuesFromEnvFile $resolved)["DMS_DATASTORE"] | Should -Be "postgresql"
        }
    }

    Context "startup phase plan selection (local and published image modes)" {
        It "selects the phase sequence for <Engine> published=<Published>" -ForEach @(
            @{ Engine = "mssql"; Published = $false; Defer = $true; Script = "start-local-dms.ps1"; Container = "ed-fi-api" }
            @{ Engine = "mssql"; Published = $true; Defer = $true; Script = "start-published-dms.ps1"; Container = "dms-published-dms-1" }
            @{ Engine = "postgresql"; Published = $false; Defer = $false; Script = "start-local-dms.ps1"; Container = "ed-fi-api" }
            @{ Engine = "postgresql"; Published = $true; Defer = $false; Script = "start-published-dms.ps1"; Container = "dms-published-dms-1" }
        ) {
            $plan = Get-E2EStartupPhasePlan -DatabaseEngine $Engine -UsePublishedImage:$Published

            # DeferDmsStart drives the whole phase sequence: $true => InfraOnly -> configure -> provision
            # -> DmsOnly (MSSQL, in either image mode); $false => full start -> provision -> restart
            # (PostgreSQL, in either image mode). The script and container follow the image mode.
            $plan.DeferDmsStart | Should -Be $Defer
            $plan.StartupScript | Should -Be $Script
            $plan.DmsContainerName | Should -Be $Container
        }

        It "defaults to the PostgreSQL legacy full-start/restart sequence when no engine is specified" {
            $plan = Get-E2EStartupPhasePlan

            $plan.DatabaseEngine | Should -Be "postgresql"
            $plan.DeferDmsStart | Should -BeFalse
            $plan.StartupScript | Should -Be "start-local-dms.ps1"
        }
    }
}

Describe "Get-E2ETestEnvironmentContext resolves the E2E database name with Compose precedence (DMS-1284)" {
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
        # extracted function's own Import-Module calls are mocked to no-ops in BeforeEach.
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))) -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../env-utility.psm1"))) -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../database-safety.psm1"))) -Force
        # Get-E2ETestEnvironmentContext derives the TRX suffix through Get-E2ETestResultSuffix
        # (which normalizes the filter through ConvertTo-NormalizedTestFilter), so those pure
        # helpers must be defined too.
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "ConvertTo-NormalizedTestFilter")))
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Get-E2ETestResultSuffix")))
        . ([scriptblock]::Create((Get-BuildScriptFunctionText -ScriptPath $script:buildScript -FunctionName "Get-E2ETestEnvironmentContext")))

        # The extracted function resolves its env-file path through this build-dms.ps1 helper; the
        # path itself is irrelevant here because ReadValuesFromEnvFile is mocked.
        function Resolve-E2EEnvironmentFilePath {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Path', Justification = 'Production-compatible signature for a Pester stub; the resolved path is fixed because ReadValuesFromEnvFile is mocked.')]
            param([string]$Path)
            "/resolved/.env.e2e"
        }
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
        Remove-Module database-safety -Force -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $script:contextEnvValues = @{
            "E2E_DATABASE_NAME"          = "edfi_datamanagementservice_e2e"
            "E2E_SNAPSHOT_DATABASE_NAME" = "edfi_datamanagementservice_e2e_snapshot"
        }

        Mock Import-Module { }
        Mock New-DataStandardDerivedEnvFile { $TargetPath }
        Mock Resolve-DataStandardEnvironmentFile { $BaseEnvironmentFile }
        Mock Resolve-DatabaseEngineEnvironmentFile { $BaseEnvironmentFile }
        Mock ReadValuesFromEnvFile { $script:contextEnvValues }
        Mock New-E2EDataStoreConnectionStrings {
            [pscustomobject]@{
                AdminConnectionString        = "admin:$DatabaseName"
                RegistrationConnectionString = "reg:$DatabaseName"
            }
        }
    }

    It "uses the env-file E2E_DATABASE_NAME when no ambient override exists" {
        Remove-Item Env:E2E_DATABASE_NAME -ErrorAction SilentlyContinue

        $context = Get-E2ETestEnvironmentContext -EnvironmentFile "./.env.e2e" -DatabaseEngine "postgresql"

        $context.DataStoreDatabaseName | Should -Be "edfi_datamanagementservice_e2e"
        Should -Invoke New-DataStandardDerivedEnvFile -Times 0 -Exactly
    }

    It "composes an explicit feature overlay before the data-standard and database-engine overlays" {
        $script:DataStandardVersion = "6.1"
        try {
            Mock Resolve-E2EEnvironmentFilePath {
                if ($Path -eq "./.env.document-cache.e2e") {
                    return "/resolved/.env.document-cache.e2e"
                }

                return "/resolved/.env.e2e"
            }
            Mock New-DataStandardDerivedEnvFile { "/resolved/.env.e2e.document-cache.e2e" }
            Mock Resolve-DataStandardEnvironmentFile { "$BaseEnvironmentFile.ds61" }
            Mock Resolve-DatabaseEngineEnvironmentFile { "$BaseEnvironmentFile.mssql" }

            $context = Get-E2ETestEnvironmentContext `
                -EnvironmentFile "./.env.e2e" `
                -EnvironmentOverlayFile "./.env.document-cache.e2e" `
                -TestFilter "Category=@DocumentCacheHostedHappyPath" `
                -DatabaseEngine "mssql"

            $context.EnvironmentFile | Should -Be "/resolved/.env.e2e.document-cache.e2e.ds61.mssql"
            $context.TestResultSuffix | Should -Be "e2e-document-cache"
            Should -Invoke New-DataStandardDerivedEnvFile -Times 1 -Exactly -ParameterFilter {
                $BaseEnvironmentFile -eq "/resolved/.env.e2e" -and
                $OverlayEnvironmentFile -eq "/resolved/.env.document-cache.e2e" -and
                $TargetPath -like "*/.derived/.env.e2e.document-cache.e2e"
            }
            Should -Invoke Resolve-DataStandardEnvironmentFile -Times 1 -Exactly -ParameterFilter {
                $BaseEnvironmentFile -eq "/resolved/.env.e2e.document-cache.e2e"
            }
            Should -Invoke Resolve-DatabaseEngineEnvironmentFile -Times 1 -Exactly -ParameterFilter {
                $BaseEnvironmentFile -eq "/resolved/.env.e2e.document-cache.e2e.ds61"
            }
        }
        finally {
            $script:DataStandardVersion = $null
        }
    }

    It "lets an ambient E2E_DATABASE_NAME override select the reset/provision target (Compose precedence)" {
        # provision-e2e-database.ps1 resolves E2E_DATABASE_NAME with ambient-wins Compose precedence
        # before its destructive reset. The context that feeds the CMS data store and the test
        # process must resolve the same way, or an ambient override would reset one database while
        # CMS registration and the tests target another.
        $priorExists = Test-Path "Env:E2E_DATABASE_NAME"
        $priorValue = [System.Environment]::GetEnvironmentVariable("E2E_DATABASE_NAME")
        try {
            [System.Environment]::SetEnvironmentVariable("E2E_DATABASE_NAME", "ambient_e2e_db")

            $context = Get-E2ETestEnvironmentContext -EnvironmentFile "./.env.e2e" -DatabaseEngine "postgresql"

            $context.DataStoreDatabaseName | Should -Be "ambient_e2e_db"
            $context.DataStoreAdminConnectionString | Should -Be "admin:ambient_e2e_db"
            $context.DataStoreConnectionString | Should -Be "reg:ambient_e2e_db"
        }
        finally {
            if ($priorExists) { [System.Environment]::SetEnvironmentVariable("E2E_DATABASE_NAME", $priorValue) }
            else { Remove-Item Env:E2E_DATABASE_NAME -ErrorAction SilentlyContinue }
        }
    }

    It "throws when E2E_DATABASE_NAME is absent from both the env file and the process environment" {
        Remove-Item Env:E2E_DATABASE_NAME -ErrorAction SilentlyContinue
        $script:contextEnvValues.Remove("E2E_DATABASE_NAME")

        { Get-E2ETestEnvironmentContext -EnvironmentFile "./.env.e2e" -DatabaseEngine "postgresql" } |
            Should -Throw "*E2E_DATABASE_NAME*"
    }
}

Describe "DocumentCache hosted E2E environment isolation" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "keeps the shared E2E environment free of a configured DocumentCache target" {
        $baseValues = ReadValuesFromEnvFile (Join-Path $script:dockerComposeRoot ".env.e2e")
        $localDmsCompose = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "local-dms.yml") -Raw

        $baseValues.ContainsKey("DMS_DOCUMENTCACHE_TARGET_DATA_STORE_ID") | Should -BeFalse
        $baseValues.ContainsKey("DMS_DOCUMENTCACHE_READ_ACCELERATION_ENABLED") | Should -BeFalse
        $localDmsCompose | Should -Not -Match "DocumentCache__Targets__0"
    }

    It "adds the target and read acceleration only through the focused overlay" {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-document-cache-overlay-$([Guid]::NewGuid().ToString('N'))"
        try {
            $derivedPath = Join-Path $work ".env.e2e.document-cache.e2e"
            New-DataStandardDerivedEnvFile `
                -BaseEnvironmentFile (Join-Path $script:dockerComposeRoot ".env.e2e") `
                -OverlayEnvironmentFile (Join-Path $script:dockerComposeRoot ".env.document-cache.e2e") `
                -TargetPath $derivedPath | Out-Null

            $values = ReadValuesFromEnvFile $derivedPath
            $values["DMS_DOCUMENTCACHE_TARGET_DATA_STORE_ID"] | Should -Be "1"
            $values["DMS_DOCUMENTCACHE_READ_ACCELERATION_ENABLED"] | Should -Be "true"
            $values["DMS_DOCUMENTCACHE_COMPOSE_FILE"] | Should -Be "local-dms-document-cache.yml"

            $targetComposePath = Join-Path `
                $script:dockerComposeRoot `
                ([string]$values["DMS_DOCUMENTCACHE_COMPOSE_FILE"])
            $targetComposePath | Should -Exist
            Get-Content -LiteralPath $targetComposePath -Raw |
                Should -Match "DataManagement__DocumentCache__Targets__0__DataStoreId"

            $startScript = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-local-dms.ps1") -Raw
            $startScript | Should -Match 'Name "DMS_DOCUMENTCACHE_COMPOSE_FILE"'
            $startScript | Should -Match '\$files \+= @\("-f", \$documentCacheComposeFilePath\)'
        }
        finally {
            if (Test-Path -LiteralPath $work) {
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
