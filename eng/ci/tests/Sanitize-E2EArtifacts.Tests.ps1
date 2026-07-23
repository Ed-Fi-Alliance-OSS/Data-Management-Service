# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "sanitize-e2e-artifacts Get-SanitizedText (DMS-1284)" {
    BeforeAll {
        $script:sanitizer = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../sanitize-e2e-artifacts.ps1"))
        # Dot-source to load the functions; the script's run guard prevents Invoke-ArtifactSanitization
        # from executing when dot-sourced, and an empty -Path would be a no-op regardless.
        . $script:sanitizer -Path $TestDrive
    }

    It "redacts a PostgreSQL connection-string password but preserves benign fields" {
        $result = Get-SanitizedText -Text "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"

        $result | Should -Not -Match "abcdefgh1!"
        $result | Should -Match "password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "username=postgres"
        $result | Should -Match "port=5432"
        $result | Should -Match "database=edfi_datamanagementservice"
    }

    It "redacts a SQL Server connection-string password but preserves benign fields" {
        $result = Get-SanitizedText -Text "Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=Aa1!SqlSecret;TrustServerCertificate=true;"

        $result | Should -Not -Match "Aa1!SqlSecret"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "User Id=sa"
        $result | Should -Match "TrustServerCertificate=true"
    }

    It "redacts a double-quoted connection-string password containing spaces and a semicolon" {
        $result = Get-SanitizedText -Text 'Server=dms-mssql,1433;Database=db;User Id=sa;Password="Aa1! secret; with spaces";TrustServerCertificate=true'

        $result | Should -Not -Match "secret"
        $result | Should -Not -Match "with spaces"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "User Id=sa"
        $result | Should -Match "TrustServerCertificate=true"
    }

    It "redacts a single-quoted connection-string password" {
        $result = Get-SanitizedText -Text "host=h;password='pa;ss word';database=db"

        $result | Should -Not -Match "pa;ss word"
        $result | Should -Match "password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "database=db"
    }

    It "redacts an XML-escaped connection-string password as it appears inside a TRX" {
        $result = Get-SanitizedText -Text '<Output>connect failed: Server=s;Password=&quot;Aa1!xmlSecretValue&quot;;TrustServerCertificate=true</Output>'

        $result | Should -Not -Match "Aa1!xmlSecretValue"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "TrustServerCertificate=true"
    }

    It "redacts a double-quoted password with ADO.NET-doubled embedded quotes without leaking the tail" {
        # ADO.NET wraps a value containing both quote styles in double quotes and doubles each embedded
        # double quote. A naive `"[^"]*"` match stops at the first quote of a doubled pair and leaks the
        # remainder; the whole span (through the doubled pairs) must be redacted. The distinctive secret
        # carries a semicolon, spaces, a single quote, and embedded double quotes.
        $result = Get-SanitizedText -Text 'Server=dms-mssql,1433;Database=db;User Id=sa;Password="FRAGA; sp''ace ""FRAGB"" FRAGC";TrustServerCertificate=true'

        $result | Should -Not -Match "FRAGA"
        $result | Should -Not -Match "FRAGB"
        $result | Should -Not -Match "FRAGC"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "TrustServerCertificate=true"
    }

    It "redacts an XML-escaped password with doubled &quot;&quot; pairs inside a TRX without leaking the tail" {
        $result = Get-SanitizedText -Text '<Output>Server=s;Password=&quot;XFRAGA; space &quot;&quot;XFRAGB&quot;&quot; XFRAGC&quot;;TrustServerCertificate=true</Output>'

        $result | Should -Not -Match "XFRAGA"
        $result | Should -Not -Match "XFRAGB"
        $result | Should -Not -Match "XFRAGC"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "TrustServerCertificate=true"
    }

    It "redacts a single-quoted password with ADO.NET-doubled embedded quotes without leaking the tail" {
        # ADO.NET doubles an embedded single quote inside a single-quoted value; the doubled pairs must be
        # consumed as part of the secret rather than terminating the match at the first inner quote.
        $result = Get-SanitizedText -Text 'host=h;password=''SFRAGA; it''''SFRAGB''''s SFRAGC'';database=db'

        $result | Should -Not -Match "SFRAGA"
        $result | Should -Not -Match "SFRAGB"
        $result | Should -Not -Match "SFRAGC"
        $result | Should -Match "password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "database=db"
    }

    It "redacts JSON credential properties but preserves benign properties" {
        $result = Get-SanitizedText -Text '{ "clientId": "svc-1", "clientSecret": "topSecretValue", "password": "pw12345", "tenant": "Tenant_255901" }'

        $result | Should -Not -Match "topSecretValue"
        $result | Should -Not -Match "pw12345"
        $result | Should -Match '"clientId": "svc-1"'
        $result | Should -Match '"tenant": "Tenant_255901"'
    }

    It "redacts form-encoded credentials but preserves benign parameters" {
        $result = Get-SanitizedText -Text "grant_type=client_credentials&client_id=CMSReadOnlyAccess&client_secret=veryHushHush"

        $result | Should -Not -Match "veryHushHush"
        $result | Should -Match "grant_type=client_credentials"
        $result | Should -Match "client_id=CMSReadOnlyAccess"
    }

    It "redacts Authorization headers and bearer tokens" {
        $header = Get-SanitizedText -Text "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.somepayload.somesignature"
        $header | Should -Not -Match "eyJhbGciOiJIUzI1NiJ9"
        $header | Should -Match "Authorization: Bearer \*\*\*REDACTED\*\*\*"

        $basic = Get-SanitizedText -Text "Authorization: Basic dXNlcjpwYXNzd29yZA=="
        $basic | Should -Not -Match "dXNlcjpwYXNzd29yZA"
    }

    It "redacts environment-style secrets while keeping benign env lines" {
        $text = @"
MSSQL_SA_PASSWORD=Aa1!EnvSecretValue
POSTGRES_PORT=5435
DMS_CONFIG_IDENTITY_CLIENT_SECRET=clientSecretEnvValue
DMS_CONFIG_DATABASE_ENCRYPTION_KEY=encryptionKeyEnvValue
DMS_DATASTORE=mssql
"@

        $result = Get-SanitizedText -Text $text

        $result | Should -Not -Match "Aa1!EnvSecretValue"
        $result | Should -Not -Match "clientSecretEnvValue"
        $result | Should -Not -Match "encryptionKeyEnvValue"
        $result | Should -Match "POSTGRES_PORT=5435"
        $result | Should -Match "DMS_DATASTORE=mssql"
    }

    It "redacts the ConnectionStrings__MssqlAdmin secret written to GITHUB_ENV" {
        $result = Get-SanitizedText -Text "ConnectionStrings__MssqlAdmin=Server=localhost,1433;User Id=sa;Password=Aa1!ghenvsecret;TrustServerCertificate=true;"

        $result | Should -Not -Match "Aa1!ghenvsecret"
        $result | Should -Match "User Id=sa"
    }

    It "redacts a bracketed PowerShell key/value credential pair for <Key> but keeps the key label" -ForEach @(
        @{ Key = "ClientSecret" }
        @{ Key = "ClientKey" }
        @{ Key = "Password" }
        @{ Key = "Secret" }
        @{ Key = "AccessToken" }
        @{ Key = "RefreshToken" }
        @{ Key = "Token" }
        @{ Key = "ApiKey" }
        @{ Key = "EncryptionKey" }
    ) {
        # Placeholder value only; the real build-dms.ps1 CMS-bootstrap output shape is [<Key>, <value>].
        $result = Get-SanitizedText -Text "CMSReadOnlyAccess client: [$Key, PLACEHOLDER_CREDENTIAL_VALUE]"

        $result | Should -Not -Match "PLACEHOLDER_CREDENTIAL_VALUE"
        $result | Should -Match "\[$Key, \*\*\*REDACTED\*\*\*\]"
    }

    It "redacts a bracketed credential whose value wraps onto following lines" {
        # PowerShell console line wrapping can split a bracketed pair between the comma and the value or
        # across the value itself. Placeholder tokens only.
        $text = "[ClientSecret,`n    PLACEHOLDER_WRAP_LINE1`n    PLACEHOLDER_WRAP_LINE2]"

        $result = Get-SanitizedText -Text $text

        $result | Should -Not -Match "PLACEHOLDER_WRAP_LINE1"
        $result | Should -Not -Match "PLACEHOLDER_WRAP_LINE2"
        $result | Should -Match "\*\*\*REDACTED\*\*\*"
    }

    It "preserves non-credential bracketed key/value diagnostics" {
        $text = "[Id, 12345] [ClientSecretHash, keepThisHashValue] [2024-01-01T00:00:00Z, INFO ready]"

        Get-SanitizedText -Text $text | Should -Be $text
    }

    It "preserves benign multiline diagnostics unchanged" {
        $text = "Starting DMS...`nDMS is ready!`nExecuted endpoint 'GET /health' responded 200 in 3 ms`nLoaded 2 data stores for tenant Tenant_255901"

        Get-SanitizedText -Text $text | Should -Be $text
    }

    It "returns empty string unchanged" {
        Get-SanitizedText -Text "" | Should -Be ""
    }
}

Describe "sanitize-e2e-artifacts Invoke-ArtifactSanitization (DMS-1284)" {
    BeforeAll {
        $script:sanitizer = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../sanitize-e2e-artifacts.ps1"))
        . $script:sanitizer -Path $TestDrive
    }

    It "sanitizes matching artifact files in place under a directory" {
        $logDir = Join-Path $TestDrive "logs-in-place"
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $logFile = Join-Path $logDir "cms.log"
        Set-Content -LiteralPath $logFile -Value "connecting Password=leakedValue123;host=dms-mssql" -NoNewline

        Invoke-ArtifactSanitization -Path $logDir

        $content = Get-Content -LiteralPath $logFile -Raw
        $content | Should -Not -Match "leakedValue123"
        $content | Should -Match "host=dms-mssql"
    }

    It "does not fail when the path does not exist" {
        { Invoke-ArtifactSanitization -Path (Join-Path $TestDrive "does-not-exist") } | Should -Not -Throw
    }
}
