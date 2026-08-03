# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets', '', Justification = 'Pester tests intentionally shadow Invoke-WebRequest to stub HTTP calls.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Test stand-ins mirror the real authority parameter surface to bind named arguments; see the suppression on Assert-MssqlTopologyPhysicalConsistency.')]
param()

# Exact workspace ownership, recorded at creation. Fixtures below stage copies of repository
# scripts and modules inside temp workspaces this run creates, and EXECUTING a staged script
# imports env-utility (and siblings) from the staged path - module-table instances that outlive
# the deleted workspace and break any later suite in the same session that binds -ModuleName
# mocks. The file-level cleanup at the bottom may unload ONLY module instances rooted beneath a
# workspace this same run created and recorded through this registrar. Nothing else establishes
# ownership: not a directory-name prefix, not a module name, not living under the system temp
# directory - a caller-owned module beneath a lookalike-named directory is not this file's to
# touch, and the whole-file lifecycle tests at the bottom pin exactly that.
BeforeAll {
    $script:ownedWorkspaceRoot = [System.Collections.Generic.List[string]]::new()
    function script:Register-OwnedWorkspaceRoot {
        param([Parameter(Mandatory)] [string]$Path)
        $script:ownedWorkspaceRoot.Add([System.IO.Path]::GetFullPath($Path))
    }
}

Describe "DMS-1151 bootstrap schema deployment safety" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:sourceDockerComposeRoot = Join-Path $script:sourceRepoRoot "eng/docker-compose"

        function script:New-TestDirectory {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1151-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            # Recorded before any staged code can import from it: this exact root is what the
            # file-level cleanup is allowed to unload module instances from.
            Register-OwnedWorkspaceRoot -Path $path
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
                # configure-local-data-store.ps1 and provision-dms-schema.ps1 import the shared
                # Compose-equivalent resolver from this module.
                "database-safety.psm1",
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

            $toolPath = Join-Path $Directory "fake-api-schema-tools.ps1"
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

        # Remove-Item, not SetEnvironmentVariable with $null: PowerShell coerces $null to "", which
        # newer pwsh/.NET on Unix stores as a present-but-blank variable instead of removing it.
        Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue
        Remove-Item Env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK -ErrorAction SilentlyContinue
    }

    Context "public script contracts" {
        It "provision-dms-schema.ps1 exposes only the selector, env, engine overlay, and topology parameters" {
            $params = Get-DeclaredScriptParameters -Path $script:repo.ProvisionScript

            $params | Should -Contain "EnvironmentFile"
            $params | Should -Contain "DataStoreId"
            $params | Should -Contain "SchoolYear"
            $params | Should -Contain "DatabaseEngine"
            # The phase judges the database each selected target resolves to, which for a REUSED data
            # store is the only place its stored connection string is known - so the topology
            # declaration is part of this phase's public contract, not just configure's.
            $params | Should -Contain "SeparateConfigDatabase"
            $params | Should -Not -Contain "SchemaToolPath"
            $params | Should -Not -Contain "SeedTemplate"
            $params | Should -Not -Contain "LoadSeedData"
            $params | Should -Not -Contain "ApiSchemaPath"
            # Never a datastore-name parameter: the target comes from CMS, so a caller-authored name
            # here could only disagree with what will actually be provisioned.
            $params | Should -Not -Contain "DataStoreDatabaseName"
            $params.Count | Should -Be 5
        }

        It "provision-dms-schema.ps1 forwards the topology declaration from its parameter surface into the phase function" {
            # The manual-phase boundary: a switch the entry point accepts but drops would leave the
            # guard unreachable for exactly the direct invocation the start script's guidance prints.
            $content = Get-Content -LiteralPath $script:repo.ProvisionScript -Raw

            $content | Should -Match ([regex]::Escape('-SeparateConfigDatabase:$SeparateConfigDatabase'))
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
            $params | Should -Contain "DatabaseEngine"
            $params | Should -Not -Contain "SchemaToolPath"
            $params | Should -Not -Contain "DataStoreId"
            $params | Should -Not -Contain "SchoolYear"
            $params.Count | Should -Be 5
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
            # start-published-dms.ps1 keeps -NoDataStore, -SchoolYearRange, and
            # -AddSmokeTestCredentials until the published-image consumer path is migrated (separate task).
            # -LoadSeedData (the direct-SQL database-template path) has been removed.
            $params = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1")

            $params | Should -Not -Contain "LoadSeedData"
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

        It "invokes api-schema-tools once per target database with host-side connection settings" {
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

        It "forwards -SeparateConfigDatabase to the configure phase, not only to the start phase" {
            # The configure phase registers the DMS datastore, so it needs the same topology
            # declaration the start phase gets: without the forward it would judge a
            # separate-topology run as shared and could register the reserved CMS database.
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "topology-forwarding.txt"

            "param([switch] `$InfraOnly, [switch] `$DmsOnly, [switch] `$SeparateConfigDatabase, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); if (`$InfraOnly) { Add-Content -LiteralPath '$sequencePath' -Value `"start-infra separate=`$SeparateConfigDatabase`" }" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([switch] `$SeparateConfigDatabase, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value `"configure separate=`$SeparateConfigDatabase`"
[pscustomobject]@{
    DataStoreIds = [long[]] @(51)
    SelectedDataStoreIds = [long[]] @(51)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedDataStores = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly -SeparateConfigDatabase

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence | Should -Contain "start-infra separate=True"
            $sequence | Should -Contain "configure separate=True"
        }

        It "does not declare a separate topology to the configure phase when the run is shared" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "topology-forwarding-shared.txt"

            "param([switch] `$InfraOnly, [switch] `$DmsOnly, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); if (`$InfraOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra' }" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([switch] `$SeparateConfigDatabase, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value `"configure separate=`$SeparateConfigDatabase`"
[pscustomobject]@{
    DataStoreIds = [long[]] @(52)
    SelectedDataStoreIds = [long[]] @(52)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedDataStores = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-data-store.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -InfraOnly

            @(Get-Content -LiteralPath $sequencePath) | Should -Contain "configure separate=False"
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
        # ModuleOwnershipProbe: the whole-file ownership children run exactly this test, because
        # it provably exercises the staged-import lifecycle - the Describe's BeforeEach creates
        # the recorded isolated-repo workspace, and this It's first statement imports
        # env-utility.psm1 FROM that staged path.
        It "Resolve-LocalSettingsEnvironmentFile throws on missing file" -Tag "ModuleOwnershipProbe" {
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
        # Docker Compose interpolation gives a process/shell value precedence over the same key in the
        # env file, so the running containers receive the ambient value. The configure phase must read
        # with the same precedence: a file-only read would register CMS data stores (and select the
        # tenant) from values the running stack never received. The shared resolver in
        # database-safety.psm1 is the single rule for this across startup, readiness, provisioning,
        # destructive-reset safety, and CMS configuration.
        It "configure-local-data-store.ps1 resolves ambient process env over the supplied -EnvironmentFile (Compose precedence)" {
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

                $result.Tenant | Should -Be "process-tenant"
            }
            finally {
                $env:CONFIG_SERVICE_TENANT = $null
            }
        }

        It "configure-local-data-store.ps1 uses the supplied -EnvironmentFile values when no ambient override exists" {
            $isolatedEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-tenant-no-ambient.env"
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

            Remove-Item Env:CONFIG_SERVICE_TENANT -ErrorAction SilentlyContinue
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

        It "configure-local-data-store.ps1 registers the MSSQL data store with ambient credential and database-name overrides" {
            # The running SQL Server container received the ambient MSSQL_SA_PASSWORD /
            # MSSQL_DB_NAME (Compose interpolation), so the connection string registered in CMS
            # must carry exactly those values - including a password full of connection-string
            # metacharacters, which must round-trip through the safe builder unbroken.
            $isolatedEnvFile = Join-Path $script:repo.DockerComposeRoot "env-mssql-ambient.env"
            @"
POSTGRES_PASSWORD=isolated-pass
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $isolatedEnvFile -Encoding utf8

            $ambientPassword = 'Amb;ient "P@ss,word''=1 $x'
            $env:MSSQL_SA_PASSWORD = $ambientPassword
            $env:MSSQL_DB_NAME = "ambient_dms_db"
            try {
                . $script:repo.ConfigureScript

                function Add-CmsClient { }
                function Get-CmsToken { return "token" }
                $script:capturedDataStore = $null
                function Add-DataStore {
                    param($CmsUrl, $AccessToken, [System.Management.Automation.PSCredential]$PostgresCredential, $PostgresDbName, $ConnectionString, $Name, $DataStoreType, $Tenant)
                    $script:capturedDataStore = [pscustomobject]@{
                        ConnectionString = $ConnectionString
                        PostgresDbName = $PostgresDbName
                    }
                    return [long]42
                }

                $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $isolatedEnvFile -DatabaseEngine mssql

                $result.SelectedDataStoreIds | Should -Be @([long]42)
                $parsed = [System.Data.Common.DbConnectionStringBuilder]::new()
                $parsed.set_ConnectionString($script:capturedDataStore.ConnectionString)
                $parsed["Password"] | Should -Be $ambientPassword
                $parsed["Database"] | Should -Be "ambient_dms_db"
            }
            finally {
                Remove-Item Env:MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue
                Remove-Item Env:MSSQL_DB_NAME -ErrorAction SilentlyContinue
            }
        }

        It "provision-dms-schema.ps1 host-side connection resolves POSTGRES_PORT with Compose precedence (ambient wins)" {
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

                # Compose publishes the host-side port from the ambient-resolved POSTGRES_PORT, so
                # the SchemaTools host-side translation must use the same value: with an ambient
                # override in place, the file value names a port nothing is listening on.
                $captured = @(Get-Content -LiteralPath $capturePath)
                $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
                $connectionString | Should -Match "port=1111"
                $connectionString | Should -Not -Match "port=9876"
            }
            finally {
                $env:POSTGRES_PORT = $null
            }
        }

        It "provision-dms-schema.ps1 host-side connection uses the env-file POSTGRES_PORT when no ambient override exists" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot

            $isolatedEnvFile = Join-Path $script:repo.DockerComposeRoot "env-port-file-only.env"
            @"
POSTGRES_PASSWORD=isolated-pass
POSTGRES_DB_NAME=isolated_db
POSTGRES_PORT=9876
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $isolatedEnvFile -Encoding utf8

            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args-file-only.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            Remove-Item Env:POSTGRES_PORT -ErrorAction SilentlyContinue

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

            $content | Should -Match 'Assert-RouteContextSchemaProvisioned -Database \$db'
            $content | Should -Match 'dms\."EffectiveSchema"'
            $content | Should -Match '"edfi"\."School"'
            $content | Should -Match '"edfi"\."Student"'
        }

        It "does not pass the removed connector skip flag to start-local-dms.ps1" {
            $e2eSetupScript = Join-Path $script:sourceRepoRoot "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"
            $content = Get-Content -LiteralPath $e2eSetupScript -Raw

            $content | Should -Match 'start-local-dms\.ps1'
            $content | Should -Not -Match 'SkipConnectorSetup'
        }
    }

    Context "wrapper revalidates the staged workspace against the effective SCHEMA_PACKAGES" {
        BeforeAll {
            function script:New-WrapperRevalidationFixture {
                <#
                .SYNOPSIS
                Isolated repo carrying only what Invoke-BootstrapWrapper needs to reach its schema/claims
                staging phase and then return early: the wrapper module + entry script, env-utility.psm1
                plus the DS 5.2/6.1 bootstrap overlays (composed unconditionally for start-local-dms.ps1),
                a base .env.example, and no-op stubs for prepare-dms-schema.ps1, prepare-dms-claims.ps1, and
                start-local-dms.ps1. configure-local-data-store.ps1 / provision-dms-schema.ps1 are
                deliberately absent so the wrapper takes its documented "isolated Pester fixture" early
                return right after the infrastructure phase (mirrors BootstrapSeedDelivery.Tests.ps1's
                "wrapper opt-in" fixtures).
                #>
                param(
                    [ValidateSet("bootstrap-local-dms.ps1", "bootstrap-published-dms.ps1")]
                    [string]$WrapperEntryScript = "bootstrap-local-dms.ps1"
                )

                $startScriptName = if ($WrapperEntryScript -eq "bootstrap-local-dms.ps1") {
                    "start-local-dms.ps1"
                }
                else {
                    "start-published-dms.ps1"
                }

                $repoRoot = script:New-TestDirectory
                $dockerComposeRoot = Join-Path $repoRoot "eng/docker-compose"
                New-Item -ItemType Directory -Path $dockerComposeRoot -Force | Out-Null

                foreach ($fileName in @(
                    "bootstrap-wrapper.psm1",
                    $WrapperEntryScript,
                    "bootstrap-schema-catalog.psm1",
                    "env-utility.psm1",
                    # env-utility.psm1 imports this at module load, so any isolated copy needs it too.
                    "database-safety.psm1",
                    ".env.bootstrap.ds52",
                    ".env.bootstrap.ds61"
                )) {
                    Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
                }
                Copy-Item `
                    -LiteralPath (Join-Path $script:sourceRepoRoot "eng/schema-package-utility.psm1") `
                    -Destination (Join-Path $repoRoot "eng/schema-package-utility.psm1")

                $envFile = Join-Path $dockerComposeRoot ".env.example"
                @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                # These stubs only record calls. Mismatch tests assert that neither schema
                # preparation nor infrastructure startup is reached.
                $prepareSchemaCallLog = Join-Path $repoRoot "prepare-schema-calls.txt"
                @"
param(
    [string] `$EnvironmentFile,
    [Parameter(ValueFromRemainingArguments = `$true)] `$Rest
)
Add-Content -LiteralPath '$prepareSchemaCallLog' -Value "EnvironmentFile=`$EnvironmentFile"
"@ | Set-Content -LiteralPath (Join-Path $dockerComposeRoot "prepare-dms-schema.ps1") -Encoding utf8

                "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)" |
                    Set-Content -LiteralPath (Join-Path $dockerComposeRoot "prepare-dms-claims.ps1") -Encoding utf8

                $startCallLog = Join-Path $repoRoot "start-calls.txt"
                @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$startCallLog' -Value "start"
"@ | Set-Content -LiteralPath (Join-Path $dockerComposeRoot $startScriptName) -Encoding utf8

                return [pscustomobject]@{
                    RepoRoot             = $repoRoot
                    DockerComposeRoot    = $dockerComposeRoot
                    EnvFile              = $envFile
                    WrapperScript        = Join-Path $dockerComposeRoot $WrapperEntryScript
                    PrepareSchemaCallLog = $prepareSchemaCallLog
                    StartCallLog         = $startCallLog
                }
            }

            function script:New-StandardModeManifestFile {
                <#
                .SYNOPSIS
                Writes a Standard-mode (package-backed) .bootstrap/bootstrap-manifest.json carrying the
                supplied schema.selectedExtensions (and, when supplied, schema.selectedPackages), plus
                complete claims/seed sections so Test-WrapperManifestClaimsStaged reports claims already
                staged - isolating the schema-package revalidation as the only variable under test.
                -Malformed writes unparsable JSON instead, exercising the fail-fast cleanup path.
                #>
                param(
                    [Parameter(Mandatory)]
                    [string]$DockerComposeRoot,

                    [string[]]$SelectedExtensions = @(),

                    # "<packageId>@<version>" identity strings; omitted from the manifest when not
                    # supplied, modeling a workspace staged before selectedPackages was recorded.
                    [string[]]$SelectedPackages = $null,

                    [switch]$Malformed
                )

                $bootstrapRoot = Join-Path $DockerComposeRoot ".bootstrap"
                New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
                $manifestPath = Join-Path $bootstrapRoot "bootstrap-manifest.json"

                if ($Malformed) {
                    "{ not valid json" | Set-Content -LiteralPath $manifestPath -Encoding utf8
                    return $manifestPath
                }

                $manifest = [ordered]@{
                    version = 1
                    schema  = [ordered]@{
                        selectionMode         = "Standard"
                        selectedExtensions    = @($SelectedExtensions)
                        effectiveSchemaHash   = "abc123"
                        workspaceFingerprint  = "0000000000000000000000000000000000000000000000000000000000000000"
                        apiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json"
                    }
                    claims  = [ordered]@{
                        mode                       = "Embedded"
                        directory                  = "claims"
                        fingerprint                = "def456"
                        expectedVerificationChecks = @()
                    }
                    seed    = [ordered]@{
                        extensionNamespacePrefixes = @()
                    }
                }
                if ($null -ne $SelectedPackages) {
                    $manifest["schema"].Insert(2, "selectedPackages", @($SelectedPackages))
                }
                $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
                return $manifestPath
            }
        }

        It "reuses the staged workspace when the recorded package identities match the effective SCHEMA_PACKAGES exactly" {
            $fixture = script:New-WrapperRevalidationFixture
            try {
                # Derive the expected "<packageId>@<version>" set from the same DS 5.2 overlay the
                # wrapper composes, so this spec keeps passing when the pinned versions bump.
                $overlayContent = Get-Content -LiteralPath (Join-Path $fixture.DockerComposeRoot ".env.bootstrap.ds52") -Raw
                $packagesJson = [regex]::Match($overlayContent, "(?ms)^[ \t]*SCHEMA_PACKAGES='(?<value>\[.*?\])'").Groups["value"].Value
                $overlayPackages = @(($packagesJson | ConvertFrom-Json) | ForEach-Object { "$($_.name)@$($_.version)" })
                $overlayPackages.Count | Should -BeGreaterThan 0

                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedExtensions @("tpdm") `
                    -SelectedPackages $overlayPackages | Out-Null

                & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog |
                    Should -BeFalse -Because "a manifest recording the exact effective package identities must be reused as-is"
                Test-Path -LiteralPath $fixture.StartCallLog |
                    Should -BeTrue -Because "the current workspace may proceed to infrastructure startup"
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "stops legacy Standard manifests without selectedPackages before preparation or Docker" {
            $fixture = script:New-WrapperRevalidationFixture
            try {
                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedExtensions @("tpdm") | Out-Null

                { & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile } |
                    Should -Throw "*Automatic replacement*DMS-1271*"

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "stops when the staged package identities no longer match the effective package set" {
            $fixture = script:New-WrapperRevalidationFixture
            try {
                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedExtensions @() `
                    -SelectedPackages @("EdFi.DataStandard61.ApiSchema@1.0.333") | Out-Null

                { & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile } |
                    Should -Throw "*does not match*DMS-1271*"

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "stops when package versions drift despite an identical extension set" {
            $fixture = script:New-WrapperRevalidationFixture
            try {
                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedExtensions @("tpdm") `
                    -SelectedPackages @(
                        "EdFi.DataStandard52.ApiSchema@0.0.1",
                        "EdFi.DataStandard52.TPDM.ApiSchema@0.0.1"
                    ) | Out-Null

                { & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile } |
                    Should -Throw "*does not match*DMS-1271*"

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "stops when the staged bootstrap manifest is malformed" {
            $fixture = script:New-WrapperRevalidationFixture
            try {
                script:New-StandardModeManifestFile -DockerComposeRoot $fixture.DockerComposeRoot -Malformed | Out-Null

                { & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile } |
                    Should -Throw "*without a complete selectedPackages identity*DMS-1271*"

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "fails closed when SCHEMA_PACKAGES is present but malformed" {
            $fixture = script:New-WrapperRevalidationFixture
            try {
                $overlayPath = Join-Path $fixture.DockerComposeRoot ".env.bootstrap.ds52"
                $overlayContent = Get-Content -LiteralPath $overlayPath -Raw
                $overlayContent = $overlayContent -replace '(?m)^SCHEMA_PACKAGES=.*$', "SCHEMA_PACKAGES=not-json"
                Set-Content -LiteralPath $overlayPath -Value $overlayContent -NoNewline

                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedPackages @("EdFi.DataStandard52.ApiSchema@1.0.333") | Out-Null

                { & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile } |
                    Should -Throw "*Unable to find quoted JSON env value for 'SCHEMA_PACKAGES'*"

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "uses the catalog-pinned core identity when SCHEMA_PACKAGES is absent" {
            $fixture = script:New-WrapperRevalidationFixture -WrapperEntryScript "bootstrap-published-dms.ps1"
            try {
                Import-Module (Join-Path $fixture.DockerComposeRoot "bootstrap-schema-catalog.psm1") -Force
                $corePackage = Get-StandardCorePackage
                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedPackages @("$($corePackage.Id)@$($corePackage.Version)") | Out-Null

                & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeTrue
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "rejects an obsolete core package when SCHEMA_PACKAGES is absent" {
            $fixture = script:New-WrapperRevalidationFixture -WrapperEntryScript "bootstrap-published-dms.ps1"
            try {
                Import-Module (Join-Path $fixture.DockerComposeRoot "bootstrap-schema-catalog.psm1") -Force
                $corePackage = Get-StandardCorePackage
                script:New-StandardModeManifestFile `
                    -DockerComposeRoot $fixture.DockerComposeRoot `
                    -SelectedPackages @("$($corePackage.Id)@0.0.1") | Out-Null

                { & $fixture.WrapperScript -EnvironmentFile $fixture.EnvFile } |
                    Should -Throw "*$($corePackage.Id)@$($corePackage.Version)*DMS-1271*"

                Test-Path -LiteralPath $fixture.PrepareSchemaCallLog | Should -BeFalse
                Test-Path -LiteralPath $fixture.StartCallLog | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $fixture.RepoRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "E2E dedicated-database guard (engine-aware)" {
        BeforeAll {
            # Dot-source stops at the script's dot-source guard, exposing the guard functions
            # without provisioning anything.
            . (Join-Path $script:sourceDockerComposeRoot "provision-e2e-database.ps1")
        }

        It "rejects a case-variant of the bootstrap database name" {
            # SQL Server's default collation treats identifiers case-insensitively, so a
            # case-variant of the protected name is the same physical database there.
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{ POSTGRES_DB_NAME = "edfi_datamanagementservice" } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "EDFI_DataManagementService"
            } | Should -Throw "*must be dedicated*POSTGRES_DB_NAME*"
        }

        It "protects MSSQL_DB_NAME as a first-class database-name key" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{ MSSQL_DB_NAME = "edfi_datamanagementservice" } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "Edfi_DataManagementService"
            } | Should -Throw "*MSSQL_DB_NAME*"
        }

        It "extracts the database name from an Initial Catalog connection-string segment" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        DATABASE_CONNECTION_STRING_ADMIN = "Server=dms-mssql,1433;Initial Catalog=edfi_datamanagementservice;User ID=sa;Password=p;"
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "edfi_datamanagementservice"
            } | Should -Throw "*DATABASE_CONNECTION_STRING_ADMIN*"
        }

        It "rejects a Compose-quoted connection string that targets the E2E database" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        DMS_CONFIG_DATABASE_CONNECTION_STRING = '"Server=dms-mssql,1433;Initial Catalog=shared"'
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "shared"
            } | Should -Throw "*DMS_CONFIG_DATABASE_CONNECTION_STRING*"
        }

        It "rejects a Compose-quoted database-name key that matches the E2E database" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{ MSSQL_DB_NAME = "'shared' # local database" } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "shared"
            } | Should -Throw "*MSSQL_DB_NAME*"
        }

        It "resolves a variable-referenced connection-string database before comparing" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        SHARED_DB                           = "shared"
                        DATABASE_CONNECTION_STRING_ADMIN = 'Server=dms-mssql,1433;Database=${SHARED_DB};User ID=sa;Password=p;'
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "shared"
            } | Should -Throw "*DATABASE_CONNECTION_STRING_ADMIN*"
        }

        It "fails closed when a protected database reference is undefined" {
            # An undefined reference resolves to empty the way Docker Compose does, leaving no database
            # name to compare; the guard refuses rather than proceed.
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        DATABASE_CONNECTION_STRING_ADMIN = 'Server=dms-mssql,1433;Database=${SHARED_DB};User ID=sa;Password=p;'
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "shared"
            } | Should -Throw "*could not determine a database name*DATABASE_CONNECTION_STRING_ADMIN*"
        }

        It "fails closed when protected database references are cyclic" {
            # A cyclic reference cannot be expanded; the resolver leaves the '$' marker, which the guard
            # treats as unresolved and refuses.
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        SHARED_DB                           = '${OTHER_DB}'
                        OTHER_DB                            = '${SHARED_DB}'
                        DATABASE_CONNECTION_STRING_ADMIN = 'Server=dms-mssql,1433;Database=${SHARED_DB};User ID=sa;Password=p;'
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "shared"
            } | Should -Throw "*unresolved or cyclic reference*"
        }

        It "fails closed when a configured protected connection string has no database name" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;User ID=sa;Password=p;"
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "shared"
            } | Should -Throw "*could not determine a database name*"
        }

        It "accepts a dedicated E2E database name" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        POSTGRES_DB_NAME                 = "edfi_datamanagementservice"
                        MSSQL_DB_NAME                    = "edfi_datamanagementservice"
                        DATABASE_CONNECTION_STRING_ADMIN = "Server=dms-mssql,1433;Initial Catalog=edfi_datamanagementservice;User ID=sa;Password=p;"
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "edfi_e2e"
            } | Should -Not -Throw
        }

        # FR8: Docker Compose gives a process/shell value precedence over the env file. The guard must
        # resolve protected values with the same precedence, or an ambient override that makes the live
        # shared database equal the reset target would pass while the guard evaluated a stale file value.
        It "fails closed when an ambient <Key> override makes the live database the reset target" -ForEach @(
            @{ Key = "MSSQL_DB_NAME" }
            @{ Key = "POSTGRES_DB_NAME" }
        ) {
            $priorExists = Test-Path "Env:$Key"
            $priorValue = [System.Environment]::GetEnvironmentVariable($Key)
            try {
                [System.Environment]::SetEnvironmentVariable($Key, "shared_e2e")
                {
                    Assert-E2EDatabaseIsDedicated `
                        -EnvironmentValues @{ $Key = "main_db" } `
                        -EnvironmentFilePath ".env.e2e" `
                        -E2EDatabaseName "shared_e2e"
                } | Should -Throw "*must be dedicated*$Key*"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable($Key, $priorValue) }
                else { Remove-Item "Env:$Key" -ErrorAction SilentlyContinue }
            }
        }

        It "fails closed when an ambient override of a referenced protected variable targets the reset database" {
            $priorExists = Test-Path "Env:DMS1284_SHARED_DBNAME"
            $priorValue = [System.Environment]::GetEnvironmentVariable("DMS1284_SHARED_DBNAME")
            try {
                [System.Environment]::SetEnvironmentVariable("DMS1284_SHARED_DBNAME", "shared_e2e")
                {
                    Assert-E2EDatabaseIsDedicated `
                        -EnvironmentValues @{
                            MSSQL_DB_NAME         = '${DMS1284_SHARED_DBNAME}'
                            DMS1284_SHARED_DBNAME = "main_db"
                        } `
                        -EnvironmentFilePath ".env.e2e" `
                        -E2EDatabaseName "shared_e2e"
                } | Should -Throw "*must be dedicated*MSSQL_DB_NAME*"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("DMS1284_SHARED_DBNAME", $priorValue) }
                else { Remove-Item Env:DMS1284_SHARED_DBNAME -ErrorAction SilentlyContinue }
            }
        }

        It "fails closed when an ambient override of a connection-string variable targets the reset database" {
            $priorExists = Test-Path "Env:DMS1284_CONN_DBNAME"
            $priorValue = [System.Environment]::GetEnvironmentVariable("DMS1284_CONN_DBNAME")
            try {
                [System.Environment]::SetEnvironmentVariable("DMS1284_CONN_DBNAME", "shared_e2e")
                {
                    Assert-E2EDatabaseIsDedicated `
                        -EnvironmentValues @{
                            DATABASE_CONNECTION_STRING_ADMIN = 'Server=dms-mssql,1433;Database=${DMS1284_CONN_DBNAME};User ID=sa;Password=p;'
                        } `
                        -EnvironmentFilePath ".env.e2e" `
                        -E2EDatabaseName "shared_e2e"
                } | Should -Throw "*must stay separate*DATABASE_CONNECTION_STRING_ADMIN*"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("DMS1284_CONN_DBNAME", $priorValue) }
                else { Remove-Item Env:DMS1284_CONN_DBNAME -ErrorAction SilentlyContinue }
            }
        }

        It "accepts a dedicated E2E database when an ambient protected override differs from the reset target" {
            # Ambient precedence must not create false positives: an ambient protected name that differs
            # from the E2E target is still dedicated and must not throw.
            $priorExists = Test-Path "Env:MSSQL_DB_NAME"
            $priorValue = [System.Environment]::GetEnvironmentVariable("MSSQL_DB_NAME")
            try {
                [System.Environment]::SetEnvironmentVariable("MSSQL_DB_NAME", "still_the_main_db")
                {
                    Assert-E2EDatabaseIsDedicated `
                        -EnvironmentValues @{ MSSQL_DB_NAME = "main_db" } `
                        -EnvironmentFilePath ".env.e2e" `
                        -E2EDatabaseName "shared_e2e"
                } | Should -Not -Throw
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("MSSQL_DB_NAME", $priorValue) }
                else { Remove-Item Env:MSSQL_DB_NAME -ErrorAction SilentlyContinue }
            }
        }

        # A protected key explicitly present with a blank value is NOT the same as an absent key:
        # Docker Compose's ${VAR:-default} substitution uses the default when the configured value is
        # blank, so the running container can be on the compose-file default database (e.g.
        # edfi_datamanagementservice) while a value-based check sees "" and skips the collision check
        # entirely. An explicitly present blank protected key therefore counts as configured and the
        # guard fails closed rather than proving the reset target dedicated against an empty value.
        It "fails closed when <Key> is explicitly present but blank" -ForEach @(
            @{ Key = "POSTGRES_DB_NAME" }
            @{ Key = "MSSQL_DB_NAME" }
        ) {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{ $Key = "" } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "edfi_datamanagementservice"
            } | Should -Throw "*could not resolve $Key*"
        }

        It "fails closed when the protected <Key> connection string is explicitly present but blank" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "DMS_CONFIG_DATABASE_CONNECTION_STRING" }
        ) {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{ $Key = "" } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "edfi_datamanagementservice"
            } | Should -Throw "*could not resolve $Key*"
        }

        It "permits a genuinely absent protected key" {
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{ UNRELATED_KEY = "value" } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "edfi_datamanagementservice_e2e"
            } | Should -Not -Throw
        }

        It "fails closed when a direct ambient <Key> override targets the reset database" -ForEach @(
            @{ Key = "DATABASE_CONNECTION_STRING_ADMIN" }
            @{ Key = "DMS_CONFIG_DATABASE_CONNECTION_STRING" }
        ) {
            $priorExists = Test-Path "Env:$Key"
            $priorValue = [System.Environment]::GetEnvironmentVariable($Key)
            try {
                [System.Environment]::SetEnvironmentVariable($Key, "Server=dms-mssql,1433;Database=shared_e2e;User ID=sa;Password=p;")
                {
                    Assert-E2EDatabaseIsDedicated `
                        -EnvironmentValues @{ $Key = "Server=dms-mssql,1433;Database=main_db;User ID=sa;Password=p;" } `
                        -EnvironmentFilePath ".env.e2e" `
                        -E2EDatabaseName "shared_e2e"
                } | Should -Throw "*must stay separate*$Key*"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable($Key, $priorValue) }
                else { Remove-Item "Env:$Key" -ErrorAction SilentlyContinue }
            }
        }

        It "rejects a connection string whose provider-synonym database keys include the reset target" {
            # SqlClient treats Database and Initial Catalog as synonyms where the LAST occurrence
            # wins, while the generic parser keeps both as distinct keys. A string carrying both can
            # therefore effectively target the second value, so every candidate must be compared -
            # returning only the first would let the effective database skip the collision check.
            {
                Assert-E2EDatabaseIsDedicated `
                    -EnvironmentValues @{
                        DATABASE_CONNECTION_STRING_ADMIN = "Server=dms-mssql,1433;Database=edfi_datamanagementservice;Initial Catalog=edfi_e2e;User ID=sa;Password=p;"
                    } `
                    -EnvironmentFilePath ".env.e2e" `
                    -E2EDatabaseName "edfi_e2e"
            } | Should -Throw "*must stay separate*DATABASE_CONNECTION_STRING_ADMIN*"
        }

        It "fails closed when an ambient override of a referenced POSTGRES_DB_NAME variable targets the reset database" {
            $priorExists = Test-Path "Env:DMS1284_SHARED_PG_DBNAME"
            $priorValue = [System.Environment]::GetEnvironmentVariable("DMS1284_SHARED_PG_DBNAME")
            try {
                [System.Environment]::SetEnvironmentVariable("DMS1284_SHARED_PG_DBNAME", "shared_e2e")
                {
                    Assert-E2EDatabaseIsDedicated `
                        -EnvironmentValues @{
                            POSTGRES_DB_NAME         = '${DMS1284_SHARED_PG_DBNAME}'
                            DMS1284_SHARED_PG_DBNAME = "main_db"
                        } `
                        -EnvironmentFilePath ".env.e2e" `
                        -E2EDatabaseName "shared_e2e"
                } | Should -Throw "*must be dedicated*POSTGRES_DB_NAME*"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("DMS1284_SHARED_PG_DBNAME", $priorValue) }
                else { Remove-Item Env:DMS1284_SHARED_PG_DBNAME -ErrorAction SilentlyContinue }
            }
        }
    }

    # DMS-1270 post-PR review: start-local-dms.ps1 -InfraOnly -SeparateConfigDatabase writes the
    # topology marker only into its own derived file, then prints terminal guidance directing the
    # developer to run these two standalone phases. Reusing the original caller-authored MSSQL env
    # file, both phases resolve the engine overlay with NO skip switch, so the legacy shared-only
    # invariant used to reject the dedicated CMS target the start phase had just established. These
    # drive the real entry points end to end (CMS calls stubbed, no network) rather than asserting on
    # source text, so the continuation is proven at the boundary the developer actually uses.
    Context "standalone MSSQL manual-phase continuation of a separate CMS database topology" {
        BeforeAll {
            function script:New-SeparateTopologyMssqlEnvFile {
                param(
                    [Parameter(Mandatory)]
                    [string]$CmsDatabaseName
                )

                $path = Join-Path $script:repo.DockerComposeRoot "env-separate-topology-$([Guid]::NewGuid().ToString('N')).env"
                @"
POSTGRES_PASSWORD=isolated-pass
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=$CmsDatabaseName;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;
"@ | Set-Content -LiteralPath $path -Encoding utf8
                return $path
            }
        }

        It "configure-local-data-store.ps1 resolves an export-declared dependency of the connection string" {
            # The names a connection string references are resolved through the sequential model of the
            # composed environment. Resolving them through a ReadValuesFromEnvFile map instead mis-keyed
            # `export CMS_DB=...`, so the database segment resolved empty and this phase rejected a
            # dedicated target the start phase had accepted.
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @([pscustomobject]@{ id = 97; name = "Existing"; dataStoreContexts = @() })
            }

            $envFile = Join-Path $script:repo.DockerComposeRoot "env-export-dependency-$([Guid]::NewGuid().ToString('N')).env"
            @"
POSTGRES_PASSWORD=isolated-pass
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
export CMS_DB_OVERRIDE_XYZ=edfi_configurationservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${CMS_DB_OVERRIDE_XYZ};User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $envFile -DatabaseEngine mssql -NoDataStore

            $result.SelectedDataStoreIds | Should -Be @([long]97) -Because "the phase must run to completion, not merely avoid the specific rejection"
        }

        It "provision-dms-schema.ps1 resolves an export-declared dependency of the connection string" {
            # Both manual phases reach the engine gate independently, so both are covered: a defect in
            # this resolution stops a run at whichever phase the developer reaches first.
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "export-dependency-schema-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 98
                            name = "ExportDependency"
                            connectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $envFile = Join-Path $script:repo.DockerComposeRoot "env-provision-export-dep-$([Guid]::NewGuid().ToString('N')).env"
                @"
POSTGRES_PASSWORD=isolated-pass
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
export CMS_DB_OVERRIDE_XYZ=edfi_configurationservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${CMS_DB_OVERRIDE_XYZ};User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

                { Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(98) -DatabaseEngine mssql } |
                    Should -Not -Throw

                @(Get-Content -LiteralPath $capturePath) | Should -Contain "mssql"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "configure-local-data-store.ps1 accepts an operator-shaped dedicated-database expression" {
            # The start phase accepted this shape and this phase then threw "unsupported environment
            # expression", because the two resolved the database segment with different grammars. The
            # ${A:-${B}} form is the one the checked-in .yml fallbacks themselves use.
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @([pscustomobject]@{ id = 95; name = "Existing"; dataStoreContexts = @() })
            }

            $envFile = New-SeparateTopologyMssqlEnvFile -CmsDatabaseName '${CMS_DB_OVERRIDE_XYZ:-edfi_configurationservice}'

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $envFile -DatabaseEngine mssql -NoDataStore

            $result.SelectedDataStoreIds | Should -Be @([long]95) -Because "the phase must run to completion, not merely avoid the specific rejection"
        }

        It "provision-dms-schema.ps1 accepts an operator-shaped dedicated-database expression" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "operator-shaped-schema-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 96
                            name = "OperatorShaped"
                            connectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $envFile = New-SeparateTopologyMssqlEnvFile -CmsDatabaseName '${CMS_DB_OVERRIDE_XYZ:-edfi_configurationservice}'

                { Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(96) -DatabaseEngine mssql } |
                    Should -Not -Throw

                @(Get-Content -LiteralPath $capturePath) | Should -Contain "mssql"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "configure-local-data-store.ps1 accepts the caller-authored file targeting the dedicated CMS database" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @([pscustomobject]@{ id = 91; name = "Existing"; dataStoreContexts = @() })
            }

            $envFile = New-SeparateTopologyMssqlEnvFile -CmsDatabaseName "edfi_configurationservice"

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $envFile -DatabaseEngine mssql -NoDataStore

            $result.SelectedDataStoreIds | Should -Be @([long]91) -Because "the phase must run to completion, not merely avoid the specific rejection"
        }

        It "configure-local-data-store.ps1 renders no name verdict for a CMS connection string naming some third database" {
            # The superseded offline shared-database invariant rejected this file here. It is
            # deleted with the rest of the offline MSSQL name verdicts: whether legacy_config and
            # the datastore are the same physical database is the running instance's collation's
            # call, and this manual phase owns the DMS datastore, never the CMS seam. The
            # physical protection lives in the start scripts' live authority, which every
            # CMS-consuming start runs.
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore {
                return @([pscustomobject]@{ id = 92; name = "Existing"; dataStoreContexts = @() })
            }

            $envFile = New-SeparateTopologyMssqlEnvFile -CmsDatabaseName "legacy_config"

            $result = Invoke-ConfigureLocalDataStore -EnvironmentFile $envFile -DatabaseEngine mssql -NoDataStore

            $result.SelectedDataStoreIds | Should -Be @([long]92) -Because "the phase must run to completion, not merely avoid the specific rejection"
        }

        It "provision-dms-schema.ps1 accepts the caller-authored file targeting the dedicated CMS database" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "separate-topology-schema-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 93
                            name = "SeparateTopology"
                            connectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $envFile = New-SeparateTopologyMssqlEnvFile -CmsDatabaseName "edfi_configurationservice"

                { Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(93) -DatabaseEngine mssql } |
                    Should -Not -Throw

                # The phase must have actually provisioned the DMS datastore, proving it ran past the
                # resolution rather than failing somewhere harmlessly earlier.
                @(Get-Content -LiteralPath $capturePath) | Should -Contain "mssql"
            }
            finally {
                Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue
            }
        }

        It "provision-dms-schema.ps1 renders no name verdict for a CMS connection string naming some third database" {
            # The symmetric half: both manual phases reach the engine gate independently, so both
            # lose the deleted offline invariant together. Provisioning must run to completion.
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "third-database-schema-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 94
                            name = "ThirdDatabase"
                            connectionString = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $envFile = New-SeparateTopologyMssqlEnvFile -CmsDatabaseName "legacy_config"

                { Invoke-ProvisionDmsSchema -EnvironmentFile $envFile -DataStoreId @(94) -DatabaseEngine mssql } |
                    Should -Not -Throw

                @(Get-Content -LiteralPath $capturePath) | Should -Contain "mssql"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }
    }

    Context "separate-topology datastore guard at the manual configure boundary" {
        # The configure phase registers the DMS datastore after the start phase's live check has
        # already run, so the `-InfraOnly -SeparateConfigDatabase` continuation is its own
        # boundary. These rows pin WHAT is judged, WHEN, and by WHOM. The MSSQL collation verdicts
        # themselves are not re-litigated here - they belong to the running instance and are
        # covered against real servers in MssqlPhysicalDistinctnessLive.Tests.ps1; what matters at
        # this boundary is that the same authority is consulted, on the same effective file, with
        # the same provider-parsed candidate, before CMS is touched.
        BeforeAll {
            function script:New-GuardMssqlEnvFile {
                param([string]$DatastoreName = "edfi_datamanagementservice")

                $path = Join-Path $script:repo.DockerComposeRoot "env-guard-mssql-$([Guid]::NewGuid().ToString('N')).env"
                @"
POSTGRES_PASSWORD=isolated-pass
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=$DatastoreName
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;
"@ | Set-Content -LiteralPath $path -Encoding utf8
                return $path
            }

            function script:New-GuardPostgresEnvFile {
                param([string]$DatastoreName = "edfi_datamanagementservice")

                $path = Join-Path $script:repo.DockerComposeRoot "env-guard-pg-$([Guid]::NewGuid().ToString('N')).env"
                @"
POSTGRES_PASSWORD=isolated-pass
POSTGRES_DB_NAME=$DatastoreName
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $path -Encoding utf8
                return $path
            }

            # One trace for both questions the ordering rows ask: which calls happened, and in
            # which order relative to the first CMS mutation.
            function script:Reset-GuardTrace {
                $script:guardTrace = [System.Collections.Generic.List[string]]::new()
                $script:guardAuthorityArgument = $null
                $script:guardRegisteredName = $null
            }
        }

        It "asks the live authority - on the derived topology environment, for the container the datastore uses - before any CMS mutation" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Assert-MssqlTopologyPhysicalConsistency {
                param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $TimeoutSeconds)
                $script:guardTrace.Add("authority")
                $script:guardAuthorityArgument = @{
                    EnvironmentFile = $EnvironmentFile
                    ContainerName = $ContainerName
                    SaPassword = $SaPassword
                    Registered = $RegisteredDatastoreDatabaseName
                }
            }
            function Add-CmsClient { $script:guardTrace.Add("Add-CmsClient") }
            function Get-CmsToken { return "token" }
            function Add-DataStore { $script:guardTrace.Add("Add-DataStore"); return 601 }

            $envFile = New-GuardMssqlEnvFile
            Invoke-ConfigureLocalDataStore `
                -EnvironmentFile $envFile `
                -DatabaseEngine mssql `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName "edfi_datamanagementservice" | Out-Null

            $script:guardTrace[0] | Should -Be "authority" -Because "a refusal must be able to leave CMS with no client, tenant, or data store"
            @($script:guardTrace) | Should -Be @("authority", "Add-CmsClient", "Add-DataStore")
            $script:guardAuthorityArgument.ContainerName | Should -Be "dms-mssql"
            $script:guardAuthorityArgument.SaPassword | Should -Be "abcdefgh1!" -Because "the same resolved credential the registered connection string carries"
            $script:guardAuthorityArgument.Registered | Should -Be "edfi_datamanagementservice"
            # The marker is what selects separate-mode semantics inside the authority, and it lives
            # only in the derived file this phase re-derived - never in the caller's own file.
            @(Get-Content -LiteralPath $script:guardAuthorityArgument.EnvironmentFile) |
                Should -Contain "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true"
            @(Get-Content -LiteralPath $envFile) |
                Should -Not -Contain "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true" -Because "the caller's source file is never edited"
        }

        It "hands the authority the value a provider receives, not the raw parameter text" {
            # A bare trailing line feed survives the parameter but not the connection-string
            # transport, so judging the text would ask about a name no provider will ever see.
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Assert-MssqlTopologyPhysicalConsistency {
                param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $TimeoutSeconds)
                $script:guardRegisteredName = $RegisteredDatastoreDatabaseName
            }
            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore { return 602 }

            Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardMssqlEnvFile) `
                -DatabaseEngine mssql `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName "edfi_configurationservice`n" | Out-Null

            $script:guardRegisteredName | Should -Be "edfi_configurationservice" -Because "the transport drops the trailing line feed before the provider sees the name"
        }

        It "registers nothing when the server reports the datastore is the reserved database" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Assert-MssqlTopologyPhysicalConsistency {
                param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $TimeoutSeconds)
                throw "CMS database topology mismatch: SQL Server reports that the datastore name resolved from '-DataStoreDatabaseName' denotes the SAME physical database as the dedicated 'edfi_configurationservice'."
            }
            function Add-CmsClient { $script:guardTrace.Add("Add-CmsClient") }
            function Get-CmsToken { return "token" }
            function Add-DataStore { $script:guardTrace.Add("Add-DataStore"); return 603 }

            { Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardMssqlEnvFile) `
                -DatabaseEngine mssql `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName "EDFI_ConfigurationService" } |
                Should -Throw "*SAME physical database*"

            @($script:guardTrace) | Should -BeNullOrEmpty -Because "the server's verdict governs, and it arrives before CMS is mutated"
        }

        It "leaves the environment's own datastore key to the authority when no override is supplied" {
            # With no override the registered name IS the effective MSSQL_DB_NAME, which the
            # authority resolves and reports under that key - so it is still checked, and the
            # diagnostic still names the input the operator actually supplied.
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Assert-MssqlTopologyPhysicalConsistency {
                param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $TimeoutSeconds)
                $script:guardTrace.Add("authority")
                $script:guardRegisteredName = $RegisteredDatastoreDatabaseName
            }
            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore { return 604 }

            Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardMssqlEnvFile) `
                -DatabaseEngine mssql `
                -SeparateConfigDatabase | Out-Null

            @($script:guardTrace) | Should -Be @("authority")
            $script:guardRegisteredName | Should -BeNullOrEmpty -Because "no parameter was supplied, so no parameter-sourced candidate is asserted"
        }

        It "asks the server nothing when -NoDataStore makes the name inert" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Assert-MssqlTopologyPhysicalConsistency {
                param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $TimeoutSeconds)
                $script:guardTrace.Add("authority")
            }
            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore { return @([pscustomobject]@{ id = 605; name = "Existing"; dataStoreContexts = @() }) }

            $result = Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardMssqlEnvFile) `
                -DatabaseEngine mssql `
                -SeparateConfigDatabase `
                -NoDataStore `
                -DataStoreDatabaseName "edfi_configurationservice"

            @($script:guardTrace) | Should -BeNullOrEmpty -Because "no name is registered, so there is nothing to verify"
            $result.SelectedDataStoreIds | Should -Be @([long]605)
        }

        It "asks the server nothing in shared mode, where the datastore and the Configuration Service share one database by design" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Assert-MssqlTopologyPhysicalConsistency {
                param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $TimeoutSeconds)
                $script:guardTrace.Add("authority")
            }
            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore {
                param($CmsUrl, $AccessToken, $PostgresCredential, $PostgresDbName, $ConnectionString, $Name, $DataStoreType, $Tenant)
                $script:guardRegisteredName = $ConnectionString
                return 606
            }

            Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardMssqlEnvFile) `
                -DatabaseEngine mssql `
                -DataStoreDatabaseName "edfi_configurationservice" | Out-Null

            @($script:guardTrace) | Should -BeNullOrEmpty
            $script:guardRegisteredName | Should -BeLike "*Database=edfi_configurationservice;*" -Because "shared mode's behavior is unchanged by this guard"
        }

        It "refuses the exact reserved name on PostgreSQL, naming the parameter and registering nothing" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Add-CmsClient { $script:guardTrace.Add("Add-CmsClient") }
            function Get-CmsToken { return "token" }
            function Add-DataStore { $script:guardTrace.Add("Add-DataStore"); return 607 }

            $thrown = { Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName "edfi_configurationservice" } |
                Should -Throw -PassThru

            $thrown.Exception.Message | Should -BeLike "*'-DataStoreDatabaseName'*"
            $thrown.Exception.Message | Should -BeLike "*edfi_configurationservice*" -Because "the fixed reserved literal is allowed in diagnostics"
            @($script:guardTrace) | Should -BeNullOrEmpty
        }

        It "refuses a PostgreSQL name the provider parses back as the reserved database" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore { return 608 }

            { Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName "edfi_configurationservice`n" } |
                Should -Throw "*must be provably distinct*"
        }

        It "accepts a PostgreSQL case variant, which is a genuinely distinct database there" {
            # The unquoted-CREATE folding that governs the initialization path must not be borrowed
            # here: SchemaTools creates the registered name with a quoted identifier.
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore {
                param($CmsUrl, $AccessToken, $PostgresCredential, $PostgresDbName, $ConnectionString, $Name, $DataStoreType, $Tenant)
                $script:guardRegisteredName = $PostgresDbName
                return 609
            }

            Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -SeparateConfigDatabase `
                -DataStoreDatabaseName "EDFI_ConfigurationService" | Out-Null

            $script:guardRegisteredName | Should -Be "EDFI_ConfigurationService"
        }

        It "names the environment key when the reserved name came from POSTGRES_DB_NAME rather than the parameter" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore { return 610 }

            $thrown = { Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile -DatastoreName "edfi_configurationservice") `
                -SeparateConfigDatabase } |
                Should -Throw -PassThru

            $thrown.Exception.Message | Should -BeLike "*'POSTGRES_DB_NAME'*"
            $thrown.Exception.Message | Should -Not -BeLike "*'-DataStoreDatabaseName'*" -Because "the diagnostic must point at the input the operator actually supplied"
        }

        It "leaves PostgreSQL shared mode unchanged" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DataStore {
                param($CmsUrl, $AccessToken, $PostgresCredential, $PostgresDbName, $ConnectionString, $Name, $DataStoreType, $Tenant)
                $script:guardRegisteredName = $PostgresDbName
                return 611
            }

            Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -DataStoreDatabaseName "edfi_configurationservice" | Out-Null

            $script:guardRegisteredName | Should -Be "edfi_configurationservice" -Because "shared mode never required the datastore to differ from the reserved name"
        }

        It "treats the PostgreSQL name as inert under -NoDataStore" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DataStore { return @([pscustomobject]@{ id = 612; name = "Existing"; dataStoreContexts = @() }) }

            $result = Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -SeparateConfigDatabase `
                -NoDataStore `
                -DataStoreDatabaseName "edfi_configurationservice"

            $result.SelectedDataStoreIds | Should -Be @([long]612)
        }

        It "refuses before any school-year data store is created" {
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Add-CmsClient { $script:guardTrace.Add("Add-CmsClient") }
            function Get-CmsToken { return "token" }
            function Add-DmsSchoolYearInstances { $script:guardTrace.Add("Add-DmsSchoolYearInstances"); return @() }

            { Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -SeparateConfigDatabase `
                -SchoolYearRange "2024-2025" `
                -DataStoreDatabaseName "edfi_configurationservice" } |
                Should -Throw "*must be provably distinct*"

            @($script:guardTrace) | Should -BeNullOrEmpty -Because "the name is not inert under -SchoolYearRange; it is what every year would register"
        }

        It "registers the validated name for every school year, unsuffixed" {
            # Why one candidate is enough: the per-year helper varies the data store's display name
            # and route context, never the database. A name that passes here is the name each year
            # gets, so there is no post-suffix value left unchecked.
            . $script:repo.ConfigureScript
            Reset-GuardTrace

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Add-DmsSchoolYearInstances {
                param($CmsUrl, $AccessToken, $StartYear, $EndYear, $PostgresCredential, $PostgresDbName, $ConnectionString, $Tenant)
                $script:guardRegisteredName = $PostgresDbName
                return @(
                    @{ DataStoreId = [long]613; Year = 2024 },
                    @{ DataStoreId = [long]614; Year = 2025 }
                )
            }

            $result = Invoke-ConfigureLocalDataStore `
                -EnvironmentFile (New-GuardPostgresEnvFile) `
                -SeparateConfigDatabase `
                -SchoolYearRange "2024-2025" `
                -DataStoreDatabaseName "edfi_datamanagementservice_sy"

            $script:guardRegisteredName | Should -Be "edfi_datamanagementservice_sy" -Because "every year registers the single validated name"
            $result.DataStoreIds | Should -Be @([long]613, [long]614)
        }
    }

    Context "separate-topology target guard at the provisioning boundary" {
        # The configure guard can only judge a name it is about to REGISTER. Under -NoDataStore it
        # registers none: it selects an existing data store whose STORED connection string is what
        # decides where the schema lands. These rows pin that boundary - the value judged is the
        # database the resolved/decrypted stored connection string yields, and a refusal reaches
        # SchemaTools with nothing.
        BeforeAll {
            function script:New-ProvisionGuardPostgresEnvFile {
                param([string]$DatastoreName = "edfi_datamanagementservice")

                $path = Join-Path $script:repo.DockerComposeRoot "env-provguard-pg-$([Guid]::NewGuid().ToString('N')).env"
                @"
POSTGRES_PASSWORD=isolated-pass
POSTGRES_DB_NAME=$DatastoreName
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $path -Encoding utf8
                return $path
            }

            function script:New-ProvisionGuardMssqlEnvFile {
                param([string]$DatastoreName = "edfi_datamanagementservice")

                $path = Join-Path $script:repo.DockerComposeRoot "env-provguard-mssql-$([Guid]::NewGuid().ToString('N')).env"
                @"
POSTGRES_PASSWORD=isolated-pass
MSSQL_PORT=15433
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=$DatastoreName
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;
"@ | Set-Content -LiteralPath $path -Encoding utf8
                return $path
            }

            # One staged workspace + one fake tool per row, with the capture path as the single
            # record of whether any DDL was attempted. Absence of the file is "SchemaTools was
            # never invoked" - stronger than counting lines in a file the tool created.
            function script:Initialize-ProvisionGuardWorkspace {
                New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot -Extensions @()
                $capturePath = Join-Path $script:repo.RepoRoot "provguard-schema-args-$([Guid]::NewGuid().ToString('N')).txt"
                $env:DMS_SCHEMA_TOOL_PATH = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
                return $capturePath
            }
        }

        It "refuses a reused PostgreSQL data store whose stored target is the reserved database, before SchemaTools" {
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 701
                            name = "Reused"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_configurationservice;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $thrown = { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardPostgresEnvFile) `
                    -DataStoreId @(701) `
                    -SeparateConfigDatabase } |
                    Should -Throw -PassThru

                $thrown.Exception.Message | Should -BeLike "*701*" -Because "the data store id is a permitted diagnostic"
                $thrown.Exception.Message | Should -BeLike "*'CMS_DATA_STORE_CONNECTION_STRING'*" -Because "the diagnostic must name where the target came from"
                $thrown.Exception.Message | Should -BeLike "*edfi_configurationservice*" -Because "the fixed reserved literal is allowed in diagnostics"
                $thrown.Exception.Message | Should -Not -BeLike "*isolated-pass*" -Because "the stored connection string and its credentials are never printed"
                Test-Path -LiteralPath $capturePath | Should -BeFalse -Because "a refused target must reach SchemaTools with nothing"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "refuses a reused MSSQL data store the running server reports as the same physical database, before SchemaTools" {
            # The MSSQL verdict belongs to the instance collation, so the row stands in for the
            # authority's answer rather than re-litigating it: what is pinned here is that the
            # authority is consulted, and that its refusal stops the phase before any DDL.
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                function Assert-MssqlTopologyPhysicalConsistency {
                    param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $RegisteredDatastoreDatabaseSourceKey, $TimeoutSeconds)
                    throw "CMS database topology mismatch: SQL Server reports that the datastore name resolved from '$RegisteredDatastoreDatabaseSourceKey' denotes the SAME physical database as the dedicated 'edfi_configurationservice'."
                }
                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 702
                            name = "Reused"
                            connectionString = 'Server=dms-mssql,1433;Database=EDFI_ConfigurationService;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $thrown = { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardMssqlEnvFile) `
                    -DataStoreId @(702) `
                    -DatabaseEngine mssql `
                    -SeparateConfigDatabase } |
                    Should -Throw -PassThru

                $thrown.Exception.Message | Should -BeLike "*SAME physical database*"
                $thrown.Exception.Message | Should -BeLike "*'CMS_DATA_STORE_CONNECTION_STRING'*" -Because "the candidate came from the stored connection string, not a parameter this phase never accepts"
                Test-Path -LiteralPath $capturePath | Should -BeFalse -Because "the server's verdict arrives before any DDL"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "asks the MSSQL authority about the stored connection string's database, on the derived topology environment" {
            # Not MSSQL_DB_NAME, which is the datastore key the authority resolves itself, and not a
            # caller-authored parameter this phase does not have. The marker that selects separate-mode
            # semantics lives only in the derived file this phase re-derived.
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                $script:provisionAuthorityArgument = $null
                function Assert-MssqlTopologyPhysicalConsistency {
                    param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $RegisteredDatastoreDatabaseSourceKey, $TimeoutSeconds)
                    $script:provisionAuthorityArgument = @{
                        EnvironmentFile = $EnvironmentFile
                        ContainerName = $ContainerName
                        SaPassword = $SaPassword
                        Registered = $RegisteredDatastoreDatabaseName
                        SourceKey = $RegisteredDatastoreDatabaseSourceKey
                    }
                }
                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 703
                            name = "Reused"
                            connectionString = 'Server=dms-mssql,1433;Database=some_other_datastore;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                $envFile = New-ProvisionGuardMssqlEnvFile -DatastoreName "edfi_datamanagementservice"
                Invoke-ProvisionDmsSchema `
                    -EnvironmentFile $envFile `
                    -DataStoreId @(703) `
                    -DatabaseEngine mssql `
                    -SeparateConfigDatabase *>&1 | Out-Null

                $script:provisionAuthorityArgument.Registered | Should -Be "some_other_datastore" -Because "the judged value is the stored connection string's database"
                $script:provisionAuthorityArgument.Registered | Should -Not -Be "edfi_datamanagementservice" -Because "MSSQL_DB_NAME is the authority's own datastore key, not the reused target"
                $script:provisionAuthorityArgument.SourceKey | Should -Be "CMS_DATA_STORE_CONNECTION_STRING"
                $script:provisionAuthorityArgument.ContainerName | Should -Be "dms-mssql"
                $script:provisionAuthorityArgument.SaPassword | Should -Be "abcdefgh1!"
                @(Get-Content -LiteralPath $script:provisionAuthorityArgument.EnvironmentFile) |
                    Should -Contain "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true"
                @(Get-Content -LiteralPath $envFile) |
                    Should -Not -Contain "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true" -Because "the caller's source file is never edited"
                @(Get-Content -LiteralPath $capturePath) |
                    Should -Contain "mssql" -Because "a verified target must run to completion, not merely avoid the refusal"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "refuses a reused data store whose ENCRYPTED stored connection string decrypts to the reserved database" {
            # CMS returns ciphertext as base64. A guard reading the raw connectionString would find no
            # database segment at all and let this through, so the encrypted case is the row that
            # rejects a raw-base64 implementation.
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                $encrypted = New-CmsEncryptedConnectionString `
                    -PlainText 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_configurationservice;'

                function Get-CmsToken { return "token" }
                $script:provisionEncryptedValue = $encrypted
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 704
                            name = "ReusedEncrypted"
                            connectionString = $script:provisionEncryptedValue
                            dataStoreContexts = @()
                        }
                    )
                }

                # Proves the fixture really is opaque ciphertext, not a readable connection string.
                $encrypted | Should -Not -BeLike "*edfi_configurationservice*"

                { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardPostgresEnvFile) `
                    -DataStoreId @(704) `
                    -SeparateConfigDatabase } |
                    Should -Throw "*edfi_configurationservice*"

                Test-Path -LiteralPath $capturePath | Should -BeFalse
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "provisions a reused data store whose stored target is distinct from the reserved database" {
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 705
                            name = "Reused"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_datamanagementservice;'
                            dataStoreContexts = @()
                        }
                    )
                }

                { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardPostgresEnvFile) `
                    -DataStoreId @(705) `
                    -SeparateConfigDatabase } |
                    Should -Not -Throw

                @(Get-Content -LiteralPath $capturePath) | Should -Contain "provision" -Because "a distinct target must still be provisioned"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "judges the stored target, not POSTGRES_DB_NAME: a reserved-looking datastore key does not refuse a distinct target" {
            # The discriminating direction. If the guard read the environment's own datastore key it
            # would refuse here, where the database SchemaTools receives is a different one.
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 706
                            name = "Reused"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_datamanagementservice;'
                            dataStoreContexts = @()
                        }
                    )
                }

                { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardPostgresEnvFile -DatastoreName "edfi_configurationservice") `
                    -DataStoreId @(706) `
                    -SeparateConfigDatabase } |
                    Should -Not -Throw

                @(Get-Content -LiteralPath $capturePath) | Should -Contain "--connection-string"
                @(Get-Content -LiteralPath $capturePath) |
                    Should -Contain "host=localhost;port=5544;username=postgres;password=isolated-pass;database=edfi_datamanagementservice" -Because "the provisioned target is the stored one"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "leaves shared mode unchanged for the very same stored target" {
            # Without the declaration the datastore and the Configuration Service share one database
            # by design, so this exact record still provisions - the guard adds nothing to shared mode.
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 707
                            name = "Reused"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_configurationservice;'
                            dataStoreContexts = @()
                        }
                    )
                }

                { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardPostgresEnvFile) `
                    -DataStoreId @(707) } |
                    Should -Not -Throw

                @(Get-Content -LiteralPath $capturePath) |
                    Should -Contain "host=localhost;port=5544;username=postgres;password=isolated-pass;database=edfi_configurationservice" -Because "shared mode's behavior is unchanged by this guard"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "asks the MSSQL server nothing in shared mode" {
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                $script:provisionAuthorityCalled = $false
                function Assert-MssqlTopologyPhysicalConsistency {
                    param($EnvironmentFile, $ContainerName, $SaPassword, $RegisteredDatastoreDatabaseName, $RegisteredDatastoreDatabaseSourceKey, $TimeoutSeconds)
                    $script:provisionAuthorityCalled = $true
                }
                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 708
                            name = "Reused"
                            connectionString = 'Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                            dataStoreContexts = @()
                        }
                    )
                }

                Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardMssqlEnvFile) `
                    -DataStoreId @(708) `
                    -DatabaseEngine mssql *>&1 | Out-Null

                $script:provisionAuthorityCalled | Should -BeFalse -Because "no round trip belongs to a topology that shares one database by design"
                @(Get-Content -LiteralPath $capturePath) | Should -Contain "mssql"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }

        It "refuses before provisioning ANY target when one of several selected targets is the reserved database" {
            # The guard runs over the whole target set before the schema tool is resolved, so an
            # earlier distinct target is not provisioned on the way to discovering a later bad one.
            $capturePath = Initialize-ProvisionGuardWorkspace
            try {
                . $script:repo.ProvisionScript

                function Get-CmsToken { return "token" }
                function Get-DataStore {
                    return @(
                        [pscustomobject]@{
                            id = 709
                            name = "Distinct"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_datamanagementservice;'
                            dataStoreContexts = @()
                        },
                        [pscustomobject]@{
                            id = 710
                            name = "Reserved"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=isolated-pass;database=edfi_configurationservice;'
                            dataStoreContexts = @()
                        }
                    )
                }

                { Invoke-ProvisionDmsSchema `
                    -EnvironmentFile (New-ProvisionGuardPostgresEnvFile) `
                    -DataStoreId @(709, 710) `
                    -SeparateConfigDatabase } |
                    Should -Throw "*710*"

                Test-Path -LiteralPath $capturePath | Should -BeFalse -Because "no target is provisioned when any of them is refused"
            }
            finally { Remove-Item Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue }
        }
    }
}

# Unload exactly the module instances staged under the workspaces THIS run created and
# recorded - the roots in $script:ownedWorkspaceRoot - and nothing else. Containment respects
# directory boundaries (exact root, or root plus a separator), so a lookalike sibling such as
# '<owned-root>-other' or a caller's own 'dms-1151-*' directory never matches. The staged
# instances are usually NESTED inside a staged wrapper module, so enumeration must be
# Get-Module -All. Cleanup failure is loud: owned residue fails this suite with the surviving
# paths named, because later suites that bind -ModuleName mocks would otherwise fail on state
# this file left behind.
AfterAll {
    $ownedRoots = @($script:ownedWorkspaceRoot | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    # Windows paths compare case-insensitively; elsewhere they do not.
    $pathComparison = if ($IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
    function Test-PathWithinOwnedRoot {
        param([string]$CandidatePath)
        if ([string]::IsNullOrEmpty($CandidatePath) -or -not [System.IO.Path]::IsPathRooted($CandidatePath)) { return $false }
        $canonical = [System.IO.Path]::GetFullPath($CandidatePath)
        foreach ($root in $ownedRoots) {
            if ([string]::Equals($canonical, $root, $pathComparison)) { return $true }
            if ($canonical.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, $pathComparison)) { return $true }
        }
        return $false
    }
    foreach ($module in @(Get-Module -All)) {
        if (Test-PathWithinOwnedRoot -CandidatePath ([string]$module.Path)) {
            $module | Remove-Module -Force -ErrorAction SilentlyContinue
        }
    }
    $ownedResidue = @(Get-Module -All | Where-Object { Test-PathWithinOwnedRoot -CandidatePath ([string]$_.Path) })
    if ($ownedResidue.Count -gt 0) {
        $residueList = @($ownedResidue | ForEach-Object { "'$($_.Path)'" }) -join ", "
        throw "BootstrapSchemaDeploymentSafety.Tests.ps1 cleanup: module instances staged under this run's own recorded workspaces survived removal ($residueList). Refusing to hand the session back dirty."
    }
}

Describe "whole-file module-table ownership (post-Invoke-Pester, isolated children)" {
    # Both halves of the exact-ownership invariant, proven AFTER Invoke-Pester returns: owned
    # staged instances are gone, and a caller-owned module beneath a LOOKALIKE-named directory
    # survives untouched. The children exclude this tag, so there is no recursion; launches go
    # through [Environment]::ProcessPath, never a literal executable name.

    BeforeAll {
        $script:ownershipChildWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-1151-ownership-child-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:ownershipChildWork -Force | Out-Null
    }

    AfterAll {
        Remove-Item -LiteralPath $script:ownershipChildWork -Recurse -Force -ErrorAction SilentlyContinue
    }

    It "a caller-owned module beneath a lookalike dms-1151-* directory survives the complete file lifecycle" -Tag "WholeFileModuleOwnership" {
        # The review-measured regression this pins: a cleanup that inferred ownership from the
        # directory-name prefix deleted exactly this module while the suite stayed green.
        $childScript = Join-Path $script:ownershipChildWork "lookalike-survival.ps1"
        @(
            "`$callerRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('dms-1151-callerowned-' + [Guid]::NewGuid().ToString('N'))",
            "New-Item -ItemType Directory -Path `$callerRoot -Force | Out-Null",
            "`$callerModulePath = Join-Path `$callerRoot 'CallerOwned.psm1'",
            "Set-Content -LiteralPath `$callerModulePath -Value 'function Get-CallerOwnedSentinel { ''caller-owned'' }'",
            "Import-Module `$callerModulePath -Force",
            "try {",
            "    `$result = Invoke-Pester -Path '$PSCommandPath' -TagFilter 'ModuleOwnershipProbe' -ExcludeTagFilter 'WholeFileModuleOwnership' -Output None -PassThru",
            "    `$survivor = @(Get-Module -Name CallerOwned -All)",
            "    `$command = Get-Command Get-CallerOwnedSentinel -ErrorAction SilentlyContinue",
            "    [pscustomobject]@{",
            "        Failed = `$result.FailedCount",
            "        PassedCount = `$result.PassedCount",
            "        PassedName = @(`$result.Passed | ForEach-Object { `$_.ExpandedPath }) -join ';'",
            "        SurvivorCount = `$survivor.Count",
            "        SurvivorPath = @(`$survivor | ForEach-Object { `$_.Path }) -join ';'",
            "        ExpectedPath = `$callerModulePath",
            "        ExportsSentinel = (`$survivor.Count -eq 1 -and `$survivor[0].ExportedCommands.ContainsKey('Get-CallerOwnedSentinel'))",
            "        SentinelOutput = if (`$command) { Get-CallerOwnedSentinel } else { 'command-gone' }",
            "    } | ConvertTo-Json -Compress",
            "}",
            "finally { Remove-Item -LiteralPath `$callerRoot -Recurse -Force -ErrorAction SilentlyContinue }"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ([Environment]::ProcessPath) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        # Execution proof first: the probe must have RUN and PASSED - discovery counts prove
        # nothing, and a probe that never reached its staged import would make survival vacuous.
        $childState.Failed | Should -Be 0 -Because "the staged-import probe must complete cleanly around the caller's module"
        $childState.PassedCount | Should -Be 1 -Because "exactly the one tagged staged-import probe runs in the child"
        $childState.PassedName | Should -BeLike "*Resolve-LocalSettingsEnvironmentFile throws on missing file*" -Because "the passing test must be the staged-import probe itself"
        $childState.SurvivorCount | Should -Be 1 -Because "a lookalike directory name establishes no ownership; the caller's module is not this file's to remove"
        $childState.SurvivorPath | Should -Be $childState.ExpectedPath -Because "the surviving instance must be the caller's own, at its own path"
        $childState.ExportsSentinel | Should -BeTrue
        $childState.SentinelOutput | Should -Be 'caller-owned'
    }

    It "removes every module instance beneath the exact roots this run created" -Tag "WholeFileModuleOwnership" {
        # The other half: after the run, no NEW module instance rooted under the temp root -
        # where every workspace this run creates lives - may survive. New instances elsewhere
        # (repository modules, engine modules the run loads lazily) are legitimate. The temp
        # filter is a DETECTION oracle in a controlled clean child, never an ownership rule,
        # and the before/after set difference makes this fail even if the in-file cleanup
        # postcondition is deleted outright.
        $childScript = Join-Path $script:ownershipChildWork "own-removal.ps1"
        @(
            "`$before = @{}",
            "foreach (`$m in @(Get-Module -All)) { if (`$m.Path) { `$before[[string]`$m.Path] = `$true } }",
            "`$result = Invoke-Pester -Path '$PSCommandPath' -TagFilter 'ModuleOwnershipProbe' -ExcludeTagFilter 'WholeFileModuleOwnership' -Output None -PassThru",
            "`$comparison = if (`$IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }",
            "`$tempRoot = [System.IO.Path]::GetTempPath()",
            "`$newResidue = @(Get-Module -All | Where-Object {",
            "    `$p = [string]`$_.Path",
            "    `$p -and [System.IO.Path]::IsPathRooted(`$p) -and -not `$before.ContainsKey(`$p) -and",
            "    ([System.IO.Path]::GetFullPath(`$p)).StartsWith(`$tempRoot, `$comparison)",
            "})",
            "[pscustomobject]@{",
            "    Failed = `$result.FailedCount",
            "    PassedCount = `$result.PassedCount",
            "    PassedName = @(`$result.Passed | ForEach-Object { `$_.ExpandedPath }) -join ';'",
            "    ResidueCount = `$newResidue.Count",
            "    ResiduePath = @(`$newResidue | ForEach-Object { `$_.Path }) -join ';'",
            "} | ConvertTo-Json -Compress"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ([Environment]::ProcessPath) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        # Execution proof first: the residue check is meaningful only if the staged-import probe
        # really ran and passed - a run that never imported a staged module has nothing to clean.
        $childState.Failed | Should -Be 0 -Because "the staged-import probe must complete cleanly"
        $childState.PassedCount | Should -Be 1 -Because "exactly the one tagged staged-import probe runs in the child"
        $childState.PassedName | Should -BeLike "*Resolve-LocalSettingsEnvironmentFile throws on missing file*" -Because "the passing test must be the staged-import probe itself"
        $childState.ResidueCount | Should -Be 0 -Because "every instance staged under this run's recorded workspaces must be unloaded before the file hands the session back (residue: $($childState.ResiduePath))"
    }
}
