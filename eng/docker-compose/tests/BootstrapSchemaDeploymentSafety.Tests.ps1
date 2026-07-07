# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets', '', Justification = 'Pester tests intentionally shadow Invoke-WebRequest to stub HTTP calls.')]
param()

Describe "DMS-1151 bootstrap schema deployment safety" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"

        function script:New-TestDirectory {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1151-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function script:Copy-DockerComposeFile {
            param(
                [string]$FileName,
                [string]$Destination
            )

            Copy-Item -LiteralPath (Join-Path $script:sourceDockerComposeRoot $FileName) -Destination $Destination
        }

        function script:New-IsolatedBootstrapRepo {
            $repoRoot = New-TestDirectory
            $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
            $engRoot = Join-Path $repoRoot "eng"
            New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null
            New-Item -ItemType Directory -Path $engRoot -Force | Out-Null

            foreach ($fileName in @(
                "bootstrap-manifest.psm1",
                "bootstrap-schema-tool.psm1",
                "bootstrap-schema-workspace.psm1",
                "env-utility.psm1",
                "configure-local-data-store.ps1",
                "provision-dms-schema.ps1",
                "provision-e2e-database.ps1",
                "bootstrap-wrapper.psm1",
                "bootstrap-local-dms.ps1",
                # The wrapper always composes the local-bootstrap data-standard overlay
                # (default 5.2) onto the base env, so wrapper invocations need the overlays.
                ".env.bootstrap.ds52",
                ".env.bootstrap.ds61",
                # provision-dms-schema.ps1's -DatabaseEngine mssql composes this overlay.
                ".env.mssql"
            )) {
                Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
            }

            Copy-Item -LiteralPath (Join-Path $script:sourceRepoRoot "eng/Dms-Management.psm1") -Destination $engRoot

            $envFile = Join-Path $dockerComposeRoot ".env.example"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
MSSQL_PORT=15433
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            return [pscustomobject]@{
                RepoRoot = $repoRoot
                DockerComposeRoot = $dockerComposeRoot
                BootstrapRoot = Join-Path $dockerComposeRoot ".bootstrap"
                EnvFile = $envFile
                ConfigureScript = Join-Path $dockerComposeRoot "configure-local-data-store.ps1"
                ProvisionScript = Join-Path $dockerComposeRoot "provision-dms-schema.ps1"
                E2EProvisionScript = Join-Path $dockerComposeRoot "provision-e2e-database.ps1"
                WrapperScript = Join-Path $dockerComposeRoot "bootstrap-local-dms.ps1"
            }
        }

        function script:New-StagedSchemaWorkspace {
            param(
                [Parameter(Mandatory)]
                [string]$DockerComposeRoot,

                [switch]$MissingCoreFile,

                [switch]$PathTraversal,

                # Extension project names included alongside the Ed-Fi core. Default preserves the
                # historical core+Sample fixture; callers can pass @() for core-only or @("Sample",
                # "Homograph") to exercise multi-extension ordering.
                [string[]]$Extensions = @("Sample")
            )

            $bootstrapRoot = Join-Path $DockerComposeRoot ".bootstrap"
            $apiSchemaRoot = Join-Path $bootstrapRoot "ApiSchema"
            New-Item -ItemType Directory -Path (Join-Path $apiSchemaRoot "schemas/Ed-Fi") -Force | Out-Null
            foreach ($extensionName in $Extensions) {
                New-Item -ItemType Directory -Path (Join-Path $apiSchemaRoot "schemas/$extensionName") -Force | Out-Null
                "{}" | Set-Content -LiteralPath (Join-Path $apiSchemaRoot "schemas/$extensionName/ApiSchema.json") -Encoding utf8
            }

            if (-not $MissingCoreFile) {
                "{}" | Set-Content -LiteralPath (Join-Path $apiSchemaRoot "schemas/Ed-Fi/ApiSchema.json") -Encoding utf8
            }

            $coreSchemaPath = if ($PathTraversal) { "../escape.json" } else { "schemas/Ed-Fi/ApiSchema.json" }
            $projects = @(
                [ordered]@{
                    projectName = "Ed-Fi"
                    projectEndpointName = "ed-fi"
                    isExtensionProject = $false
                    schemaPath = $coreSchemaPath
                }
            )
            foreach ($extensionName in $Extensions) {
                $projects += [ordered]@{
                    projectName = $extensionName
                    projectEndpointName = $extensionName.ToLowerInvariant()
                    isExtensionProject = $true
                    schemaPath = "schemas/$extensionName/ApiSchema.json"
                }
            }
            $apiSchemaManifest = [ordered]@{
                version = 1
                projects = $projects
            }
            $apiSchemaManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $apiSchemaRoot "bootstrap-api-schema-manifest.json") -Encoding utf8

            Import-Module (Join-Path $DockerComposeRoot "bootstrap-manifest.psm1") -Force
            $workspaceFingerprint = Get-BootstrapWorkspaceFingerprint -Path $apiSchemaRoot

            $rootManifest = [ordered]@{
                version = 1
                schema = [ordered]@{
                    selectionMode = "ApiSchemaPath"
                    selectedExtensions = @("sample")
                    effectiveSchemaHash = "abc123"
                    workspaceFingerprint = $workspaceFingerprint
                    apiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                }
                claims = [ordered]@{
                    mode = "Embedded"
                    directory = "claims"
                    fingerprint = "claims"
                    expectedVerificationChecks = @()
                }
                seed = [ordered]@{
                    extensionNamespacePrefixes = @()
                }
            }
            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $bootstrapRoot "claims") -Force | Out-Null
            $rootManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $bootstrapRoot "bootstrap-manifest.json") -Encoding utf8
        }

        function script:New-CmsEncryptedConnectionString {
            param(
                [string]$PlainText,
                [string]$EncryptionKey = "TestEncryptionKey123456789012345678901234567890"
            )

            $keyText = $EncryptionKey.PadRight(32, "0").Substring(0, 32)
            $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($keyText)
            $plainTextBytes = [System.Text.Encoding]::UTF8.GetBytes($PlainText)
            $aes = [System.Security.Cryptography.Aes]::Create()
            try {
                $aes.Key = $keyBytes
                $aes.GenerateIV()
                $encryptor = $aes.CreateEncryptor()
                try {
                    $cipherText = $encryptor.TransformFinalBlock($plainTextBytes, 0, $plainTextBytes.Length)
                    $result = [byte[]]::new($aes.IV.Length + $cipherText.Length)
                    [Array]::Copy($aes.IV, 0, $result, 0, $aes.IV.Length)
                    [Array]::Copy($cipherText, 0, $result, $aes.IV.Length, $cipherText.Length)
                    return [Convert]::ToBase64String($result)
                }
                finally {
                    $encryptor.Dispose()
                }
            }
            finally {
                $aes.Dispose()
            }
        }

        function script:New-FakeSchemaTool {
            param(
                [string]$Directory,
                [string]$CapturePath,
                [int]$ExitCode = 0,
                [string]$StdoutText = "fake schema stdout",
                [string]$StderrText = ""
            )

            $toolPath = Join-Path $Directory "fake-dms-schema.ps1"
            @"
param([Parameter(ValueFromRemainingArguments = `$true)][string[]] `$Arguments)
Add-Content -LiteralPath '$CapturePath' -Value 'BEGIN'
foreach (`$argument in `$Arguments) {
    Add-Content -LiteralPath '$CapturePath' -Value `$argument
}
Write-Output '$StdoutText'
if ('$StderrText'.Length -gt 0) {
    [Console]::Error.WriteLine('$StderrText')
}
exit $ExitCode
"@ | Set-Content -LiteralPath $toolPath -Encoding utf8
            return $toolPath
        }

        function script:Get-DeclaredScriptParameters {
            param(
                [string]$Path
            )

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) {
                throw "Failed to parse $Path"
            }

            return @(
                $ast.ParamBlock.Parameters |
                    ForEach-Object { $_.Name.VariablePath.UserPath } |
                    Select-Object -Unique
            )
        }
    }

    BeforeEach {
        $script:repo = New-IsolatedBootstrapRepo
    }

    AfterEach {
        if ($null -ne $script:repo) {
            Get-Module Dms-Management, SmokeTest |
                Where-Object { $_.Path -like "$($script:repo.RepoRoot)*" } |
                Remove-Module -Force -ErrorAction SilentlyContinue
        }

        if ($null -ne $script:repo -and (Test-Path -LiteralPath $script:repo.RepoRoot)) {
            Remove-Item -LiteralPath $script:repo.RepoRoot -Recurse -Force
        }

        [System.Environment]::SetEnvironmentVariable("DMS_SCHEMA_TOOL_PATH", $null)
        [System.Environment]::SetEnvironmentVariable("DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK", $null)
    }

    Context "public script contracts" {
        It "provision-dms-schema.ps1 exposes only the selector, env, and engine overlay parameters" {
            $params = Get-DeclaredScriptParameters -Path $script:repo.ProvisionScript

            $params | Should -Contain "EnvironmentFile"
            $params | Should -Contain "DataStoreId"
            $params | Should -Contain "SchoolYear"
            $params | Should -Contain "DatabaseEngine"
            $params | Should -Not -Contain "SchemaToolPath"
            $params | Should -Not -Contain "SeedTemplate"
            $params | Should -Not -Contain "LoadSeedData"
            $params | Should -Not -Contain "ApiSchemaPath"
            $params.Count | Should -Be 4
        }

        It "provision-dms-schema.ps1 composes the MSSQL engine overlay after resolving the environment file and before reading env values" {
            $content = Get-Content -LiteralPath $script:repo.ProvisionScript -Raw

            $resolveIndex = $content.IndexOf('$resolvedEnvironmentFile = Resolve-ProvisionEnvironmentFile -Path $EnvironmentFile')
            $engineIndex = $content.IndexOf('$resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile')
            $readValuesIndex = $content.IndexOf('$envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile')

            $resolveIndex | Should -BeGreaterThan -1
            $engineIndex | Should -BeGreaterThan $resolveIndex
            $readValuesIndex | Should -BeGreaterThan $engineIndex

            $content | Should -Match 'Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine \$DatabaseEngine -BaseEnvironmentFile \$resolvedEnvironmentFile -DockerComposeRoot \$PSScriptRoot'
        }

        It "provision-e2e-database.ps1 exposes neutral reset and provision parameters" {
            $params = Get-DeclaredScriptParameters -Path $script:repo.E2EProvisionScript

            $params | Should -Contain "EnvironmentFile"
            $params | Should -Contain "DatabaseName"
            $params | Should -Contain "Configuration"
            $params | Should -Contain "PostgresContainerName"
            $params | Should -Not -Contain "SchemaToolPath"
            $params | Should -Not -Contain "DataStoreId"
            $params | Should -Not -Contain "SchoolYear"
            $params.Count | Should -Be 4
        }

        It "provision-e2e-database.ps1 owns explicit E2E database reset and SchemaTools provisioning" {
            $content = Get-Content -LiteralPath $script:repo.E2EProvisionScript -Raw
            $oldHelperNamePattern = "provision-relational" + "-e2e-database"
            $oldDatabaseNamePattern = "RELATIONAL" + "_E2E_DATABASE_NAME"

            $content | Should -Match "E2E_DATABASE_NAME"
            $content | Should -Match "Reset-E2EDatabase"
            $content | Should -Match '"ddl"'
            $content | Should -Match '"provision"'
            $content | Should -Match '"--create-database"'
            $content | Should -Match 'if \(\[string\]::IsNullOrWhiteSpace\(\$DatabaseName\)\)'
            $content | Should -Not -Match $oldHelperNamePattern
            $content | Should -Not -Match $oldDatabaseNamePattern
        }

        It "wrapper entry script exposes configure flags without exposing direct data-store selectors" {
            $params = Get-DeclaredScriptParameters -Path $script:repo.WrapperScript

            $params | Should -Contain "NoDataStore"
            $params | Should -Contain "AddSmokeTestCredentials"
            $params | Should -Contain "SchoolYearRange"
            $params | Should -Contain "LoadSeedData"
            $params | Should -Not -Contain "InstanceId"
            $params | Should -Not -Contain "DataStoreId"
        }

        It "start scripts expose InfraOnly and DmsOnly phase switches" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $params = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot $name)

                $params | Should -Contain "InfraOnly"
                $params | Should -Contain "DmsOnly"
                $params | Should -Contain "EnableKafka"
                $params | Should -Not -Contain "SkipConnectorSetup"
                $params | Should -Not -Contain "ApiSchemaPath"
                $params | Should -Not -Contain "ClaimsDirectoryPath"
                $params | Should -Not -Contain "Extensions"
            }
        }

        It "start scripts keep Kafka compose files behind explicit opt-in" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot $name) -Raw

                ([regex]::Matches($content, '"kafka\.yml"')).Count | Should -Be 1
                $content | Should -Match '\$enableKafkaInfrastructure\s*=\s*\$EnableKafka\s+-or\s+\$EnableKafkaUI'
                # The MSSQL relational path does not use Debezium CDC, so start-local-dms.ps1 additionally
                # gates the kafka.yml/kafka-ui.yml compose files on $DatabaseEngine -eq "postgresql"; that
                # extra clause is optional here so both start-local-dms.ps1 and start-published-dms.ps1 match.
                $content | Should -Match 'if \(\$enableKafkaInfrastructure( -and \$DatabaseEngine -eq "postgresql")?\) \{\s*\$files \+= @\("-f", "kafka\.yml"\)\s*\}'
                $content | Should -Match 'if \(\$EnableKafkaUI( -and \$DatabaseEngine -eq "postgresql")?\) \{\s*\$files \+= @\("-f", "kafka-ui\.yml"\)\s*\}'
                $content | Should -Match 'docker compose \$files --env-file \$EnvironmentFile -p dms-(local|published) up \$upArgs kafka kafka-postgresql-source'
                $content | Should -Match '"--remove-orphans"'
            }
        }

        It "start scripts do not reference removed installer or setup plumbing" {
            $installerPathPattern = "/app/" + "Installer"
            $installerProjectPattern = "Backend" + "\.Installer"
            $setupFlagPattern = "NEED" + "_DATABASE_SETUP"
            $deployFlagPattern = "DMS" + "_DEPLOY_DATABASE_ON_STARTUP"

            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1", "start-all-services.ps1")) {
                $content = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot $name) -Raw

                $content | Should -Not -Match $installerPathPattern
                $content | Should -Not -Match $installerProjectPattern
                $content | Should -Not -Match $setupFlagPattern
                $content | Should -Not -Match $deployFlagPattern
            }
        }

        It "start-published-dms.ps1 retains transitional flags pending consumer migration" {
            # start-published-dms.ps1 keeps -LoadSeedData, -NoDataStore, -SchoolYearRange, and
            # -AddSmokeTestCredentials until the published-image consumer path is migrated (separate task).
            $params = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1")

            $params | Should -Contain "LoadSeedData"
            $params | Should -Contain "NoDataStore"
            $params | Should -Contain "SchoolYearRange"
            $params | Should -Contain "AddSmokeTestCredentials"
        }

        It "start-local-dms.ps1 no longer declares de-scoped non-infrastructure flags" {
            # DMS-1153: -NoDataStore, -SchoolYearRange, -LoadSeedData, and -AddSmokeTestCredentials
            # have been removed from start-local-dms.ps1. Use configure-local-data-store.ps1 for
            # data-store/smoke-credential concerns and load-dms-seed-data.ps1 for seed delivery.
            $params = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1")

            $params | Should -Not -Contain "LoadSeedData"
            $params | Should -Not -Contain "NoDataStore"
            $params | Should -Not -Contain "SchoolYearRange"
            $params | Should -Not -Contain "AddSmokeTestCredentials"
        }
    }

    Context "staged schema workspace validation" {
        It "returns core first and extensions in manifest order" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-workspace.psm1") -Force

            $workspace = Resolve-BootstrapSchemaWorkspace

            $workspace.CoreSchemaPath | Should -Match "schemas.Ed-Fi.ApiSchema.json"
            $workspace.ExtensionSchemaPaths.Count | Should -Be 1
            $workspace.ExtensionSchemaPaths[0] | Should -Match "schemas.Sample.ApiSchema.json"
            $workspace.EffectiveSchemaHash | Should -Be "abc123"
            $manifest = Get-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") -Raw |
                ConvertFrom-Json -AsHashtable
            $workspace.WorkspaceFingerprint | Should -Be $manifest["schema"]["workspaceFingerprint"]
            $workspace.WorkspaceFingerprint | Should -Match '^[a-f0-9]{64}$'
        }

        It "rejects missing staged schema files and path traversal" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot -MissingCoreFile
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-workspace.psm1") -Force

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*Staged core schema file is missing*"

            Remove-Item -LiteralPath $script:repo.BootstrapRoot -Recurse -Force
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot -PathTraversal

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*parent path segments*"
        }

        It "rejects an absolute schemaPath in the ApiSchema manifest" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-workspace.psm1") -Force
            $apiSchemaManifestPath = Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json"

            $absoluteSchemaPath = if ($IsWindows) { "C:\evil-schema.json" } else { "/tmp/evil-schema.json" }
            $absolutePathManifest = [ordered]@{
                version = 1
                projects = @(
                    [ordered]@{
                        projectName = "Ed-Fi"
                        projectEndpointName = "ed-fi"
                        isExtensionProject = $false
                        schemaPath = $absoluteSchemaPath
                    }
                )
            }
            $absolutePathManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath $apiSchemaManifestPath -Encoding utf8

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*must be relative*"
        }

        It "rejects a non-boolean isExtensionProject value in the ApiSchema manifest" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-workspace.psm1") -Force
            $apiSchemaManifestPath = Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json"

            $nonBoolManifest = [ordered]@{
                version = 1
                projects = @(
                    [ordered]@{
                        projectName = "Ed-Fi"
                        projectEndpointName = "ed-fi"
                        isExtensionProject = "yes"
                        schemaPath = "schemas/Ed-Fi/ApiSchema.json"
                    }
                )
            }
            $nonBoolManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath $apiSchemaManifestPath -Encoding utf8

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*malformed boolean*"
        }

        It "rejects missing and malformed manifest handoffs before provisioning can run" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-workspace.psm1") -Force

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*Bootstrap manifest not found*"

            New-Item -ItemType Directory -Path $script:repo.BootstrapRoot -Force | Out-Null
            "not-json" | Set-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") -Encoding utf8
            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*contains malformed JSON*"

            @{ version = 1 } |
                ConvertTo-Json -Depth 10 |
                Set-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "bootstrap-manifest.json") -Encoding utf8
            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*malformed schema section*"

            Remove-Item -LiteralPath $script:repo.BootstrapRoot -Recurse -Force
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Remove-Item -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json")
            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*ApiSchema manifest is missing*"
        }

        It "rejects zero and multiple core projects in the ApiSchema manifest" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-schema-workspace.psm1") -Force
            $apiSchemaManifestPath = Join-Path $script:repo.BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json"

            $zeroCoreManifest = [ordered]@{
                version = 1
                projects = @(
                    [ordered]@{
                        projectName = "Sample"
                        projectEndpointName = "sample"
                        isExtensionProject = $true
                        schemaPath = "schemas/Sample/ApiSchema.json"
                    }
                )
            }
            $zeroCoreManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath $apiSchemaManifestPath -Encoding utf8

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*exactly one core project. Found 0*"

            $multipleCoreManifest = [ordered]@{
                version = 1
                projects = @(
                    [ordered]@{
                        projectName = "Ed-Fi"
                        projectEndpointName = "ed-fi"
                        isExtensionProject = $false
                        schemaPath = "schemas/Ed-Fi/ApiSchema.json"
                    },
                    [ordered]@{
                        projectName = "Core Duplicate"
                        projectEndpointName = "core-duplicate"
                        isExtensionProject = $false
                        schemaPath = "schemas/Sample/ApiSchema.json"
                    }
                )
            }
            $multipleCoreManifest | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath $apiSchemaManifestPath -Encoding utf8

            { Resolve-BootstrapSchemaWorkspace } | Should -Throw -ExpectedMessage "*exactly one core project. Found 2*"
        }
    }

    Context "schema provisioning" {
        It "rejects mutually exclusive selectors before reading CMS or invoking SchemaTools" {
            . $script:repo.ProvisionScript

            function Add-CmsClient { throw "CMS must not be contacted when selectors are invalid." }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1) -SchoolYear @(2024) } |
                Should -Throw -ExpectedMessage "*mutually exclusive*"
        }

        It "invokes dms-schema once per target database with host-side connection settings" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=tenant_db;'
                        dataStoreContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        name = "B"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=tenant_db;'
                        dataStoreContexts = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" }
                        )
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1, 2)

            $captured = @(Get-Content -LiteralPath $capturePath)
            @($captured | Where-Object { $_ -eq "BEGIN" }).Count | Should -Be 1
            $captured | Should -Contain "ddl"
            $captured | Should -Contain "provision"
            @($captured | Where-Object { $_ -eq "--schema" }).Count | Should -Be 2
            $captured | Should -Contain "--connection-string"
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=localhost"
            $connectionString | Should -Match "port=5544"
            $connectionString | Should -Match "database=tenant_db"
            $connectionString | Should -Not -Match "dms-postgresql"
            $captured | Should -Contain "--dialect"
            $captured | Should -Contain "pgsql"
            $captured | Should -Contain "--create-database"
        }

        It "decrypts CMS-encrypted connection strings before provisioning" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            $encryptedConnectionString = New-CmsEncryptedConnectionString `
                -PlainText 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=encrypted_db;'

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 3
                        name = "Encrypted"
                        connectionString = $encryptedConnectionString
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(3)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=localhost"
            $connectionString | Should -Match "port=5544"
            $connectionString | Should -Match "database=encrypted_db"
            $connectionString | Should -Not -Match "dms-postgresql"
        }

        It "rejects an encrypted connection string when the encryption key is not configured" {
            . $script:repo.ProvisionScript

            { ConvertFrom-CmsEncryptedConnectionString -ProtectedConnectionString "AAAAAAAAAAAAAAAAAAAAAA==" -EnvValues @{} } |
                Should -Throw -ExpectedMessage "*DMS_CONFIG_DATABASE_ENCRYPTION_KEY is not set*"
        }

        It "rejects an encrypted connection string payload that is not valid base64" {
            . $script:repo.ProvisionScript

            $envValues = @{ DMS_CONFIG_DATABASE_ENCRYPTION_KEY = "TestEncryptionKey123456789012345678901234567890" }
            { ConvertFrom-CmsEncryptedConnectionString -ProtectedConnectionString "@@@@" -EnvValues $envValues } |
                Should -Throw -ExpectedMessage "*not valid CMS encrypted base64*"
        }

        It "rejects an encrypted connection string payload too short to contain an IV" {
            . $script:repo.ProvisionScript

            $envValues = @{ DMS_CONFIG_DATABASE_ENCRYPTION_KEY = "TestEncryptionKey123456789012345678901234567890" }
            $shortPayload = [Convert]::ToBase64String([byte[]]::new(8))
            { ConvertFrom-CmsEncryptedConnectionString -ProtectedConnectionString $shortPayload -EnvValues $envValues } |
                Should -Throw -ExpectedMessage "*payload is invalid*"
        }

        It "rejects an encrypted connection string that cannot be decrypted with the configured key" {
            . $script:repo.ProvisionScript

            $envValues = @{ DMS_CONFIG_DATABASE_ENCRYPTION_KEY = "TestEncryptionKey123456789012345678901234567890" }
            # 16-byte IV plus a 17-byte ciphertext is not a whole AES block, so PKCS7 decryption
            # fails deterministically rather than relying on a wrong-key padding collision.
            $undecryptable = [Convert]::ToBase64String([byte[]]::new(33))
            { ConvertFrom-CmsEncryptedConnectionString -ProtectedConnectionString $undecryptable -EnvValues $envValues } |
                Should -Throw -ExpectedMessage "*could not be decrypted*"
        }

        It "fails fast when CMS instance results reach the query page size" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    1..500 | ForEach-Object {
                        [pscustomobject]@{
                            id = $_
                            name = "I$_"
                            connectionString = "host=dms-postgresql;port=5432;username=postgres;password=x;database=db$_;"
                            dataStoreContexts = @()
                        }
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1) } |
                Should -Throw -ExpectedMessage "*page size (500)*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse
        }

        It "provisions normally when instance results stay below the page size" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                $target = [pscustomobject]@{
                    id = 1
                    name = "Target"
                    connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=below_limit;'
                    dataStoreContexts = @()
                }
                $filler = 2..499 | ForEach-Object {
                    [pscustomobject]@{
                        id = $_
                        name = "I$_"
                        connectionString = "host=dms-postgresql;port=5432;username=postgres;password=x;database=db$_;"
                        dataStoreContexts = @()
                    }
                }
                return @($target) + @($filler)
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1) } |
                Should -Not -Throw
            @(Get-Content -LiteralPath $capturePath) | Should -Contain "provision"
        }

        It "resolves school-year selectors and fails when a year is ambiguous" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 10
                        name = "SY2024-A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=sy2024a;'
                        dataStoreContexts = @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" })
                    },
                    [pscustomobject]@{
                        id = 11
                        name = "SY2024-B"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=sy2024b;'
                        dataStoreContexts = @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" })
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -SchoolYear @(2024) } |
                Should -Throw -ExpectedMessage "*Multiple data stores found with route context schoolYear=2024*"
        }

        It "fails on zero data stores or ambiguous auto-selection before invoking SchemaTools" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore { return @() }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile } |
                Should -Throw -ExpectedMessage "*No data stores found*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse

            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=a;'
                        dataStoreContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        name = "B"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=b;'
                        dataStoreContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile } |
                Should -Throw -ExpectedMessage "*Multiple data stores exist*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse
        }

        It "surfaces SchemaTools stdout and stderr and fails on non-zero exit" {
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool `
                -Directory $script:repo.RepoRoot `
                -CapturePath $capturePath `
                -ExitCode 23 `
                -StdoutText "schema-tool-out" `
                -StderrText "schema-tool-err"

            . $script:repo.ProvisionScript

            $output = & {
                try {
                    Invoke-DmsSchemaProvision `
                        -ToolPath $fakeTool `
                        -SchemaPaths @("core.json") `
                        -ConnectionString "host=localhost;port=5544;username=postgres;password=secret-pass;database=tool_failure;" `
                        -DatabaseName "tool_failure"
                }
                catch {
                    $_.Exception.Message
                }
            } *>&1 | Out-String

            $output | Should -Match "schema-tool-out"
            $output | Should -Match "schema-tool-err"
            $output | Should -Match "exit code 23"
        }

        It "does not write bootstrap-generated secrets or raw connection strings to logs" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 5
                        name = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=log_guard;'
                        dataStoreContexts = @()
                    }
                )
            }

            $output = & {
                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(5)
            } *>&1 | Out-String

            $output | Should -Not -Match "secret-pass"
            $output | Should -Not -Match "ValidClientSecret1234567890"
            $output | Should -Not -Match "dms-postgresql"
            $output | Should -Not -Match "password="
        }

        It "rejects staged schema workspace drift before invoking SchemaTools" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Set-Content -LiteralPath (Join-Path $script:repo.BootstrapRoot "ApiSchema/schemas/Ed-Fi/ApiSchema.json") -Value '{"changed":true}' -Encoding utf8
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 5
                        name = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=drift_guard;'
                        dataStoreContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(5) } |
                Should -Throw -ExpectedMessage "*staged schema workspace fingerprint mismatch*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse
        }

        It "fails fast when a selected data store's dialect does not match the environment's DMS_DATASTORE" {
            # configure-local-data-store.ps1 -NoDataStore can silently reuse a route-unqualified
            # CMS data store from a previous run without checking its connection-string dialect.
            # A stale PostgreSQL data store combined with an env configured for DMS_DATASTORE=mssql
            # must fail fast here rather than provisioning against the wrong engine.
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-mssql-engine-mismatch.env"
            Get-Content -LiteralPath $script:repo.EnvFile |
                Set-Content -LiteralPath $envFile -Encoding utf8
            Add-Content -LiteralPath $envFile -Value "DMS_DATASTORE=mssql"

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 9
                        name = "StalePostgres"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=stale_pg;'
                        dataStoreContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(9) } |
                Should -Throw -ExpectedMessage "*CMS data store 9*name=StalePostgres*database=stale_pg*resolved to dialect 'pgsql'*DMS_DATASTORE is 'mssql'*expected dialect 'mssql'*-NoDataStore*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse
        }

        It "provisions normally when a selected data store's dialect matches the environment's DMS_DATASTORE" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-mssql-engine-match.env"
            Get-Content -LiteralPath $script:repo.EnvFile |
                Set-Content -LiteralPath $envFile -Encoding utf8
            Add-Content -LiteralPath $envFile -Value "DMS_DATASTORE=mssql"

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 10
                        name = "MatchedMssql"
                        connectionString = 'Server=dms-mssql,1433;Database=matched_mssql;User Id=sa;Password=${POSTGRES_PASSWORD};TrustServerCertificate=true;'
                        dataStoreContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(10) } |
                Should -Not -Throw
            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "mssql"
        }

        It "passes the dialect guard for -DatabaseEngine mssql against a base env without DMS_DATASTORE" {
            # $script:repo.EnvFile carries no DMS_DATASTORE at all (see New-IsolatedBootstrapRepo).
            # Direct invocation with -DatabaseEngine mssql must compose the .env.mssql overlay
            # (DMS_DATASTORE=mssql) onto it before Resolve-ExpectedProvisioningDialect reads the
            # effective environment, so an mssql-dialect data store is accepted rather than
            # rejected against the postgresql default.
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 11
                        name = "ComposedMssql"
                        connectionString = 'Server=dms-mssql,1433;Database=composed_mssql;User Id=sa;Password=${POSTGRES_PASSWORD};TrustServerCertificate=true;'
                        dataStoreContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(11) -DatabaseEngine mssql } |
                Should -Not -Throw
            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "mssql"
        }
    }

    Context "instance configuration" {
        It "returns a structured object for NoDataStore route-unqualified selection" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 77
                        name = "Existing"
                        dataStoreContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $script:repo.EnvFile -NoDataStore

            $result.DataStoreIds | Should -Be @(77)
            $result.HasRouteQualifiedDataStores | Should -BeFalse
            $result.RouteContexts.Count | Should -Be 0
        }

        It "rejects NoDataStore when the sole existing data store is route-qualified" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 77
                        name = "Existing"
                        dataStoreContexts = @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" })
                    }
                )
            }

            { Invoke-ConfigureLocalDataStore -EnvironmentFile $script:repo.EnvFile -NoDataStore } |
                Should -Throw -ExpectedMessage "*route-qualified*"
        }

        It "creates smoke credentials for the selected NoDataStore target and tenant" {
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-with-tenant.env"
            Get-Content -LiteralPath $script:repo.EnvFile |
                Set-Content -LiteralPath $envFile -Encoding utf8
            Add-Content -LiteralPath $envFile -Value "CONFIG_SERVICE_TENANT=tenant-a"

            $capturePath = Join-Path $script:repo.RepoRoot "smoke-capture.txt"
            $smokeModuleDir = Join-Path $script:repo.RepoRoot "eng/smoke_test/modules"
            New-Item -ItemType Directory -Path $smokeModuleDir -Force | Out-Null
            @"
function Get-SmokeTestCredential {
    param([string] `$ConfigServiceUrl, [long[]] `$DataStoreIds, [string] `$Tenant)
    Add-Content -LiteralPath '$capturePath' -Value `"smoke url=`$ConfigServiceUrl ids=`$(`$DataStoreIds -join ',') tenant=`$Tenant`"
}
Export-ModuleMember -Function Get-SmokeTestCredential
"@ | Set-Content -LiteralPath (Join-Path $smokeModuleDir "SmokeTest.psm1") -Encoding utf8

            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                param([string] $Tenant)
                $Tenant | Should -Be "tenant-a"
                return @(
                    [pscustomobject]@{
                        id = 77
                        name = "Existing"
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ConfigureLocalDataStore -EnvironmentFile $envFile -NoDataStore -AddSmokeTestCredentials | Out-Null

            @(Get-Content -LiteralPath $capturePath) | Should -Contain "smoke url=http://localhost:18081 ids=77 tenant=tenant-a"
        }

        It "creates smoke credentials for all selected school-year data stores" {
            $capturePath = Join-Path $script:repo.RepoRoot "smoke-schoolyear-capture.txt"
            $smokeModuleDir = Join-Path $script:repo.RepoRoot "eng/smoke_test/modules"
            New-Item -ItemType Directory -Path $smokeModuleDir -Force | Out-Null
            @"
function Get-SmokeTestCredential {
    param([string] `$ConfigServiceUrl, [long[]] `$DataStoreIds, [string] `$Tenant)
    Add-Content -LiteralPath '$capturePath' -Value `"smoke ids=`$(`$DataStoreIds -join ',') tenant=`$Tenant`"
}
Export-ModuleMember -Function Get-SmokeTestCredential
"@ | Set-Content -LiteralPath (Join-Path $smokeModuleDir "SmokeTest.psm1") -Encoding utf8

            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DmsSchoolYearInstances {
                return @(
                    @{ DataStoreId = [long]101; Year = 2024 },
                    @{ DataStoreId = [long]102; Year = 2025 }
                )
            }

            Invoke-ConfigureLocalDataStore -EnvironmentFile $script:repo.EnvFile -SchoolYearRange "2024-2025" -AddSmokeTestCredentials | Out-Null

            @(Get-Content -LiteralPath $capturePath) | Should -Contain "smoke ids=101,102 tenant="
        }

        It "uses an explicit database name when creating the default local data store" {
            . $script:repo.ConfigureScript

            $script:capturedPostgresDbName = $null
            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore {
                param(
                    [string] $CmsUrl,
                    [string] $AccessToken,
                    [System.Management.Automation.PSCredential] $PostgresCredential,
                    [string] $PostgresDbName,
                    [string] $Name,
                    [string] $DataStoreType,
                    [string] $Tenant
                )
                $script:capturedPostgresDbName = $PostgresDbName
                return 303
            }

            $result = Invoke-ConfigureLocalDataStore `
                -EnvironmentFile $script:repo.EnvFile `
                -DataStoreDatabaseName "edfi_datamanagementservice_e2e"

            $script:capturedPostgresDbName | Should -Be "edfi_datamanagementservice_e2e"
            $result.DataStoreIds | Should -Be @([long]303)
        }

        It "uses an explicit database name when creating school-year local data stores" {
            . $script:repo.ConfigureScript

            $script:capturedPostgresDbName = $null
            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DmsSchoolYearInstances {
                param(
                    [string] $CmsUrl,
                    [string] $AccessToken,
                    [int] $StartYear,
                    [int] $EndYear,
                    [System.Management.Automation.PSCredential] $PostgresCredential,
                    [string] $PostgresDbName,
                    [string] $Tenant
                )
                $script:capturedPostgresDbName = $PostgresDbName
                return @(
                    @{ DataStoreId = [long]401; Year = 2024 },
                    @{ DataStoreId = [long]402; Year = 2025 }
                )
            }

            $result = Invoke-ConfigureLocalDataStore `
                -EnvironmentFile $script:repo.EnvFile `
                -SchoolYearRange "2024-2025" `
                -DataStoreDatabaseName "edfi_datamanagementservice_e2e"

            $script:capturedPostgresDbName | Should -Be "edfi_datamanagementservice_e2e"
            $result.DataStoreIds | Should -Be @([long]401, [long]402)
        }
    }

    Context "wrapper sequencing" {
        It "orders infra, configure, provision, DMS-only, then seed with school-year handoff" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "sequence.txt"

            @"
param(
    [switch] `$InfraOnly,
    [switch] `$DmsOnly,
    [switch] `$EnableConfig,
    [string] `$EnvironmentFile,
    [string] `$IdentityProvider,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
if (`$InfraOnly) { Add-Content -LiteralPath '$sequencePath' -Value `"start-infra EnableConfig=`$EnableConfig`" }
elseif (`$DmsOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-dms' }
else { Add-Content -LiteralPath '$sequencePath' -Value 'start-legacy' }
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([string] `$EnvironmentFile, [string] `$SchoolYearRange, [switch] `$NoDataStore, [switch] `$AddSmokeTestCredentials)
Add-Content -LiteralPath '$sequencePath' -Value `"configure range=`$SchoolYearRange noDataStore=`$NoDataStore smoke=`$AddSmokeTestCredentials`"
[pscustomobject]@{
    DataStoreIds = [long[]] @(101, 102)
    SelectedDataStoreIds = [long[]] @(101, 102)
    RouteContexts = @(
        [pscustomobject]@{ DataStoreId = [long]101; ContextKey = 'schoolYear'; ContextValue = '2024' },
        [pscustomobject]@{ DataStoreId = [long]102; ContextKey = 'schoolYear'; ContextValue = '2025' }
    )
    Tenant = ''
    SchoolYears = [int[]] @(2024, 2025)
    HasRouteQualifiedDataStores = `$true
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-data-store.ps1") -Encoding utf8

            @"
param([string] `$EnvironmentFile, [long[]] `$DataStoreId)
Add-Content -LiteralPath '$sequencePath' -Value `"provision ids=`$(`$DataStoreId -join ',')`"
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            @"
param([string] `$EnvironmentFile, [int[]] `$SchoolYear, [long[]] `$DataStoreId, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value `"seed years=`$(`$SchoolYear -join ',') ids=`$(`$DataStoreId -join ',')`"
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "load-dms-seed-data.ps1") -Encoding utf8

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -LoadSeedData `
                -SeedDataPath $script:repo.DockerComposeRoot `
                -SchoolYearRange "2024-2025" `
                -AddSmokeTestCredentials

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence[0] | Should -Be "start-infra EnableConfig=True"
            $sequence[1] | Should -Be "configure range=2024-2025 noDataStore=False smoke=True"
            $sequence[2] | Should -Be "provision ids=101,102"
            $sequence[3] | Should -Be "start-dms"
            $sequence[4] | Should -Be "seed years=2024,2025 ids="
        }

        It "passes route-unqualified configured data store to seed by DataStoreId" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "sequence.txt"

            "param([switch] `$InfraOnly, [switch] `$DmsOnly, [switch] `$EnableConfig, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); if (`$InfraOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra' } else { Add-Content -LiteralPath '$sequencePath' -Value 'start-dms' }" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value 'configure'
[pscustomobject]@{
    DataStoreIds = [long[]] @(42)
    SelectedDataStoreIds = [long[]] @(42)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedDataStores = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            "param([long[]] `$DataStoreId, [int[]] `$SchoolYear, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value (`"seed ids=`$(`$DataStoreId -join ',') years=`$(`$SchoolYear -join ',')`")" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "load-dms-seed-data.ps1") -Encoding utf8

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -LoadSeedData -SeedDataPath $script:repo.DockerComposeRoot

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence[-1] | Should -Be "seed ids=42 years="
        }

        It "stops after provisioning failure and does not start DMS or seed" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "sequence.txt"

            "param([switch] `$InfraOnly, [switch] `$DmsOnly, [switch] `$EnableConfig, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); if (`$InfraOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra' } elseif (`$DmsOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-dms' }" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value 'configure'
[pscustomobject]@{
    DataStoreIds = [long[]] @(42)
    SelectedDataStoreIds = [long[]] @(42)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedDataStores = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'; throw 'provision failed'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'seed'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "load-dms-seed-data.ps1") -Encoding utf8

            { & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -LoadSeedData -SeedDataPath $script:repo.DockerComposeRoot } |
                Should -Throw -ExpectedMessage "*provision failed*"

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence | Should -Be @("start-infra", "configure", "provision")
        }

        It "passes a derived env into DMS-only startup" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "sequence.txt"

            @"
param([switch] `$InfraOnly, [switch] `$DmsOnly, [string] `$EnvironmentFile, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
if (`$InfraOnly) {
    Add-Content -LiteralPath '$sequencePath' -Value 'start-infra'
}
elseif (`$DmsOnly) {
    Add-Content -LiteralPath '$sequencePath' -Value 'start-dms'
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
[pscustomobject]@{
    DataStoreIds = [long[]] @(42)
    SelectedDataStoreIds = [long[]] @(42)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedDataStores = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence[-1] | Should -Be "start-dms"
        }
    }

    Context "DMS start script branch messaging" {
        It "start-local-dms.ps1 reports the no-manifest path" {
            $startScript = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") -Raw

            $startScript | Should -Match 'if \(\$bootstrapManifestPresent\)'
            $startScript | Should -Match 'No bootstrap manifest detected; starting DMS\.'
        }

        It "start-published-dms.ps1 reports the no-manifest path" {
            $startScript = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1") -Raw

            $startScript | Should -Match 'if \(\$bootstrapManifestPresent\)'
            $startScript | Should -Match 'No bootstrap manifest detected; starting published DMS\.'
        }
    }

    Context "effective database target grouping" {
        It "treats two instances with the same database name on different hosts as separate targets" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=shared_name;'
                        dataStoreContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        name = "B"
                        connectionString = 'host=other-postgresql;port=5432;username=postgres;password=secret-pass;database=shared_name;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1, 2)

            $captured = @(Get-Content -LiteralPath $capturePath)
            @($captured | Where-Object { $_ -eq "BEGIN" }).Count | Should -Be 2
        }

        It "treats two instances with the same database name on different ports as separate targets" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "A"
                        connectionString = 'host=localhost;port=15432;username=postgres;password=secret-pass;database=shared_name;'
                        dataStoreContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        name = "B"
                        connectionString = 'host=localhost;port=15433;username=postgres;password=secret-pass;database=shared_name;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1, 2)

            $captured = @(Get-Content -LiteralPath $capturePath)
            @($captured | Where-Object { $_ -eq "BEGIN" }).Count | Should -Be 2
        }

        It "treats two instances sharing host/port/db under different users as separate targets" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "RoleA"
                        connectionString = 'host=dms-postgresql;port=5432;username=app_role_a;password=secret-pass;database=shared_db;'
                        dataStoreContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        name = "RoleB"
                        connectionString = 'host=dms-postgresql;port=5432;username=app_role_b;password=secret-pass;database=shared_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1, 2)

            $captured = @(Get-Content -LiteralPath $capturePath)
            @($captured | Where-Object { $_ -eq "BEGIN" }).Count | Should -Be 2
        }
    }

    Context "host-side target connection conversion" {
        It "preserves per-instance username, password, and database from the stored connection string" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Tenant-A"
                        connectionString = 'host=dms-postgresql;port=5432;username=tenant_a_user;password=tenant_a_secret;database=tenant_a_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=localhost"
            $connectionString | Should -Match "port=5544"
            $connectionString | Should -Match "username=tenant_a_user"
            $connectionString | Should -Match "password=tenant_a_secret"
            $connectionString | Should -Match "database=tenant_a_db"
        }

        It "preserves non-default external host and port for instances not on dms-postgresql:5432" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "External"
                        connectionString = 'host=managed-pg.example.com;port=5439;username=ops_user;password=ops_pass;database=ext_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=managed-pg.example.com"
            $connectionString | Should -Match "port=5439"
            $connectionString | Should -Match "username=ops_user"
            $connectionString | Should -Match "database=ext_db"
            $connectionString | Should -Not -Match "host=localhost"
        }

        It "provisions MSSQL-style connection strings with --dialect mssql and host-side translation" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-mssql-engine.env"
            Get-Content -LiteralPath $script:repo.EnvFile |
                Set-Content -LiteralPath $envFile -Encoding utf8
            Add-Content -LiteralPath $envFile -Value "DMS_DATASTORE=mssql"

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "MsSql"
                        connectionString = 'Server=dms-mssql,1433;Database=db1;User Id=sa;Password=foo;TrustServerCertificate=true;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            # SchemaTools is invoked with the mssql dialect, auto-detected from the connection string.
            $captured | Should -Contain "--dialect"
            $captured | Should -Contain "mssql"
            $captured | Should -Not -Contain "pgsql"

            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            # The Docker-internal server is translated to the host-side mapped MSSQL_PORT...
            $connectionString | Should -Match "127\.0\.0\.1,15433"
            $connectionString | Should -Not -Match "dms-mssql"
            # ...while the database, user, and other stored options survive verbatim.
            $connectionString | Should -Match "Database=db1"
            $connectionString | Should -Match "User Id=sa"
            $connectionString | Should -Match "TrustServerCertificate=true"
        }

        It "preserves an external (non-Docker) MSSQL server without translation" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-mssql-engine-external.env"
            Get-Content -LiteralPath $script:repo.EnvFile |
                Set-Content -LiteralPath $envFile -Encoding utf8
            Add-Content -LiteralPath $envFile -Value "DMS_DATASTORE=mssql"

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "ExternalMsSql"
                        connectionString = 'Server=managed-mssql.example.com,1433;Database=ext_db;User Id=ops;Password=ops_pass;TrustServerCertificate=true;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "mssql"
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "managed-mssql.example.com,1433"
            $connectionString | Should -Match "Database=ext_db"
            $connectionString | Should -Not -Match "127\.0\.0\.1"
        }

        It "carries SSL and timeout options through the host-side translation" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Secured"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=secured_db;SSL Mode=Require;Trust Server Certificate=true;Timeout=45;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            # Host and port are translated to host-side coordinates...
            $connectionString | Should -Match "host=localhost"
            $connectionString | Should -Match "port=5544"
            $connectionString | Should -Not -Match "dms-postgresql"
            # ...while every other stored option survives verbatim rather than being dropped.
            $connectionString | Should -Match "database=secured_db"
            $connectionString | Should -Match "SSL Mode=Require"
            $connectionString | Should -Match "Trust Server Certificate=true"
            $connectionString | Should -Match "Timeout=45"
        }

        It "carries options through unchanged for external (non-translated) hosts" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "ExternalSecured"
                        connectionString = 'host=managed-pg.example.com;port=5439;username=ops_user;password=ops_pass;database=ext_db;SSL Mode=VerifyFull;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=managed-pg.example.com"
            $connectionString | Should -Match "port=5439"
            $connectionString | Should -Match "SSL Mode=VerifyFull"
            $connectionString | Should -Not -Match "host=localhost"
        }

        It "carries a quoted-semicolon password through the host-side translation intact" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "QuotedSemicolon"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password="abc;123";database=quoted_db;SSL Mode=Require;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]

            # The password embeds a semicolon, so the value is quoted and a regex match on
            # "password=abc;123" would not work. Parse the emitted string back through the same
            # builder and assert the value survived intact.
            $reparsed = [System.Data.Common.DbConnectionStringBuilder]::new()
            $reparsed.set_ConnectionString($connectionString)
            $reparsed.get_Item("password") | Should -Be 'abc;123'
            $reparsed.get_Item("host") | Should -Be 'localhost'
            $reparsed.get_Item("port") | Should -Be '5544'
            $reparsed.get_Item("database") | Should -Be 'quoted_db'
            $reparsed.get_Item("ssl mode") | Should -Be 'Require'
            $reparsed.ContainsKey("host") | Should -BeTrue
        }

        It "carries a quoted password with semicolons and equals through an external host intact" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "ExternalQuoted"
                        connectionString = 'host=managed-pg.example.com;port=5439;username=ops_user;password="p;w=d/q";database=ext_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]

            # No translation occurs for an external host; the quoted password must still round-trip
            # uncorrupted through the builder.
            $reparsed = [System.Data.Common.DbConnectionStringBuilder]::new()
            $reparsed.set_ConnectionString($connectionString)
            $reparsed.get_Item("password") | Should -Be 'p;w=d/q'
            $reparsed.get_Item("host") | Should -Be 'managed-pg.example.com'
            $reparsed.get_Item("port") | Should -Be '5439'
            $reparsed.get_Item("database") | Should -Be 'ext_db'
        }
    }

    Context "Resolve-TargetDialect" {
        It "resolves a SQL Server connection string (Server key) to mssql" {
            . $script:repo.ProvisionScript

            $builder = ConvertTo-ConnectionStringBuilder -ConnectionString `
                'Server=dms-mssql,1433;Database=db1;User Id=sa;Password=foo;TrustServerCertificate=true;'

            Resolve-TargetDialect -Builder $builder | Should -Be "mssql"
        }

        It "resolves a SQL Server connection string (Data Source key) to mssql" {
            . $script:repo.ProvisionScript

            $builder = ConvertTo-ConnectionStringBuilder -ConnectionString `
                'Data Source=dms-mssql,1433;Database=db1;User Id=sa;Password=foo;'

            Resolve-TargetDialect -Builder $builder | Should -Be "mssql"
        }

        It "resolves a PostgreSQL connection string that uses the User Id alias for Username to pgsql" {
            . $script:repo.ProvisionScript

            # "User Id" is a legal Npgsql alias for Username, so it also matches the mssql "user
            # id" marker. Host is never a valid SqlClient key, so its presence must take
            # precedence and resolve to pgsql rather than being misrouted to mssql.
            $builder = ConvertTo-ConnectionStringBuilder -ConnectionString `
                'Host=localhost;Port=5432;User Id=postgres;Password=x;Database=edfi;'

            Resolve-TargetDialect -Builder $builder | Should -Be "pgsql"
        }

        It "resolves a PostgreSQL connection string that uses the Server alias for Host to pgsql" {
            . $script:repo.ProvisionScript

            # "Server" is a legal Npgsql alias for Host, so a PostgreSQL connection string built
            # from that alias (with no "host" key at all) would otherwise be misrouted to mssql
            # by the Server marker below. Port and Username are never valid SqlClient keys, so
            # their presence here is definitive and must take precedence.
            $builder = ConvertTo-ConnectionStringBuilder -ConnectionString `
                'Server=my-pg;Port=5432;Username=u;Password=p;Database=d;'

            Resolve-TargetDialect -Builder $builder | Should -Be "pgsql"
        }

        It "resolves a SQL Server connection string (Initial Catalog/User Id/TrustServerCertificate) to mssql" {
            . $script:repo.ProvisionScript

            $builder = ConvertTo-ConnectionStringBuilder -ConnectionString `
                'Server=dms-mssql,1433;Initial Catalog=db1;User Id=sa;TrustServerCertificate=true;Password=foo;'

            Resolve-TargetDialect -Builder $builder | Should -Be "mssql"
        }

        It "throws when neither a PostgreSQL host key nor any SQL Server key is present" {
            . $script:repo.ProvisionScript

            $builder = ConvertTo-ConnectionStringBuilder -ConnectionString `
                'password=secret-pass;database=no_host_db;'

            { Resolve-TargetDialect -Builder $builder } |
                Should -Throw -ExpectedMessage "*carries none of the PostgreSQL keys (host, username, port, sslmode) and no SQL Server key*"
        }
    }

    Context "configure result contract" {
        It "returns SelectedDataStoreIds plus DataStoreIds" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 99
                        name = "Sole"
                        dataStoreContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $script:repo.EnvFile -NoDataStore

            $result.PSObject.Properties.Name | Should -Contain "SelectedDataStoreIds"
            $result.PSObject.Properties.Name | Should -Contain "DataStoreIds"
            $result.SelectedDataStoreIds | Should -Be @([long]99)
            $result.DataStoreIds | Should -Be @([long]99)
        }

        It "includes CMSReadOnlyAccess block when env supplies the client id" {
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-with-ro.env"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
MSSQL_PORT=15433
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
CONFIG_SERVICE_CLIENT_ID=CMSReadOnlyAccess
CONFIG_SERVICE_CLIENT_SCOPE=edfi_admin_api/readonly_access
CONFIG_SERVICE_CLIENT_SECRET=my-ro-secret
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Sole"
                        dataStoreContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $envFile -NoDataStore

            $result.PSObject.Properties.Name | Should -Contain "CMSReadOnlyAccess"
            $result.CMSReadOnlyAccess["ClientId"] | Should -Be "CMSReadOnlyAccess"
            $result.CMSReadOnlyAccess["Scope"] | Should -Be "edfi_admin_api/readonly_access"
            $result.CMSReadOnlyAccess["ClientSecret"] | Should -Be "my-ro-secret"
        }
    }

    Context "provisioning summary and IDE guidance" {
        It "emits the post-provisioning summary listing each provisioned target" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Single"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=summary_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            $output = & {
                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)
            } *>&1 | Out-String

            $output | Should -Match "Schema Provisioning Summary"
            $output | Should -Match "database=summary_db"
            $output | Should -Match "host=localhost"
            $output | Should -Match "data-store-ids=\[1\]"
            $output | Should -Match "status=Provisioned"
        }

        It "emits IDE next-step guidance showing the staged workspace is runtime-authoritative" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 7
                        name = "Single"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=guidance_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            $output = & {
                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(7)
            } *>&1 | Out-String

            $output | Should -Match "IDE next-step guidance"
            $output | Should -Match "AppSettings__UseApiSchemaPath = true"
            $output | Should -Match "AppSettings__ApiSchemaPath"
            $output | Should -Match "runtime-authoritative"
        }

        It "guidance generator produces deterministic lines from a schema workspace and target list" {
            . $script:repo.ProvisionScript

            $schemaWorkspace = [pscustomobject]@{
                BootstrapManifestPath = "/tmp/.bootstrap/bootstrap-manifest.json"
                ApiSchemaManifestPath = "/tmp/.bootstrap/ApiSchema/bootstrap-api-schema-manifest.json"
                CoreSchemaPath = "/tmp/.bootstrap/ApiSchema/schemas/Ed-Fi/ApiSchema.json"
                ExtensionSchemaPaths = [string[]]@()
                EffectiveSchemaHash = "hash-xyz"
                WorkspaceFingerprint = "fp"
            }
            $targets = @(
                [pscustomobject]@{
                    DatabaseName = "td"
                    Host = "h"
                    Port = "5432"
                    Dialect = "pgsql"
                    Username = "u"
                    DataStoreIds = [long[]]@(1, 2)
                    Status = "Provisioned"
                }
            )

            $lines = Get-ProvisionIdeGuidance -SchemaWorkspace $schemaWorkspace -ProvisionedTargets $targets

            ($lines -join "`n") | Should -Match "Provisioned 1 database target"
            ($lines -join "`n") | Should -Match "database=td host=h port=5432 user=u"
            ($lines -join "`n") | Should -Match "runtime-authoritative"
        }

        It "Format-LogSafePath preserves backslashes so Windows paths survive sanitization" {
            . $script:repo.ProvisionScript

            Format-LogSafePath "C:\work\ApiSchema" | Should -Be "C:\work\ApiSchema"
            # Control characters that enable log forging are still stripped.
            Format-LogSafePath "C:\work\ApiSchema`r`nINJECTED" | Should -Be "C:\work\ApiSchemaINJECTED"
        }

        It "Format-LogSafePath preserves printable path characters and strips only control characters" {
            . $script:repo.ProvisionScript

            # Spaces, parentheses, '#', hyphens, and backslashes are all path-legal and must survive.
            Format-LogSafePath 'C:\Program Files (x86)\Ed-Fi\Api #1' | Should -Be 'C:\Program Files (x86)\Ed-Fi\Api #1'
            Format-LogSafePath '/srv/ed fi/api (staging)/schema#2.json' | Should -Be '/srv/ed fi/api (staging)/schema#2.json'
            # Tabs, carriage returns, and newlines are control characters and are removed.
            Format-LogSafePath "a`tb`r`nc" | Should -Be "abc"
        }

        It "guidance preserves backslashes in Windows-style staged paths" {
            . $script:repo.ProvisionScript

            $schemaWorkspace = [pscustomobject]@{
                BootstrapManifestPath = "C:\work\.bootstrap\bootstrap-manifest.json"
                ApiSchemaManifestPath = "C:\work\.bootstrap\ApiSchema\bootstrap-api-schema-manifest.json"
                CoreSchemaPath = "C:\work\.bootstrap\ApiSchema\schemas\Ed-Fi\ApiSchema.json"
                ExtensionSchemaPaths = [string[]]@()
                EffectiveSchemaHash = "hash-xyz"
                WorkspaceFingerprint = "fp"
            }

            $joined = (Get-ProvisionIdeGuidance -SchemaWorkspace $schemaWorkspace -ProvisionedTargets @()) -join "`n"

            # The path lines feed Format-LogSafePath directly, so backslashes survive on every platform.
            $joined | Should -Match ([regex]::Escape("C:\work\.bootstrap\bootstrap-manifest.json"))
            $joined | Should -Match ([regex]::Escape("C:\work\.bootstrap\ApiSchema\bootstrap-api-schema-manifest.json"))
        }
    }

    Context "shared env-file helpers" {
        It "Resolve-LocalSettingsEnvironmentFile throws on missing file" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            { Resolve-LocalSettingsEnvironmentFile -Path "/does/not/exist.env" -DockerComposeRoot $script:repo.DockerComposeRoot } |
                Should -Throw -ExpectedMessage "*Environment file not found*"
        }

        It "Resolve-LocalSettingsEnvironmentFile returns the absolute env path for the supplied file" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $resolved = Resolve-LocalSettingsEnvironmentFile -Path $script:repo.EnvFile -DockerComposeRoot $script:repo.DockerComposeRoot

            [System.IO.Path]::IsPathRooted($resolved) | Should -BeTrue
            $resolved | Should -Be ([System.IO.Path]::GetFullPath($script:repo.EnvFile))
        }

        It "Resolve-LocalSettingsEnvironmentFile seeds .env from .env.example on first default resolution" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $seededEnv = Join-Path $script:repo.DockerComposeRoot ".env"
            Test-Path -LiteralPath $seededEnv | Should -BeFalse

            $resolved = Resolve-LocalSettingsEnvironmentFile -Path "" -DockerComposeRoot $script:repo.DockerComposeRoot

            # .env.example is never consumed at runtime: the resolver materializes .env once as
            # an identical copy and resolves to it, giving the user a durable file to edit.
            $resolved | Should -Be ([System.IO.Path]::GetFullPath($seededEnv))
            Get-Content -LiteralPath $seededEnv -Raw | Should -Be (Get-Content -LiteralPath $script:repo.EnvFile -Raw)

            # A later default resolution reuses the seeded .env (with any user edits) untouched.
            "CUSTOM_MARKER=kept" | Add-Content -LiteralPath $seededEnv
            Resolve-LocalSettingsEnvironmentFile -Path "" -DockerComposeRoot $script:repo.DockerComposeRoot |
                Should -Be $resolved
            Get-Content -LiteralPath $seededEnv -Raw | Should -Match "CUSTOM_MARKER=kept"
        }

        It "Get-EnvValue returns the supplied default when the key is absent or blank" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $envValues = @{ A = "alpha"; B = "" }
            Get-EnvValue -EnvValues $envValues -Name "A" -DefaultValue "fallback" | Should -Be "alpha"
            Get-EnvValue -EnvValues $envValues -Name "B" -DefaultValue "fallback" | Should -Be "fallback"
            Get-EnvValue -EnvValues $envValues -Name "Z" -DefaultValue "fallback" | Should -Be "fallback"
        }
    }

    Context "EnvironmentFile precedence" {
        It "configure-local-data-store.ps1 reads only the supplied -EnvironmentFile, not ambient process env" {
            $isolatedEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-tenant.env"
            @"
POSTGRES_PASSWORD=isolated-pass
POSTGRES_DB_NAME=isolated_db
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
CONFIG_SERVICE_TENANT=isolated-tenant
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $isolatedEnvFile -Encoding utf8

            # Set conflicting values in process env; the script must ignore them and use the file.
            $env:POSTGRES_PASSWORD = "process-pass"
            $env:POSTGRES_DB_NAME = "process_db"
            $env:CONFIG_SERVICE_TENANT = "process-tenant"
            try {
                . $script:repo.ConfigureScript

                function Add-CmsClient { }
                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 1
                            name = "Sole"
                            dataStoreContexts = @()
                        }
                    )
                }

                $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $isolatedEnvFile -NoDataStore

                $result.Tenant | Should -Be "isolated-tenant"
            }
            finally {
                $env:POSTGRES_PASSWORD = $null
                $env:POSTGRES_DB_NAME = $null
                $env:CONFIG_SERVICE_TENANT = $null
            }
        }

        It "provision-dms-schema.ps1 host-side connection uses POSTGRES_PORT from supplied env file, not process env" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot

            $isolatedEnvFile = Join-Path $script:repo.DockerComposeRoot "env-port-isolation.env"
            @"
POSTGRES_PASSWORD=isolated-pass
POSTGRES_DB_NAME=isolated_db
POSTGRES_PORT=9876
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $isolatedEnvFile -Encoding utf8

            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            $env:POSTGRES_PORT = "1111"

            try {
                . $script:repo.ProvisionScript

                function Add-CmsClient { }
                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 1
                            name = "Sole"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=isolated_db;'
                            dataStoreContexts = @()
                        }
                    )
                }

                Invoke-ProvisionDmsSchema -EnvironmentFile $isolatedEnvFile -DataStoreId @(1)

                $captured = @(Get-Content -LiteralPath $capturePath)
                $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
                $connectionString | Should -Match "port=9876"
                $connectionString | Should -Not -Match "port=1111"
            }
            finally {
                $env:POSTGRES_PORT = $null
            }
        }
    }

    Context "wrapper consumes SelectedDataStoreIds" {
        It "Resolve-WrapperSelectedDataStoreIds prefers SelectedDataStoreIds over DataStoreIds" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

            $configured = [pscustomobject]@{
                SelectedDataStoreIds = [long[]]@(101, 102)
                DataStoreIds = [long[]]@(901, 902)
                HasRouteQualifiedDataStores = $false
            }
            $resolved = Resolve-WrapperSelectedDataStoreIds -ConfigureResult $configured

            $resolved | Should -Be @([long]101, [long]102)
        }

        It "Resolve-WrapperSelectedDataStoreIds falls back to DataStoreIds when SelectedDataStoreIds is absent" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

            $configured = [pscustomobject]@{
                DataStoreIds = [long[]]@(42)
                HasRouteQualifiedDataStores = $false
            }
            $resolved = Resolve-WrapperSelectedDataStoreIds -ConfigureResult $configured

            $resolved | Should -Be @([long]42)
        }

        It "Resolve-WrapperSelectedDataStoreIds throws when neither property is present" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

            $configured = [pscustomobject]@{ Tenant = "" }

            { Resolve-WrapperSelectedDataStoreIds -ConfigureResult $configured } |
                Should -Throw -ExpectedMessage "*missing SelectedDataStoreIds*"
        }
    }

    Context "provision is an auth consumer only" {
        It "does not call Add-CmsClient during provisioning" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { throw "Add-CmsClient must not be called during provisioning." }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Sole"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=auth_consumer_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1) } |
                Should -Not -Throw
        }

        It "surfaces an actionable error pointing to configure when Get-CmsToken fails" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { throw "Add-CmsClient must not be called during provisioning." }
            function Get-CmsToken { throw "401 Unauthorized: invalid_client" }
            function Get-DataStore { return @() }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1) } |
                Should -Throw -ExpectedMessage "*configure-local-data-store.ps1*"
        }
    }

    Context "PostgreSQL port defaults" {
        It "defaults a missing port to 5432 for an external PostgreSQL host" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "ExternalNoPort"
                        connectionString = 'host=managed-pg.example.com;username=ops_user;password=ops_pass;database=ext_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=managed-pg.example.com"
            $connectionString | Should -Match "port=5432"
            $connectionString | Should -Not -Match "host=localhost"
        }

        It "defaults a missing port for dms-postgresql to the host-side mapped POSTGRES_PORT" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "DockerInternalNoPort"
                        connectionString = 'host=dms-postgresql;username=postgres;password=secret-pass;database=docker_internal_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=localhost"
            $connectionString | Should -Match "port=5544"
        }

        It "still fails fast when both host and port are missing" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "MissingHost"
                        connectionString = 'username=postgres;password=secret-pass;database=no_host_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            # "username" is a definitive PostgreSQL marker, so this resolves to pgsql and then
            # fails further along, on the missing host key specifically, rather than on
            # dialect resolution itself.
            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1) } |
                Should -Throw -ExpectedMessage "*missing the host key*"
        }
    }

    Context "CMSReadOnlyAccess presence-gated emission" {
        It "Resolve-CmsReadOnlyAccessFromEnv returns null when none of the three keys are present" {
            . $script:repo.ConfigureScript

            $result = Resolve-CmsReadOnlyAccessFromEnv -EnvValues @{
                POSTGRES_PASSWORD = "x"
            }

            $result | Should -BeNullOrEmpty
        }

        It "Resolve-CmsReadOnlyAccessFromEnv defaults client id and scope when only the secret is supplied" {
            . $script:repo.ConfigureScript

            $result = Resolve-CmsReadOnlyAccessFromEnv -EnvValues @{
                CONFIG_SERVICE_CLIENT_SECRET = "explicit-secret"
            }

            $result | Should -Not -BeNullOrEmpty
            $result["ClientId"] | Should -Be "CMSReadOnlyAccess"
            $result["Scope"] | Should -Be "edfi_admin_api/readonly_access"
            $result["ClientSecret"] | Should -Be "explicit-secret"
        }

        It "configure result omits CMSReadOnlyAccess when the env file lacks the keys" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Sole"
                        dataStoreContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $script:repo.EnvFile -NoDataStore

            $result.PSObject.Properties.Name | Should -Not -Contain "CMSReadOnlyAccess"
        }

        It "Get-ProvisionCmsReadOnlyAccessGuidance returns empty when no CONFIG_SERVICE_CLIENT_* keys are present" {
            . $script:repo.ProvisionScript

            $lines = Get-ProvisionCmsReadOnlyAccessGuidance -EnvValues @{
                POSTGRES_PASSWORD = "x"
            }

            $lines | Should -BeNullOrEmpty
        }

        It "Get-ProvisionCmsReadOnlyAccessGuidance emits the block when an explicit env key is supplied" {
            . $script:repo.ProvisionScript

            $lines = Get-ProvisionCmsReadOnlyAccessGuidance -EnvValues @{
                CONFIG_SERVICE_CLIENT_SECRET = "explicit-secret"
            }

            ($lines -join "`n") | Should -Match "ConfigurationServiceSettings__ClientId = CMSReadOnlyAccess"
            ($lines -join "`n") | Should -Match "ConfigurationServiceSettings__ClientSecret = \(present in environment file\)"
        }
    }

    Context "missing-manifest warning surfaces the post-bootstrap contract" {
        It "warns when no .bootstrap workspace is present and -IsTeardown is false" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-manifest.psm1") -Force

            $warnings = & { Invoke-BootstrapStartupConfiguration -IsTeardown:$false } 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

            $warnings.Count | Should -BeGreaterThan 0
            ($warnings | ForEach-Object Message) -join " " | Should -Match "No bootstrap manifest detected"
            ($warnings | ForEach-Object Message) -join " " | Should -Match "bootstrap-\(local\|published\)-dms.ps1 wrapper"
            ($warnings | ForEach-Object Message) -join " " | Should -Match "Bootstrap schema provisioning will NOT be run"
        }

        It "stays silent when a .bootstrap workspace is present" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-manifest.psm1") -Force

            $warnings = & { Invoke-BootstrapStartupConfiguration -IsTeardown:$false } 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

            $warnings | Where-Object { $_.Message -match "No bootstrap manifest detected" } | Should -BeNullOrEmpty
        }

        It "stays silent during teardown even when no .bootstrap workspace is present" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-manifest.psm1") -Force

            $warnings = & { Invoke-BootstrapStartupConfiguration -IsTeardown:$true } 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

            $warnings | Where-Object { $_.Message -match "No bootstrap manifest detected" } | Should -BeNullOrEmpty
        }
    }

    Context "staged --schema path order matches manifest declaration order" {
        BeforeAll {
            function script:Get-OrderedSchemaPaths {
                param([string[]]$Captured)

                $paths = @()
                for ($i = 0; $i -lt $Captured.Count; $i++) {
                    if ($Captured[$i] -eq "--schema") {
                        $paths += ($Captured[$i + 1]).Replace('\', '/')
                    }
                }
                return ,$paths
            }

            function script:Invoke-OrderedProvisionCapture {
                param(
                    [Parameter(Mandatory)]
                    [string]$CaptureName,
                    [Parameter(Mandatory)]
                    [string]$DatabaseName,
                    [string[]]$Extensions = @("Sample")
                )

                New-StagedSchemaWorkspace `
                    -DockerComposeRoot $script:repo.DockerComposeRoot `
                    -Extensions $Extensions
                $capturePath = Join-Path $script:repo.RepoRoot $CaptureName
                $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
                $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

                . $script:repo.ProvisionScript

                $connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=' +
                    '${POSTGRES_PASSWORD};database=' + $DatabaseName + ';'
                function Add-CmsClient { }
                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 1
                            name = "Sole"
                            connectionString = $connectionString
                            dataStoreContexts = @()
                        }
                    )
                }

                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

                return @(Get-Content -LiteralPath $capturePath)
            }
        }

        It "core-only manifest emits a single --schema for the Ed-Fi core schema" {
            $captured = Invoke-OrderedProvisionCapture `
                -CaptureName "schema-tool-args-core-only.txt" `
                -DatabaseName "core_only_db" `
                -Extensions @()

            $schemaPaths = Get-OrderedSchemaPaths -Captured $captured
            $schemaPaths.Count | Should -Be 1
            $schemaPaths[0] | Should -Match "schemas/Ed-Fi/ApiSchema\.json$"
        }

        It "core + single extension emits --schema in [core, extension] order" {
            $captured = Invoke-OrderedProvisionCapture `
                -CaptureName "schema-tool-args-core-plus-sample.txt" `
                -DatabaseName "core_plus_sample_db" `
                -Extensions @("Sample")

            $schemaPaths = Get-OrderedSchemaPaths -Captured $captured
            $schemaPaths.Count | Should -Be 2
            $schemaPaths[0] | Should -Match "schemas/Ed-Fi/ApiSchema\.json$"
            $schemaPaths[1] | Should -Match "schemas/Sample/ApiSchema\.json$"
        }

        It "core + multiple extensions emits --schema in [core, ext1, ext2] declaration order" {
            $captured = Invoke-OrderedProvisionCapture `
                -CaptureName "schema-tool-args-multi-extension.txt" `
                -DatabaseName "multi_ext_db" `
                -Extensions @("Sample", "Homograph")

            $schemaPaths = Get-OrderedSchemaPaths -Captured $captured
            $schemaPaths.Count | Should -Be 3
            $schemaPaths[0] | Should -Match "schemas/Ed-Fi/ApiSchema\.json$"
            $schemaPaths[1] | Should -Match "schemas/Sample/ApiSchema\.json$"
            $schemaPaths[2] | Should -Match "schemas/Homograph/ApiSchema\.json$"
        }

        It "identical schema argv on repeated invocations against the same workspace" {
            New-StagedSchemaWorkspace `
                -DockerComposeRoot $script:repo.DockerComposeRoot `
                -Extensions @("Sample")

            $toolDir1 = Join-Path $script:repo.RepoRoot "tool-run-1"
            $toolDir2 = Join-Path $script:repo.RepoRoot "tool-run-2"
            New-Item -ItemType Directory -Path $toolDir1 -Force | Out-Null
            New-Item -ItemType Directory -Path $toolDir2 -Force | Out-Null
            $capturePath1 = Join-Path $script:repo.RepoRoot "schema-tool-args-idempotent-1.txt"
            $capturePath2 = Join-Path $script:repo.RepoRoot "schema-tool-args-idempotent-2.txt"
            $fakeTool1 = New-FakeSchemaTool -Directory $toolDir1 -CapturePath $capturePath1
            $fakeTool2 = New-FakeSchemaTool -Directory $toolDir2 -CapturePath $capturePath2
            $connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=idempotent_db;'

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Sole"
                        connectionString = $connectionString
                        dataStoreContexts = @()
                    }
                )
            }

            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool1
            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool2
            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -DataStoreId @(1)

            $first = Get-OrderedSchemaPaths -Captured @(Get-Content -LiteralPath $capturePath1)
            $second = Get-OrderedSchemaPaths -Captured @(Get-Content -LiteralPath $capturePath2)

            ($first -join "|") | Should -Be ($second -join "|")
        }
    }

    Context "Resolve-BootstrapAdminClient" {
        It "returns the historical defaults when neither override key is present" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $resolved = Resolve-BootstrapAdminClient -EnvValues @{ POSTGRES_PASSWORD = "x" }

            $resolved.ClientId | Should -Be "dms-data-store-admin"
            $resolved.ClientSecret | Should -Be "ValidClientSecret1234567890!Abcd"
        }

        It "returns env-file values when both overrides are supplied" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $resolved = Resolve-BootstrapAdminClient -EnvValues @{
                DMS_BOOTSTRAP_ADMIN_CLIENT_ID = "custom-admin"
                DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET = "custom-secret"
            }

            $resolved.ClientId | Should -Be "custom-admin"
            $resolved.ClientSecret | Should -Be "custom-secret"
        }

        It "applies the client id override while keeping the default secret" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $resolved = Resolve-BootstrapAdminClient -EnvValues @{
                DMS_BOOTSTRAP_ADMIN_CLIENT_ID = "id-only-admin"
            }

            $resolved.ClientId | Should -Be "id-only-admin"
            $resolved.ClientSecret | Should -Be "ValidClientSecret1234567890!Abcd"
        }

        It "applies the client secret override while keeping the default id" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $resolved = Resolve-BootstrapAdminClient -EnvValues @{
                DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET = "secret-only-value"
            }

            $resolved.ClientId | Should -Be "dms-data-store-admin"
            $resolved.ClientSecret | Should -Be "secret-only-value"
        }
    }

    Context "bootstrap admin client flows through to configure and provision" {
        It "configure-local-data-store.ps1 calls Add-CmsClient and Get-CmsToken with the env-resolved bootstrap admin client" {
            $overrideEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-bootstrap-admin.env"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
MSSQL_PORT=15433
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
DMS_BOOTSTRAP_ADMIN_CLIENT_ID=configure-side-admin
DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET=configure-side-secret
"@ | Set-Content -LiteralPath $overrideEnvFile -Encoding utf8

            . $script:repo.ConfigureScript

            $script:capturedAddCmsClient = $null
            $script:capturedGetCmsToken = $null
            function Add-CmsClient {
                param($CmsUrl, $ClientId, $ClientSecret, $DisplayName)
                $script:capturedAddCmsClient = [pscustomobject]@{
                    ClientId = $ClientId
                    ClientSecret = $ClientSecret
                }
            }
            function Get-CmsToken {
                param($CmsUrl, $ClientId, $ClientSecret)
                $script:capturedGetCmsToken = [pscustomobject]@{
                    ClientId = $ClientId
                    ClientSecret = $ClientSecret
                }
                return "token"
            }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Sole"
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ConfigureLocalDataStore -EnvironmentFile $overrideEnvFile -NoDataStore | Out-Null

            $script:capturedAddCmsClient.ClientId | Should -Be "configure-side-admin"
            $script:capturedAddCmsClient.ClientSecret | Should -Be "configure-side-secret"
            $script:capturedGetCmsToken.ClientId | Should -Be "configure-side-admin"
            $script:capturedGetCmsToken.ClientSecret | Should -Be "configure-side-secret"
        }

        It "provision-dms-schema.ps1 calls Get-CmsToken with the env-resolved bootstrap admin client and does not register" {
            $overrideEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-bootstrap-admin-prov.env"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
MSSQL_PORT=15433
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
DMS_BOOTSTRAP_ADMIN_CLIENT_ID=provision-side-admin
DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET=provision-side-secret
"@ | Set-Content -LiteralPath $overrideEnvFile -Encoding utf8

            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            $script:capturedGetCmsToken = $null
            function Add-CmsClient { throw "Add-CmsClient must not be called during provisioning." }
            function Get-CmsToken {
                param($CmsUrl, $ClientId, $ClientSecret)
                $script:capturedGetCmsToken = [pscustomobject]@{
                    ClientId = $ClientId
                    ClientSecret = $ClientSecret
                }
                return "token"
            }
            function Get-DataStore {
                return @(
                    [pscustomobject]@{
                        id = 1
                        name = "Sole"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=prov_admin_db;'
                        dataStoreContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $overrideEnvFile -DataStoreId @(1)

            $script:capturedGetCmsToken.ClientId | Should -Be "provision-side-admin"
            $script:capturedGetCmsToken.ClientSecret | Should -Be "provision-side-secret"
        }

        It "provision actionable error sanitizes an env-supplied client id containing log-injection characters" {
            $overrideEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-injection-id.env"
            $injectedId = "evil-admin`r`nFAKE-LOG-LINE"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
MSSQL_PORT=15433
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
DMS_BOOTSTRAP_ADMIN_CLIENT_ID=$injectedId
"@ | Set-Content -LiteralPath $overrideEnvFile -Encoding utf8

            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { throw "Add-CmsClient must not be called during provisioning." }
            function Get-CmsToken { throw "401 Unauthorized" }
            function Get-DataStore { return @() }

            $thrownMessage = $null
            try {
                Invoke-ProvisionDmsSchema -EnvironmentFile $overrideEnvFile -DataStoreId @(1)
            }
            catch {
                $thrownMessage = $_.Exception.Message
            }

            $thrownMessage | Should -Not -BeNullOrEmpty
            $thrownMessage | Should -Not -Match "`r"
            $thrownMessage | Should -Not -Match "`n"
            $thrownMessage | Should -Not -Match "FAKE-LOG-LINE"
            $thrownMessage | Should -Match "evil-admin"
        }
    }

    Context "connector setup" {
        It "start-all-services.ps1 starts PostgreSQL without legacy connector guidance" {
            $scriptPath = Join-Path $script:sourceDockerComposeRoot "start-all-services.ps1"

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
            $errors.Count | Should -Be 0

            $legacyConnectorScript = "setup" + "-connectors.ps1"
            $invokedCommands = @(
                $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true) |
                    ForEach-Object { $_.GetCommandName() }
            )
            $connectorInvocations = @($invokedCommands | Where-Object { $_ -and $_ -like "*$legacyConnectorScript" })
            $connectorInvocations | Should -BeNullOrEmpty

            $sourceText = Get-Content -LiteralPath $scriptPath -Raw
            $sourceText | Should -Not -Match ([regex]::Escape($legacyConnectorScript))
            $sourceText | Should -Not -Match 'kafka\.yml'
            $sourceText | Should -Not -Match ("dms" + "\.document")

            $sourceText | Should -Match '\$LASTEXITCODE -ne 0'
            $sourceText | Should -Match 'Failed to start PostgreSQL service'
        }

        It "removes legacy document-store connector setup files" {
            $removedConnectorFiles = @(
                "postgresql" + "_connector.json",
                "data_store" + "_connector_template.json",
                "setup" + "-connectors.ps1",
                "setup-data-store-kafka" + "-connectors.ps1"
            )

            foreach ($removedConnectorFile in $removedConnectorFiles) {
                Test-Path -LiteralPath (Join-Path $script:sourceDockerComposeRoot $removedConnectorFile) |
                    Should -BeFalse
            }
        }
    }

    Context "instance management E2E database setup hardening" {
        It "provisions each route-context test database through the E2E provisioning helper" {
            $e2eSetupScript = Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"
            $content = Get-Content -LiteralPath $e2eSetupScript -Raw

            $content | Should -Match 'provision-e2e-database\.ps1'
            $content | Should -Match 'foreach \(\$db in \$databases\)'
            $content | Should -Match '-DatabaseName \$db'
            $content | Should -Match '\$LASTEXITCODE -ne 0'
            $content | Should -Match 'Failed to provision route-context database'
        }

        It "verifies the relational schema after provisioning" {
            $e2eSetupScript = Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"
            $content = Get-Content -LiteralPath $e2eSetupScript -Raw

            $content | Should -Match 'Assert-RelationalSchemaProvisioned -Database \$db'
            $content | Should -Match 'dms\."EffectiveSchema"'
            $content | Should -Match 'edfi\.School'
            $content | Should -Match 'edfi\.Student'
        }

        It "does not pass the removed connector skip flag to start-local-dms.ps1" {
            $e2eSetupScript = Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"
            $content = Get-Content -LiteralPath $e2eSetupScript -Raw

            $content | Should -Match 'start-local-dms\.ps1'
            $content | Should -Not -Match 'SkipConnectorSetup'
        }
    }
}
