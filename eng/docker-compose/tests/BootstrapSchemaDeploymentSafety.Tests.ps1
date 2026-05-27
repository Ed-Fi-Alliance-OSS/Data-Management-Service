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

                [switch]$PathTraversal
            )

            $bootstrapRoot = Join-Path $DockerComposeRoot ".bootstrap"
            $apiSchemaRoot = Join-Path $bootstrapRoot "ApiSchema"
            New-Item -ItemType Directory -Path (Join-Path $apiSchemaRoot "schemas/Ed-Fi") -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $apiSchemaRoot "schemas/Sample") -Force | Out-Null

            if (-not $MissingCoreFile) {
                "{}" | Set-Content -LiteralPath (Join-Path $apiSchemaRoot "schemas/Ed-Fi/ApiSchema.json") -Encoding utf8
            }
            "{}" | Set-Content -LiteralPath (Join-Path $apiSchemaRoot "schemas/Sample/ApiSchema.json") -Encoding utf8

            $coreSchemaPath = if ($PathTraversal) { "../escape.json" } else { "schemas/Ed-Fi/ApiSchema.json" }
            $apiSchemaManifest = [ordered]@{
                version = 1
                projects = @(
                    [ordered]@{
                        projectName = "Ed-Fi"
                        projectEndpointName = "ed-fi"
                        isExtensionProject = $false
                        schemaPath = $coreSchemaPath
                    },
                    [ordered]@{
                        projectName = "Sample"
                        projectEndpointName = "sample"
                        isExtensionProject = $true
                        schemaPath = "schemas/Sample/ApiSchema.json"
                    }
                )
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
                    directory = "claims"
                    fingerprint = "claims"
                    expectedVerificationChecks = @()
                }
                seed = [ordered]@{
                    extensionNamespacePrefixes = @()
                }
            }
            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
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
}
