# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Finding 4: the connection-string validator must stay available - parsing with the EXACT runtime
# providers (Npgsql / Microsoft.Data.SqlClient via the api-schema-tools 'connection validate' verb) - on a
# clean Docker/PowerShell-only published host with no .NET SDK, no source-build output, and no prebuilt
# host tool. The distribution/execution boundary is Resolve-DmsConnectionValidator: a host executable when
# one resolves, otherwise the DMS image that bundles the tool, run via 'docker run'. The parser is never
# weakened or replaced.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    # Defensive: this suite's 'Mock -ModuleName bootstrap-schema-tool' requires exactly one module of that
    # name in the session. The isolated-repo suites now unload their temp-workspace modules in AfterEach, so
    # no copy should leak in; dropping any already-loaded copy before importing the canonical one keeps this
    # suite correct regardless of run order, even if a future fixture regresses that cleanup.
    Get-Module bootstrap-schema-tool, env-utility | Remove-Module -Force -ErrorAction SilentlyContinue
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force
    Import-Module (Join-Path $script:composeRoot "bootstrap-schema-tool.psm1") -Force
}

Describe "Resolve-DmsConnectionValidator distribution boundary (finding 4)" {
    # An explicitly configured DMS_SCHEMA_TOOL_PATH (-RequestedPath) is AUTHORITATIVE: it must resolve or
    # fail hard, and it is never masked by the image fallback. The image fallback exists only for the
    # no-explicit-path case (a clean Docker/PowerShell-only published host). These two authorities are
    # tested separately: the explicit-path cases exercise the real Resolve-DmsSchemaTool (a present/absent
    # file is deterministic), while the no-explicit-path cases mock Resolve-DmsSchemaTool so the
    # host-resolved / SDK-built / nothing-resolved outcomes are deterministic without a repo build or SDK.

    Context "an explicit -RequestedPath is authoritative" {
        BeforeAll {
            $script:existingToolStub = Join-Path ([System.IO.Path]::GetTempPath()) ("api-schema-tools-stub-" + [guid]::NewGuid().ToString("N") + ".exe")
            Set-Content -LiteralPath $script:existingToolStub -Value ""
        }
        AfterAll {
            Remove-Item -LiteralPath $script:existingToolStub -ErrorAction SilentlyContinue
        }

        It "resolves a present explicit path as a host executable" {
            $validator = Resolve-DmsConnectionValidator -RequestedPath $script:existingToolStub -DmsImage "edfialliance/ed-fi-api:test"
            $validator.Kind | Should -Be 'HostExe'
            $validator.Path | Should -Be $script:existingToolStub
        }

        It "fails a missing explicit path even when a DMS image is available, and never returns the image fallback" {
            $missing = Join-Path ([System.IO.Path]::GetTempPath()) ("no-such-tool-" + [guid]::NewGuid().ToString("N") + ".exe")
            $result = $null
            $caught = $null
            try {
                $result = Resolve-DmsConnectionValidator -RequestedPath $missing -DmsImage "edfialliance/ed-fi-api:test"
            }
            catch {
                $caught = $_
            }

            $caught | Should -Not -BeNullOrEmpty -Because "an explicitly configured DMS_SCHEMA_TOOL_PATH that does not exist is a hard error"
            $caught.Exception.Message | Should -Match 'was not found' -Because "the failure names the missing configured path"
            # Prove the image fallback was NOT selected: resolution threw rather than returning a descriptor.
            $result | Should -BeNullOrEmpty -Because "a missing explicit path must never be masked by the '-DmsImage' fallback"
        }
    }

    Context "no explicit path: host tool preferred, then image fallback, then guidance" {
        It "uses a resolved/discovered host tool when Resolve-DmsSchemaTool returns one" {
            Mock -ModuleName 'bootstrap-schema-tool' Resolve-DmsSchemaTool { "/resolved/api-schema-tools" }
            $validator = Resolve-DmsConnectionValidator -RequestedPath "" -DmsImage "edfialliance/ed-fi-api:test"
            $validator.Kind | Should -Be 'HostExe'
            $validator.Path | Should -Be "/resolved/api-schema-tools"
        }

        It "requests a -BuildIfMissing publish so the SDK-build path is reachable without an explicit path" {
            Mock -ModuleName 'bootstrap-schema-tool' Resolve-DmsSchemaTool { "/resolved/api-schema-tools" }
            $null = Resolve-DmsConnectionValidator -RequestedPath "" -DmsImage ""
            Should -Invoke -ModuleName 'bootstrap-schema-tool' Resolve-DmsSchemaTool -Times 1 -Exactly -ParameterFilter { $BuildIfMissing }
        }

        It "falls back to the DMS image when no host tool resolves and an image is supplied" {
            Mock -ModuleName 'bootstrap-schema-tool' Resolve-DmsSchemaTool { throw "In-repo api-schema-tools tool not found." }
            $validator = Resolve-DmsConnectionValidator -RequestedPath "" -DmsImage "edfialliance/ed-fi-api:test"
            $validator.Kind | Should -Be 'DockerImage'
            $validator.Image | Should -Be "edfialliance/ed-fi-api:test"
            $validator.ToolPath | Should -Match 'api-schema-tools\.dll$'
        }

        It "throws with build/configuration guidance when no host tool and no image are available" {
            Mock -ModuleName 'bootstrap-schema-tool' Resolve-DmsSchemaTool { throw "Unable to resolve the api-schema-tools executable. Build src/dms/clis/EdFi.DataManagementService.SchemaTools or set DMS_SCHEMA_TOOL_PATH." }
            { Resolve-DmsConnectionValidator -RequestedPath "" -DmsImage "" } | Should -Throw -ExpectedMessage '*Unable to resolve the api-schema-tools executable*'
        }
    }
}

Describe "Invoke-ConnectionStringValidation runs the verb inside the DMS image for a DockerImage descriptor (finding 4)" {
    # A module's '& docker' resolves module-local then GLOBAL scope, so a global stub intercepts the
    # in-module invocation and captures its arguments to a file. This avoids Pester's -ModuleName mock,
    # which cannot disambiguate the duplicate 'env-utility' modules that other suites in this lane leak
    # into the session by importing a temp-sandbox copy.
    BeforeEach {
        $script:dockerCaptureFile = Join-Path ([System.IO.Path]::GetTempPath()) ("f4-docker-" + [guid]::NewGuid().ToString("N") + ".log")
        $env:DMS_F4_DOCKER_CAPTURE = $script:dockerCaptureFile
        function global:docker {
            Add-Content -LiteralPath $env:DMS_F4_DOCKER_CAPTURE -Value ($args -join "`n")
            $global:LASTEXITCODE = 0
            '{"valid":true,"database":"edfi_x","error":null}'
        }
    }
    AfterEach {
        # Remove the global stub so real 'docker' is restored for every later test and suite.
        if (Test-Path -LiteralPath "Function:\docker") {
            Remove-Item -LiteralPath "Function:\docker" -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $script:dockerCaptureFile -ErrorAction SilentlyContinue
        $env:DMS_F4_DOCKER_CAPTURE = $null
    }

    It "dispatches to 'docker run' with the bundled tool, offline, and parses the result" {
        $descriptor = [pscustomobject]@{
            Kind     = 'DockerImage'
            Image    = 'edfialliance/ed-fi-api:test'
            ToolPath = '/app/ApiSchemaTools/api-schema-tools.dll'
        }
        $result = Get-CmsConnectionStringDatabaseName -Engine postgresql -ConnectionString 'Host=h;Database=edfi_x' -SchemaToolPath $descriptor
        $result | Should -Be @('edfi_x')

        $captured = @(Get-Content -LiteralPath $script:dockerCaptureFile)
        $captured | Should -Contain 'run'
        $captured | Should -Contain 'none' -Because "the verb only parses the string, so it must run with --network none"
        $captured | Should -Contain 'edfialliance/ed-fi-api:test'
        $captured | Should -Contain '/app/ApiSchemaTools/api-schema-tools.dll'
        $captured | Should -Contain 'validate'
        $captured | Should -Contain 'postgresql'
    }

    It "canonicalizes a case-variant engine before running the verb in the image" {
        $descriptor = [pscustomobject]@{ Kind = 'DockerImage'; Image = 'img:test'; ToolPath = '/app/ApiSchemaTools/api-schema-tools.dll' }
        $null = Get-CmsConnectionStringDatabaseName -Engine 'MSSQL' -ConnectionString 'Server=s;Database=edfi_x' -SchemaToolPath $descriptor

        # Case-sensitive: the verb must receive the canonical 'mssql', never the 'MSSQL' variant.
        $captured = @(Get-Content -LiteralPath $script:dockerCaptureFile)
        ($captured -ccontains 'mssql') | Should -BeTrue
        ($captured -ccontains 'MSSQL') | Should -BeFalse
    }
}

Describe "Get-ComposeResolvedConfiguration exposes the DMS image (finding 4)" {
    It "resolves the published dms service image so the validator can run inside it" {
        docker compose version *> $null
        $LASTEXITCODE | Should -Be 0 -Because "docker compose is a hard prerequisite for the Compose oracle"
        $files = @("-f", (Join-Path $script:composeRoot "postgresql.yml"), "-f", (Join-Path $script:composeRoot "published-dms.yml"))
        $resolved = Get-ComposeResolvedConfiguration -ComposeFiles $files -EnvironmentFile (Join-Path $script:composeRoot ".env.example") -ProjectName "dms-f4-image-oracle"
        $resolved.DmsImage | Should -Match 'ed-fi-api' -Because "the runtime contract needs the concrete DMS image to run the validator inside it on a Docker-only host"
    }
}
