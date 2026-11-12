# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The Swagger UI shipped under eng/docker-compose loads the DMS API definitions under the route context
# in DMS_SWAGGER_UI_ROUTE_CONTEXT. With multi-tenancy on, the DMS metadata routes answer only under the
# tenant and every configured route qualifier value, so an unqualified listing URL would leave the UI
# with no definition to show. These tests cover the URL construction and the wiring that carries the
# setting from the environment file into the page; they do not render the page.

Describe "Local Swagger UI route context" {
    BeforeAll {
        $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:initializer = Join-Path $script:composeRoot "custom-swagger-ui/swagger-initializer.js"
        # Loads the initializer into a bare `window`, exposes its pure helpers, and prints the JSON
        # result of the expression. The Pester DMS lane runs on ubuntu-latest, which ships Node.js.
        $script:runHelper = {
            param([string]$Expression)
            $program = "const fs = require('fs'); const window = {}; eval(fs.readFileSync(process.argv[1], 'utf8')); " +
                "const h = window.EdFiLocalSwagger; console.log(JSON.stringify($Expression));"
            $output = & node -e $program $script:initializer 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
            return ($output | Out-String | ConvertFrom-Json)
        }
        $script:base = "http://localhost:8080/DistrictA/2024/metadata"
    }

    It "loads the definitions under the configured route context" {
        & $script:runHelper "h.metadataBaseUrl('8080', 'DistrictA/2024')" | Should -Be $script:base
    }

    It "keeps the unqualified metadata routes when no route context is configured, as a single-tenant stack needs" {
        & $script:runHelper "h.metadataBaseUrl('8080', '')" | Should -Be "http://localhost:8080/metadata"
        & $script:runHelper "h.metadataBaseUrl('8080', undefined)" | Should -Be "http://localhost:8080/metadata"
    }

    It "ignores surrounding slashes and encodes each segment of the route context" {
        & $script:runHelper "h.metadataBaseUrl('8080', '/DistrictA/2024/')" | Should -Be $script:base
        & $script:runHelper "h.metadataBaseUrl('8080', 'District A/2024')" |
            Should -Be "http://localhost:8080/District%20A/2024/metadata"
    }

    It "lists the advertised definitions by their endpoint URIs, without Discovery, in either listing shape" {
        $entries = '[{"name":"Resources","endpointUri":"http://localhost:8080/DistrictA/2024/metadata/specifications/resources-spec.json"},{"name":"Discovery","endpointUri":"http://localhost:8080/DistrictA/2024/metadata/specifications/discovery-spec.json"},{"name":"Change-Queries","endpointUri":"http://localhost:8080/DistrictA/2024/metadata/changequeries/v1/swagger.json"}]'
        $expected = @(
            "Resources|http://localhost:8080/DistrictA/2024/metadata/specifications/resources-spec.json",
            "Change-Queries|http://localhost:8080/DistrictA/2024/metadata/changequeries/v1/swagger.json"
        )

        foreach ($listing in @("{`"specifications`":$entries}", $entries)) {
            $definitions = & $script:runHelper "h.definitionsFrom($listing, '$script:base')"

            @($definitions | ForEach-Object { "$($_.name)|$($_.url)" }) | Should -Be $expected -Because "listing $listing"
        }
    }

    It "falls back to the Resources and Descriptors documents under the same route context" {
        foreach ($listing in @("null", "[]", "{}", '{"specifications":"unexpected"}')) {
            $definitions = & $script:runHelper "h.definitionsFrom($listing, '$script:base')"

            @($definitions | ForEach-Object { "$($_.name)|$($_.url)" }) | Should -Be @(
                "Resources|http://localhost:8080/DistrictA/2024/metadata/specifications/resources-spec.json",
                "Descriptors|http://localhost:8080/DistrictA/2024/metadata/specifications/descriptors-spec.json"
            ) -Because "listing $listing"
        }
    }

    It "names the setting that supplies the route context, after the DMS problem detail when there is one" {
        $detail = 'Metadata requests must include the tenant and the configured route qualifiers in the path, for example /{tenant}/{schoolYear}/metadata/specifications.'

        $withDetail = & $script:runHelper "h.routeContextNotice('$detail')"
        $withoutDetail = & $script:runHelper "h.routeContextNotice(null)"

        $withDetail | Should -BeLike "$detail Set DMS_SWAGGER_UI_ROUTE_CONTEXT *DistrictA/2024*"
        $withoutDetail | Should -BeLike "Set DMS_SWAGGER_UI_ROUTE_CONTEXT *DistrictA/2024*"
    }

    It "carries DMS_SWAGGER_UI_ROUTE_CONTEXT from the environment file into the page" {
        $compose = Get-Content -LiteralPath (Join-Path $script:composeRoot "swagger-ui.compose.yml") -Raw
        $compose | Should -Match '(?m)^\s*- DMS_SWAGGER_UI_ROUTE_CONTEXT=\$\{DMS_SWAGGER_UI_ROUTE_CONTEXT:-\}\s*$'
        $compose | Should -Match 'envsubst ''[^'']*\$\$DMS_SWAGGER_UI_ROUTE_CONTEXT[^'']*'' < /tmp/nginx-template/index\.html' `
            -Because "envsubst leaves a variable it is not told about as literal text"

        $page = Get-Content -LiteralPath (Join-Path $script:composeRoot "custom-swagger-ui/index.html") -Raw
        $page | Should -Match 'window\.DMS_SWAGGER_UI_ROUTE_CONTEXT = "\$\{DMS_SWAGGER_UI_ROUTE_CONTEXT\}";'
    }

    It "ships the multi-tenant environment file with the route context the getting-started guide creates, and the example with none" {
        Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.multitenancy") -Raw |
            Should -Match '(?m)^DMS_SWAGGER_UI_ROUTE_CONTEXT=DistrictA/2024\s*$'
        Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example") -Raw |
            Should -Match '(?m)^DMS_SWAGGER_UI_ROUTE_CONTEXT=\s*$'
    }
}
