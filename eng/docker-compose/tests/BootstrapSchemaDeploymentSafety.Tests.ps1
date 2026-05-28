# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
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
                "configure-local-dms-instance.ps1",
                "provision-dms-schema.ps1",
                "bootstrap-wrapper.psm1",
                "bootstrap-local-dms.ps1"
            )) {
                Copy-DockerComposeFile -FileName $fileName -Destination $dockerComposeRoot
            }

            Copy-Item -LiteralPath (Join-Path $script:sourceRepoRoot "eng/Dms-Management.psm1") -Destination $engRoot

            $envFile = Join-Path $dockerComposeRoot ".env.example"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
NEED_DATABASE_SETUP=true
DMS_DEPLOY_DATABASE_ON_STARTUP=true
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            return [pscustomobject]@{
                RepoRoot = $repoRoot
                DockerComposeRoot = $dockerComposeRoot
                BootstrapRoot = Join-Path $dockerComposeRoot ".bootstrap"
                EnvFile = $envFile
                ConfigureScript = Join-Path $dockerComposeRoot "configure-local-dms-instance.ps1"
                ProvisionScript = Join-Path $dockerComposeRoot "provision-dms-schema.ps1"
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

            $rootManifest = [ordered]@{
                version = 1
                schema = [ordered]@{
                    selectionMode = "ApiSchemaPath"
                    selectedExtensions = @("sample")
                    effectiveSchemaHash = "abc123"
                    workspaceFingerprint = "def456"
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
            Get-Module Dms-Management |
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
        It "provision-dms-schema.ps1 exposes only the selector and env parameters" {
            $params = Get-DeclaredScriptParameters -Path $script:repo.ProvisionScript

            $params | Should -Contain "EnvironmentFile"
            $params | Should -Contain "InstanceId"
            $params | Should -Contain "SchoolYear"
            $params | Should -Not -Contain "SchemaToolPath"
            $params | Should -Not -Contain "SeedTemplate"
            $params | Should -Not -Contain "LoadSeedData"
            $params | Should -Not -Contain "ApiSchemaPath"
            $params.Count | Should -Be 3
        }

        It "wrapper entry script exposes configure flags without exposing InstanceId" {
            $params = Get-DeclaredScriptParameters -Path $script:repo.WrapperScript

            $params | Should -Contain "NoDmsInstance"
            $params | Should -Contain "AddSmokeTestCredentials"
            $params | Should -Contain "SchoolYearRange"
            $params | Should -Contain "LoadSeedData"
            $params | Should -Not -Contain "InstanceId"
        }

        It "start scripts expose InfraOnly and DmsOnly phase switches" {
            foreach ($name in @("start-local-dms.ps1", "start-published-dms.ps1")) {
                $params = Get-DeclaredScriptParameters -Path (Join-Path $script:sourceDockerComposeRoot $name)

                $params | Should -Contain "InfraOnly"
                $params | Should -Contain "DmsOnly"
                $params | Should -Contain "LoadSeedData"
                $params | Should -Not -Contain "ApiSchemaPath"
                $params | Should -Not -Contain "ClaimsDirectoryPath"
                $params | Should -Not -Contain "Extensions"
            }
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
            $workspace.WorkspaceFingerprint | Should -Be "def456"
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

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) -SchoolYear @(2024) } |
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=tenant_db;'
                        dmsInstanceRouteContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        instanceName = "B"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=tenant_db;'
                        dmsInstanceRouteContexts = @(
                            [pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" }
                        )
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1, 2)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 3
                        instanceName = "Encrypted"
                        connectionString = $encryptedConnectionString
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(3)

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
            function Get-DmsInstances {
                return @(
                    1..500 | ForEach-Object {
                        [pscustomobject]@{
                            id = $_
                            instanceName = "I$_"
                            connectionString = "host=dms-postgresql;port=5432;username=postgres;password=x;database=db$_;"
                            dmsInstanceRouteContexts = @()
                        }
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) } |
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
            function Get-DmsInstances {
                $target = [pscustomobject]@{
                    id = 1
                    instanceName = "Target"
                    connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=below_limit;'
                    dmsInstanceRouteContexts = @()
                }
                $filler = 2..499 | ForEach-Object {
                    [pscustomobject]@{
                        id = $_
                        instanceName = "I$_"
                        connectionString = "host=dms-postgresql;port=5432;username=postgres;password=x;database=db$_;"
                        dmsInstanceRouteContexts = @()
                    }
                }
                return @($target) + @($filler)
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) } |
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 10
                        instanceName = "SY2024-A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=sy2024a;'
                        dmsInstanceRouteContexts = @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" })
                    },
                    [pscustomobject]@{
                        id = 11
                        instanceName = "SY2024-B"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=sy2024b;'
                        dmsInstanceRouteContexts = @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" })
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -SchoolYear @(2024) } |
                Should -Throw -ExpectedMessage "*Multiple DMS instances found with route context schoolYear=2024*"
        }

        It "fails on zero instances or ambiguous auto-selection before invoking SchemaTools" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances { return @() }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile } |
                Should -Throw -ExpectedMessage "*No DMS instances found*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse

            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=a;'
                        dmsInstanceRouteContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        instanceName = "B"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=b;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile } |
                Should -Throw -ExpectedMessage "*Multiple DMS instances exist*"
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 5
                        instanceName = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=log_guard;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $output = & {
                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(5)
            } *>&1 | Out-String

            $output | Should -Not -Match "secret-pass"
            $output | Should -Not -Match "ValidClientSecret1234567890"
            $output | Should -Not -Match "dms-postgresql"
            $output | Should -Not -Match "password="
        }
    }

    Context "instance configuration" {
        It "returns a structured object for NoDmsInstance route-unqualified selection" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 77
                        instanceName = "Existing"
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDmsInstance -EnvironmentFile $script:repo.EnvFile -NoDmsInstance

            $result.InstanceIds | Should -Be @(77)
            $result.HasRouteQualifiedInstances | Should -BeFalse
            $result.RouteContexts.Count | Should -Be 0
        }

        It "rejects NoDmsInstance when the sole existing instance is route-qualified" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 77
                        instanceName = "Existing"
                        dmsInstanceRouteContexts = @([pscustomobject]@{ contextKey = "schoolYear"; contextValue = "2024" })
                    }
                )
            }

            { Invoke-ConfigureLocalDmsInstance -EnvironmentFile $script:repo.EnvFile -NoDmsInstance } |
                Should -Throw -ExpectedMessage "*route-qualified*"
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
param([string] `$EnvironmentFile, [string] `$SchoolYearRange, [switch] `$NoDmsInstance, [switch] `$AddSmokeTestCredentials)
Add-Content -LiteralPath '$sequencePath' -Value `"configure range=`$SchoolYearRange noDms=`$NoDmsInstance smoke=`$AddSmokeTestCredentials`"
[pscustomobject]@{
    InstanceIds = [long[]] @(101, 102)
    RouteContexts = @(
        [pscustomobject]@{ InstanceId = [long]101; ContextKey = 'schoolYear'; ContextValue = '2024' },
        [pscustomobject]@{ InstanceId = [long]102; ContextKey = 'schoolYear'; ContextValue = '2025' }
    )
    Tenant = ''
    SchoolYears = [int[]] @(2024, 2025)
    HasRouteQualifiedInstances = `$true
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-dms-instance.ps1") -Encoding utf8

            @"
param([string] `$EnvironmentFile, [long[]] `$InstanceId)
Add-Content -LiteralPath '$sequencePath' -Value `"provision ids=`$(`$InstanceId -join ',')`"
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            @"
param([string] `$EnvironmentFile, [int[]] `$SchoolYear, [long[]] `$InstanceId, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value `"seed years=`$(`$SchoolYear -join ',') ids=`$(`$InstanceId -join ',')`"
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "load-dms-seed-data.ps1") -Encoding utf8

            & $script:repo.WrapperScript `
                -EnvironmentFile $script:repo.EnvFile `
                -LoadSeedData `
                -SeedDataPath $script:repo.DockerComposeRoot `
                -SchoolYearRange "2024-2025" `
                -AddSmokeTestCredentials

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence[0] | Should -Be "start-infra EnableConfig=True"
            $sequence[1] | Should -Be "configure range=2024-2025 noDms=False smoke=True"
            $sequence[2] | Should -Be "provision ids=101,102"
            $sequence[3] | Should -Be "start-dms"
            $sequence[4] | Should -Be "seed years=2024,2025 ids="
        }

        It "passes route-unqualified configured instance to seed by InstanceId" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "sequence.txt"

            "param([switch] `$InfraOnly, [switch] `$DmsOnly, [switch] `$EnableConfig, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); if (`$InfraOnly) { Add-Content -LiteralPath '$sequencePath' -Value 'start-infra' } else { Add-Content -LiteralPath '$sequencePath' -Value 'start-dms' }" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
Add-Content -LiteralPath '$sequencePath' -Value 'configure'
[pscustomobject]@{
    InstanceIds = [long[]] @(42)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedInstances = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-dms-instance.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            "param([long[]] `$InstanceId, [int[]] `$SchoolYear, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value (`"seed ids=`$(`$InstanceId -join ',') years=`$(`$SchoolYear -join ',')`")" |
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
    InstanceIds = [long[]] @(42)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedInstances = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-dms-instance.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'provision'; throw 'provision failed'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest); Add-Content -LiteralPath '$sequencePath' -Value 'seed'" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "load-dms-seed-data.ps1") -Encoding utf8

            { & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile -LoadSeedData -SeedDataPath $script:repo.DockerComposeRoot } |
                Should -Throw -ExpectedMessage "*provision failed*"

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence | Should -Be @("start-infra", "configure", "provision")
        }

        It "passes a derived env with DMS startup provisioning disabled into DMS-only startup" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $sequencePath = Join-Path $script:repo.RepoRoot "sequence.txt"

            @"
param([switch] `$InfraOnly, [switch] `$DmsOnly, [string] `$EnvironmentFile, [Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
if (`$InfraOnly) {
    Add-Content -LiteralPath '$sequencePath' -Value 'start-infra'
}
elseif (`$DmsOnly) {
    `$values = @{}
    Get-Content -LiteralPath `$EnvironmentFile | ForEach-Object {
        `$parts = `$_.Split('=', 2)
        if (`$parts.Length -eq 2) { `$values[`$parts[0]] = `$parts[1] }
    }
    Add-Content -LiteralPath '$sequencePath' -Value `"start-dms need=`$(`$values['NEED_DATABASE_SETUP']) deploy=`$(`$values['DMS_DEPLOY_DATABASE_ON_STARTUP']) app=`$(`$values['AppSettings__DeployDatabaseOnStartup'])`"
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "start-local-dms.ps1") -Encoding utf8

            @"
param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)
[pscustomobject]@{
    InstanceIds = [long[]] @(42)
    RouteContexts = @()
    Tenant = ''
    SchoolYears = [int[]] @()
    HasRouteQualifiedInstances = `$false
}
"@ | Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "configure-local-dms-instance.ps1") -Encoding utf8

            "param([Parameter(ValueFromRemainingArguments = `$true)] `$Rest)" |
                Set-Content -LiteralPath (Join-Path $script:repo.DockerComposeRoot "provision-dms-schema.ps1") -Encoding utf8

            & $script:repo.WrapperScript -EnvironmentFile $script:repo.EnvFile

            $sequence = @(Get-Content -LiteralPath $sequencePath)
            $sequence[-1] | Should -Be "start-dms need=false deploy=false app=false"
        }
    }

    Context "legacy startup provisioning lockdown" {
        It "local-dms.yml defaults NEED_DATABASE_SETUP to false" {
            $localDmsYaml = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "local-dms.yml") -Raw
            $localDmsYaml | Should -Match 'NEED_DATABASE_SETUP:\s*\$\{NEED_DATABASE_SETUP:-false\}'
            $localDmsYaml | Should -Not -Match 'NEED_DATABASE_SETUP:\s*\$\{NEED_DATABASE_SETUP:-true\}'
        }

        It "published-dms.yml defaults NEED_DATABASE_SETUP to false" {
            $publishedDmsYaml = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "published-dms.yml") -Raw
            $publishedDmsYaml | Should -Match 'NEED_DATABASE_SETUP:\s*\$\{NEED_DATABASE_SETUP:-false\}'
            $publishedDmsYaml | Should -Not -Match 'NEED_DATABASE_SETUP:\s*\$\{NEED_DATABASE_SETUP:-true\}'
        }

        It ".env.example sets NEED_DATABASE_SETUP=false" {
            $envExample = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot ".env.example") -Raw
            $envExample | Should -Match '(?m)^NEED_DATABASE_SETUP=false\s*$'
            $envExample | Should -Not -Match '(?m)^NEED_DATABASE_SETUP=true\s*$'
        }

        It "start-local-dms.ps1 forces NEED_DATABASE_SETUP=false in the normal up path" {
            $startScript = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1") -Raw

            # The normal `docker compose ... up` (not -DmsOnly) must wrap the call in a try/finally
            # that forces NEED_DATABASE_SETUP=false. The -DmsOnly branch already did this; the
            # remediation extends the guard to the main up path.
            $envForceMatches = [regex]::Matches($startScript, '\$env:NEED_DATABASE_SETUP\s*=\s*"false"')
            $envForceMatches.Count | Should -BeGreaterOrEqual 2
        }

        It "start-published-dms.ps1 forces NEED_DATABASE_SETUP=false in the normal up path" {
            $startScript = Get-Content -LiteralPath (Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1") -Raw

            $envForceMatches = [regex]::Matches($startScript, '\$env:NEED_DATABASE_SETUP\s*=\s*"false"')
            $envForceMatches.Count | Should -BeGreaterOrEqual 2
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "A"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=shared_name;'
                        dmsInstanceRouteContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        instanceName = "B"
                        connectionString = 'host=other-postgresql;port=5432;username=postgres;password=secret-pass;database=shared_name;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1, 2)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "A"
                        connectionString = 'host=localhost;port=15432;username=postgres;password=secret-pass;database=shared_name;'
                        dmsInstanceRouteContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        instanceName = "B"
                        connectionString = 'host=localhost;port=15433;username=postgres;password=secret-pass;database=shared_name;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1, 2)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "RoleA"
                        connectionString = 'host=dms-postgresql;port=5432;username=app_role_a;password=secret-pass;database=shared_db;'
                        dmsInstanceRouteContexts = @()
                    },
                    [pscustomobject]@{
                        id = 2
                        instanceName = "RoleB"
                        connectionString = 'host=dms-postgresql;port=5432;username=app_role_b;password=secret-pass;database=shared_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1, 2)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Tenant-A"
                        connectionString = 'host=dms-postgresql;port=5432;username=tenant_a_user;password=tenant_a_secret;database=tenant_a_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "External"
                        connectionString = 'host=managed-pg.example.com;port=5439;username=ops_user;password=ops_pass;database=ext_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

            $captured = @(Get-Content -LiteralPath $capturePath)
            $connectionString = $captured[[array]::IndexOf($captured, "--connection-string") + 1]
            $connectionString | Should -Match "host=managed-pg.example.com"
            $connectionString | Should -Match "port=5439"
            $connectionString | Should -Match "username=ops_user"
            $connectionString | Should -Match "database=ext_db"
            $connectionString | Should -Not -Match "host=localhost"
        }

        It "rejects MSSQL-style connection strings with an actionable error" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "MsSql"
                        connectionString = 'Server=mssql-host;Initial Catalog=db1;User Id=sa;Password=foo;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) } |
                Should -Throw -ExpectedMessage "*Only PostgreSQL provisioning is supported*"
            Test-Path -LiteralPath $capturePath | Should -BeFalse
        }
    }

    Context "configure result contract" {
        It "returns SelectedInstanceIds plus a backward-compatible InstanceIds alias" {
            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 99
                        instanceName = "Sole"
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDmsInstance -EnvironmentFile $script:repo.EnvFile -NoDmsInstance

            $result.PSObject.Properties.Name | Should -Contain "SelectedInstanceIds"
            $result.SelectedInstanceIds | Should -Be @([long]99)
            $result.InstanceIds | Should -Be @([long]99)
        }

        It "includes CMSReadOnlyAccess block when env supplies the client id" {
            $envFile = Join-Path $script:repo.DockerComposeRoot "env-with-ro.env"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
CONFIG_SERVICE_CLIENT_ID=CMSReadOnlyAccess
CONFIG_SERVICE_CLIENT_SCOPE=edfi_admin_api/readonly_access
CONFIG_SERVICE_CLIENT_SECRET=my-ro-secret
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=TestEncryptionKey123456789012345678901234567890
"@ | Set-Content -LiteralPath $envFile -Encoding utf8

            . $script:repo.ConfigureScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Sole"
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDmsInstance -EnvironmentFile $envFile -NoDmsInstance

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Single"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=summary_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $output = & {
                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)
            } *>&1 | Out-String

            $output | Should -Match "Schema Provisioning Summary"
            $output | Should -Match "database=summary_db"
            $output | Should -Match "host=localhost"
            $output | Should -Match "instance-ids=\[1\]"
            $output | Should -Match "status=Provisioned"
        }

        It "emits IDE next-step guidance labeled with the Story 04 dependency" {
            New-StagedSchemaWorkspace -DockerComposeRoot $script:repo.DockerComposeRoot
            $capturePath = Join-Path $script:repo.RepoRoot "schema-tool-args.txt"
            $fakeTool = New-FakeSchemaTool -Directory $script:repo.RepoRoot -CapturePath $capturePath
            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool

            . $script:repo.ProvisionScript

            function Add-CmsClient { }
            function Get-CmsToken { return "token" }
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 7
                        instanceName = "Single"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=guidance_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $output = & {
                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(7)
            } *>&1 | Out-String

            $output | Should -Match "IDE next-step guidance"
            $output | Should -Match "AppSettings__UseApiSchemaPath = true"
            $output | Should -Match "AppSettings__ApiSchemaPath"
            $output | Should -Match "Story 04"
            $output | Should -Match "deferred"
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
                    InstanceIds = [long[]]@(1, 2)
                    Status = "Provisioned"
                }
            )

            $lines = Get-ProvisionIdeGuidance -SchemaWorkspace $schemaWorkspace -ProvisionedTargets $targets

            ($lines -join "`n") | Should -Match "Provisioned 1 database target"
            ($lines -join "`n") | Should -Match "database=td host=h port=5432 user=u"
            ($lines -join "`n") | Should -Match "Story 04"
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

        It "Get-EnvValue returns the supplied default when the key is absent or blank" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $envValues = @{ A = "alpha"; B = "" }
            Get-EnvValue -EnvValues $envValues -Name "A" -DefaultValue "fallback" | Should -Be "alpha"
            Get-EnvValue -EnvValues $envValues -Name "B" -DefaultValue "fallback" | Should -Be "fallback"
            Get-EnvValue -EnvValues $envValues -Name "Z" -DefaultValue "fallback" | Should -Be "fallback"
        }
    }

    Context "EnvironmentFile precedence" {
        It "configure-local-dms-instance.ps1 reads only the supplied -EnvironmentFile, not ambient process env" {
            $isolatedEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-tenant.env"
            @"
POSTGRES_PASSWORD=isolated-pass
POSTGRES_DB_NAME=isolated_db
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
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
                function Get-DmsInstances {
                    return @(
                        [pscustomobject]@{
                            id = 1
                            instanceName = "Sole"
                            dmsInstanceRouteContexts = @()
                        }
                    )
                }

                $result = Invoke-ConfigureLocalDmsInstance -EnvironmentFile $isolatedEnvFile -NoDmsInstance

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
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
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
                function Get-DmsInstances {
                    return @(
                        [pscustomobject]@{
                            id = 1
                            instanceName = "Sole"
                            connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=isolated_db;'
                            dmsInstanceRouteContexts = @()
                        }
                    )
                }

                Invoke-ProvisionDmsSchema -EnvironmentFile $isolatedEnvFile -InstanceId @(1)

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

    Context "wrapper consumes SelectedInstanceIds" {
        It "Resolve-WrapperSelectedInstanceIds prefers SelectedInstanceIds over the legacy alias" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

            $configured = [pscustomobject]@{
                SelectedInstanceIds = [long[]]@(101, 102)
                InstanceIds = [long[]]@(901, 902)
                HasRouteQualifiedInstances = $false
            }
            $resolved = Resolve-WrapperSelectedInstanceIds -ConfigureResult $configured

            $resolved | Should -Be @([long]101, [long]102)
        }

        It "Resolve-WrapperSelectedInstanceIds falls back to InstanceIds when SelectedInstanceIds is absent" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

            $configured = [pscustomobject]@{
                InstanceIds = [long[]]@(42)
                HasRouteQualifiedInstances = $false
            }
            $resolved = Resolve-WrapperSelectedInstanceIds -ConfigureResult $configured

            $resolved | Should -Be @([long]42)
        }

        It "Resolve-WrapperSelectedInstanceIds throws when neither property is present" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "bootstrap-wrapper.psm1") -Force

            $configured = [pscustomobject]@{ Tenant = "" }

            { Resolve-WrapperSelectedInstanceIds -ConfigureResult $configured } |
                Should -Throw -ExpectedMessage "*missing SelectedInstanceIds*"
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Sole"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=auth_consumer_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) } |
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
            function Get-DmsInstances { return @() }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) } |
                Should -Throw -ExpectedMessage "*configure-local-dms-instance.ps1*"
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "ExternalNoPort"
                        connectionString = 'host=managed-pg.example.com;username=ops_user;password=ops_pass;database=ext_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "DockerInternalNoPort"
                        connectionString = 'host=dms-postgresql;username=postgres;password=secret-pass;database=docker_internal_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "MissingHost"
                        connectionString = 'username=postgres;password=secret-pass;database=no_host_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            { Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1) } |
                Should -Throw -ExpectedMessage "*missing a host key*"
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Sole"
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $result = Invoke-ConfigureLocalDmsInstance -EnvironmentFile $script:repo.EnvFile -NoDmsInstance

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

    Context "behavioral start-script lockdown" {
        BeforeAll {
            function script:Invoke-StartScriptLockdownBlock {
                <#
                .SYNOPSIS
                Extract the main up-path (or -DmsOnly) try/finally from start-local-dms.ps1
                or start-published-dms.ps1 and execute it in an isolated scope with a docker
                stub. The extracted block is the production code, so any drift in the lockdown
                semantics (env assignment removed, docker call moved before the assignment,
                env var renamed) is caught at the behavioral level — not just the regex level.
                When -DmsOnly is set, the helper targets the -DmsOnly branch's try block (the
                one with a trailing `dms` service name) instead of the main up block.
                #>
                param(
                    [Parameter(Mandatory)]
                    [string]$ScriptPath,

                    [Parameter(Mandatory)]
                    [string]$CapturePath,

                    [switch]$DmsOnly
                )

                $sourceText = Get-Content -Raw -LiteralPath $ScriptPath
                $parseErrors = $null
                $tokens = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                    $sourceText, [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors.Count -gt 0) {
                    throw "Failed to parse $ScriptPath"
                }

                $tries = $ast.FindAll(
                    { $args[0] -is [System.Management.Automation.Language.TryStatementAst] },
                    $true)
                # The outer try wraps the entire script body, so any docker call lives inside
                # it. We want the innermost try whose body sets NEED_DATABASE_SETUP=false and
                # immediately calls `docker compose ... up $upArgs` — main up path when -DmsOnly
                # is false (regex rejects a trailing word like `dms`); -DmsOnly branch when
                # -DmsOnly is true (regex requires a trailing `dms` service name).
                if ($DmsOnly) {
                    $dockerRegex = 'docker compose .* up \$upArgs\s+dms\b'
                    $branchLabel = "-DmsOnly"
                } else {
                    $dockerRegex = 'docker compose .* up \$upArgs(?!\s*\w)'
                    $branchLabel = "main up"
                }
                $candidates = @($tries | Where-Object {
                    $bodyText = $_.Body.Extent.Text
                    ($bodyText -match '\$env:NEED_DATABASE_SETUP\s*=\s*"false"') -and
                    ($bodyText -match $dockerRegex)
                })
                $upPathTry = $candidates | Sort-Object { $_.Body.Extent.Text.Length } | Select-Object -First 1
                if ($null -eq $upPathTry) {
                    throw "Could not locate the $branchLabel lockdown try block in $ScriptPath"
                }

                $extracted = $upPathTry.Extent.Text
                $escapedCapture = $CapturePath.Replace("'", "''")
                # The docker stub deliberately omits a param() block: declaring
                # ValueFromRemainingArguments makes it an advanced function, which then binds
                # common parameters like -PipelineVariable and trips on the production
                # `-p dms-local` flag. A simple function captures all args via the automatic
                # `$args` variable and avoids the ambiguity.
                $isolated = @"
`$files = @('-f', 'local-dms.yml')
`$EnvironmentFile = '.env'
`$upArgs = @('-d')
function docker {
    Add-Content -LiteralPath '$escapedCapture' -Value "NEED_DATABASE_SETUP=`$env:NEED_DATABASE_SETUP"
    Add-Content -LiteralPath '$escapedCapture' -Value "DMS_DEPLOY_DATABASE_ON_STARTUP=`$env:DMS_DEPLOY_DATABASE_ON_STARTUP"
    Add-Content -LiteralPath '$escapedCapture' -Value "AppSettings__DeployDatabaseOnStartup=`$env:AppSettings__DeployDatabaseOnStartup"
    `$global:LASTEXITCODE = 0
}
$extracted
"@
                & ([scriptblock]::Create($isolated))
            }
        }

        It "start-local-dms.ps1 main up path captures NEED_DATABASE_SETUP=false at docker call time" {
            $capturePath = Join-Path $script:repo.RepoRoot "docker-up-capture-local.txt"
            $startScriptPath = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"

            Invoke-StartScriptLockdownBlock -ScriptPath $startScriptPath -CapturePath $capturePath

            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "NEED_DATABASE_SETUP=false"
            $captured | Should -Contain "DMS_DEPLOY_DATABASE_ON_STARTUP=false"
            $captured | Should -Contain "AppSettings__DeployDatabaseOnStartup=false"
        }

        It "start-published-dms.ps1 main up path captures NEED_DATABASE_SETUP=false at docker call time" {
            $capturePath = Join-Path $script:repo.RepoRoot "docker-up-capture-published.txt"
            $startScriptPath = Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"

            Invoke-StartScriptLockdownBlock -ScriptPath $startScriptPath -CapturePath $capturePath

            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "NEED_DATABASE_SETUP=false"
            $captured | Should -Contain "DMS_DEPLOY_DATABASE_ON_STARTUP=false"
            $captured | Should -Contain "AppSettings__DeployDatabaseOnStartup=false"
        }

        It "start-local-dms.ps1 -DmsOnly path captures NEED_DATABASE_SETUP=false at docker call time" {
            $capturePath = Join-Path $script:repo.RepoRoot "docker-dmsonly-capture-local.txt"
            $startScriptPath = Join-Path $script:sourceDockerComposeRoot "start-local-dms.ps1"

            Invoke-StartScriptLockdownBlock -ScriptPath $startScriptPath -CapturePath $capturePath -DmsOnly

            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "NEED_DATABASE_SETUP=false"
            $captured | Should -Contain "DMS_DEPLOY_DATABASE_ON_STARTUP=false"
            $captured | Should -Contain "AppSettings__DeployDatabaseOnStartup=false"
        }

        It "start-published-dms.ps1 -DmsOnly path captures NEED_DATABASE_SETUP=false at docker call time" {
            $capturePath = Join-Path $script:repo.RepoRoot "docker-dmsonly-capture-published.txt"
            $startScriptPath = Join-Path $script:sourceDockerComposeRoot "start-published-dms.ps1"

            Invoke-StartScriptLockdownBlock -ScriptPath $startScriptPath -CapturePath $capturePath -DmsOnly

            $captured = @(Get-Content -LiteralPath $capturePath)
            $captured | Should -Contain "NEED_DATABASE_SETUP=false"
            $captured | Should -Contain "DMS_DEPLOY_DATABASE_ON_STARTUP=false"
            $captured | Should -Contain "AppSettings__DeployDatabaseOnStartup=false"
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
            ($warnings | ForEach-Object Message) -join " " | Should -Match "Schemas will NOT be provisioned"
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
                function Get-DmsInstances {
                    return @(
                        [pscustomobject]@{
                            id = 1
                            instanceName = "Sole"
                            connectionString = $connectionString
                            dmsInstanceRouteContexts = @()
                        }
                    )
                }

                Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Sole"
                        connectionString = $connectionString
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool1
            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

            $env:DMS_SCHEMA_TOOL_PATH = $fakeTool2
            Invoke-ProvisionDmsSchema -EnvironmentFile $script:repo.EnvFile -InstanceId @(1)

            $first = Get-OrderedSchemaPaths -Captured @(Get-Content -LiteralPath $capturePath1)
            $second = Get-OrderedSchemaPaths -Captured @(Get-Content -LiteralPath $capturePath2)

            ($first -join "|") | Should -Be ($second -join "|")
        }
    }

    Context "Resolve-BootstrapAdminClient" {
        It "returns the historical defaults when neither override key is present" {
            Import-Module (Join-Path $script:repo.DockerComposeRoot "env-utility.psm1") -Force

            $resolved = Resolve-BootstrapAdminClient -EnvValues @{ POSTGRES_PASSWORD = "x" }

            $resolved.ClientId | Should -Be "dms-instance-admin"
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

            $resolved.ClientId | Should -Be "dms-instance-admin"
            $resolved.ClientSecret | Should -Be "secret-only-value"
        }
    }

    Context "bootstrap admin client flows through to configure and provision" {
        It "configure-local-dms-instance.ps1 calls Add-CmsClient and Get-CmsToken with the env-resolved bootstrap admin client" {
            $overrideEnvFile = Join-Path $script:repo.DockerComposeRoot "env-with-bootstrap-admin.env"
            @"
POSTGRES_PASSWORD=secret-pass
POSTGRES_DB_NAME=edfi_datamanagementservice
POSTGRES_PORT=5544
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Sole"
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ConfigureLocalDmsInstance -EnvironmentFile $overrideEnvFile -NoDmsInstance | Out-Null

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
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
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
            function Get-DmsInstances {
                return @(
                    [pscustomobject]@{
                        id = 1
                        instanceName = "Sole"
                        connectionString = 'host=dms-postgresql;port=5432;username=postgres;password=secret-pass;database=prov_admin_db;'
                        dmsInstanceRouteContexts = @()
                    }
                )
            }

            Invoke-ProvisionDmsSchema -EnvironmentFile $overrideEnvFile -InstanceId @(1)

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
DMS_CONFIG_ASPNETCORE_HTTP_PORTS=18081
DMS_HTTP_PORTS=18080
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
NEED_DATABASE_SETUP=false
DMS_DEPLOY_DATABASE_ON_STARTUP=false
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
            function Get-DmsInstances { return @() }

            $thrownMessage = $null
            try {
                Invoke-ProvisionDmsSchema -EnvironmentFile $overrideEnvFile -InstanceId @(1)
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
}
