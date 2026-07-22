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
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force
    Import-Module (Join-Path $script:composeRoot "bootstrap-schema-tool.psm1") -Force
}

Describe "Resolve-DmsConnectionValidator distribution boundary (finding 4)" {
    BeforeAll {
        $script:existingToolStub = Join-Path ([System.IO.Path]::GetTempPath()) ("api-schema-tools-stub-" + [guid]::NewGuid().ToString("N") + ".exe")
        Set-Content -LiteralPath $script:existingToolStub -Value ""
    }
    AfterAll {
        Remove-Item -LiteralPath $script:existingToolStub -ErrorAction SilentlyContinue
    }

    It "prefers a host executable when one resolves (explicit path)" {
        $validator = Resolve-DmsConnectionValidator -RequestedPath $script:existingToolStub -DmsImage "edfialliance/ed-fi-api:test"
        $validator.Kind | Should -Be 'HostExe'
        $validator.Path | Should -Be $script:existingToolStub
    }

    It "falls back to the DMS image when no host tool resolves and an image is supplied" {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) ("no-such-tool-" + [guid]::NewGuid().ToString("N") + ".exe")
        $validator = Resolve-DmsConnectionValidator -RequestedPath $missing -DmsImage "edfialliance/ed-fi-api:test"
        $validator.Kind | Should -Be 'DockerImage'
        $validator.Image | Should -Be "edfialliance/ed-fi-api:test"
        $validator.ToolPath | Should -Match 'api-schema-tools\.dll$'
    }

    It "throws when neither a host tool nor a container image is available" {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) ("no-such-tool-" + [guid]::NewGuid().ToString("N") + ".exe")
        { Resolve-DmsConnectionValidator -RequestedPath $missing -DmsImage "" } | Should -Throw
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
