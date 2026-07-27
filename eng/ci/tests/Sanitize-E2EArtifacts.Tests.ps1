# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "sanitize-e2e-artifacts Get-SanitizedText (DMS-1284)" {
    # Two value-class modes. Default (plain text: *.log, *.txt, *.out, *.err) redacts a bare value to
    # its real terminator so no suffix of a secret survives. -PreserveMarkup (*.trx, *.xml) stops the
    # same value before markup, because those artifacts are parsed by the CI test reporter and a
    # redaction that swallowed a closing tag would publish an unparseable document. Tests that assert
    # surviving markup pass -PreserveMarkup; tests that assert complete redaction of a markup-bearing
    # secret do not.
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
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output>connect failed: Server=s;Password=&quot;Aa1!xmlSecretValue&quot;;TrustServerCertificate=true</Output>'

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
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output>Server=s;Password=&quot;XFRAGA; space &quot;&quot;XFRAGB&quot;&quot; XFRAGC&quot;;TrustServerCertificate=true</Output>'

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

    It "redacts a bare (unquoted) connection-string password containing commas through the semicolon terminator" {
        # Commas are legal inside an unquoted ADO.NET value; the bare alternative must run to the real ';'
        # terminator, not stop at the first comma and leak the remainder (e.g. Password=Aa1!,tail).
        $result = Get-SanitizedText -Text "Server=dms-mssql,1433;User Id=sa;Password=Aa1!CFRAGA,CFRAGB,CFRAGC;TrustServerCertificate=true"

        $result | Should -Not -Match "CFRAGA"
        $result | Should -Not -Match "CFRAGB"
        $result | Should -Not -Match "CFRAGC"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "User Id=sa"
        $result | Should -Match "TrustServerCertificate=true"
        # The Server host,port comma belongs to a non-password field and must be preserved.
        $result | Should -Match "Server=dms-mssql,1433"
    }

    It "redacts a bare connection-string password containing internal spaces through the semicolon terminator" {
        $result = Get-SanitizedText -Text "host=h;password=SFRAGX SFRAGY SFRAGZ;database=db"

        $result | Should -Not -Match "SFRAGX"
        $result | Should -Not -Match "SFRAGY"
        $result | Should -Not -Match "SFRAGZ"
        $result | Should -Match "password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "database=db"
    }

    It "redacts a comma-bearing ConnectionStrings__MssqlAdmin secret written to GITHUB_ENV" {
        $result = Get-SanitizedText -Text "ConnectionStrings__MssqlAdmin=Server=localhost,1433;User Id=sa;Password=Aa1!GH,ENVTAIL;TrustServerCertificate=true;"

        $result | Should -Not -Match "ENVTAIL"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "User Id=sa"
        $result | Should -Match "TrustServerCertificate=true"
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

    It "redacts an underscore-prefixed credential name that appears mid-line in a timestamped diagnostic" {
        # A log line prefixes the pair with a timestamp/level and can carry trailing text, so the rule
        # cannot be line-anchored. The form-credential rule cannot cover this shape either: its \b never
        # matches the boundary between '_' and 'secret', both word characters.
        $result = Get-SanitizedText -Text "14:02:03 INFO resolved DMS_CONFIG_IDENTITY_CLIENT_SECRET=Aa1!MidLineSecretValue for the config client"

        $result | Should -Not -Match "Aa1!MidLineSecretValue"
        $result | Should -Match "DMS_CONFIG_IDENTITY_CLIENT_SECRET=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "14:02:03 INFO resolved"
        $result | Should -Match "for the config client"
    }

    # PASSWORD-suffixed names are already covered mid-line by the (unanchored) connection-string rule,
    # whose bare value intentionally runs past spaces to the ';' terminator; these are the suffixes only
    # the env-secret rule covers, and its value stops at whitespace so following prose is preserved.
    It "redacts a mid-line <Name> secret and preserves the text after the value" -ForEach @(
        @{ Name = "DMS_CONFIG_IDENTITY_CLIENT_SECRET" }
        @{ Name = "DMS_CONFIG_DATABASE_ENCRYPTION_KEY" }
        @{ Name = "SOME_SERVICE_ACCESS_TOKEN" }
    ) {
        $result = Get-SanitizedText -Text "starting stack with $Name=PLACEHOLDER_MIDLINE_VALUE and continuing"

        $result | Should -Not -Match "PLACEHOLDER_MIDLINE_VALUE"
        $result | Should -Match "$Name=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "and continuing"
    }

    It "keeps a longer benign name from being partially matched as a credential name" {
        $text = "PGPASSWORDFILE_PATH=/tmp/pgpass MY_KEYSTORE_PATH=/tmp/keystore"

        Get-SanitizedText -Text $text | Should -Be $text
    }

    It "redacts a bare connection-string password inside single-line TRX markup without consuming the closing tag" {
        # The bare (unquoted) value runs to the ';' terminator or end of line. In a single-line TRX the
        # element body is followed immediately by the closing tag, so the value must stop at '<' or the
        # sanitized artifact is malformed XML and the test reporter cannot parse it.
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output><StdErr>login failed for Server=dms-mssql,1433;Password=Aa1!TrxBareSecret</StdErr></Output>'

        $result | Should -Not -Match "Aa1!TrxBareSecret"
        $result | Should -Match "Password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match ([regex]::Escape("</StdErr></Output>"))
    }

    It "redacts a form-encoded credential inside single-line TRX markup without consuming the closing tag" {
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output><StdOut>token request: grant_type=client_credentials&client_secret=Aa1!TrxFormSecret</StdOut></Output>'

        $result | Should -Not -Match "Aa1!TrxFormSecret"
        $result | Should -Match "client_secret=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "grant_type=client_credentials"
        $result | Should -Match ([regex]::Escape("</StdOut></Output>"))
    }

    It "redacts an Authorization header inside single-line TRX markup without consuming the closing tag" {
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output><StdOut>Authorization: Bearer eyJhbGciTrxHeaderToken.payload.signature</StdOut></Output>'

        $result | Should -Not -Match "eyJhbGciTrxHeaderToken"
        $result | Should -Match "Authorization: Bearer \*\*\*REDACTED\*\*\*"
        $result | Should -Match ([regex]::Escape("</StdOut></Output>"))
    }

    It "redacts a secret inside a single-line TRX attribute without consuming the closing attribute quote" {
        $result = Get-SanitizedText -PreserveMarkup -Text '<UnitTestResult testName="It reports MSSQL_SA_PASSWORD=Aa1!TrxAttrSecret" outcome="Failed" />'

        $result | Should -Not -Match "Aa1!TrxAttrSecret"
        $result | Should -Match "MSSQL_SA_PASSWORD=\*\*\*REDACTED\*\*\*"
        $result | Should -Match ([regex]::Escape('" outcome="Failed" />'))
    }

    It "redacts XML-escaped JSON credential properties inside a TRX output block" {
        # A scenario log that echoes a JSON credential body into stdout reaches the TRX with its quotes
        # escaped as &quot;, which the literal-quote JSON rule cannot see, so the value was published.
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output><StdOut>{&quot;clientId&quot;: &quot;svc-1&quot;, &quot;clientSecret&quot;: &quot;XJFRAGA&quot;, &quot;password&quot;: &quot;XJFRAGB&quot;}</StdOut></Output>'

        $result | Should -Not -Match "XJFRAGA"
        $result | Should -Not -Match "XJFRAGB"
        $result | Should -Match ([regex]::Escape("&quot;clientId&quot;: &quot;svc-1&quot;"))
        $result | Should -Match ([regex]::Escape("</StdOut></Output>"))
    }

    It "redacts a JSON credential value containing a JSON-escaped quote without leaking the tail" {
        # JSON escapes an embedded quote as \"; a value class that stops at the first quote character
        # ends the match inside the secret and leaves the remainder in the artifact.
        $result = Get-SanitizedText -Text '{ "clientSecret": "JFRAGA\"JFRAGB", "tenant": "Tenant_255901" }'

        $result | Should -Not -Match "JFRAGA"
        $result | Should -Not -Match "JFRAGB"
        $result | Should -Match ([regex]::Escape('"tenant": "Tenant_255901"'))
    }

    It "redacts an XML-escaped JSON credential value containing a JSON-escaped quote without leaking the tail" {
        $result = Get-SanitizedText -PreserveMarkup -Text '<Output>{&quot;clientSecret&quot;: &quot;XEFRAGA\&quot;XEFRAGB&quot;, &quot;tenant&quot;: &quot;Tenant_255901&quot;}</Output>'

        $result | Should -Not -Match "XEFRAGA"
        $result | Should -Not -Match "XEFRAGB"
        $result | Should -Match ([regex]::Escape("&quot;tenant&quot;: &quot;Tenant_255901&quot;"))
        $result | Should -Match ([regex]::Escape("</Output>"))
    }

    # Plain-text artifacts (*.log, *.txt, *.out, *.err) are not parsed as markup, so a value carrying
    # '<' or '>' must still be redacted to its real terminator. Stopping at the markup character there
    # ends the match inside the secret and publishes its suffix.
    It "redacts a bare connection-string password containing a markup character in a plain-text artifact" {
        $result = Get-SanitizedText -Text "host=h;password=Aa1!<PFRAGA<PFRAGB;database=db"

        $result | Should -Not -Match "PFRAGA"
        $result | Should -Not -Match "PFRAGB"
        $result | Should -Match "password=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "database=db"
    }

    It "redacts a form-encoded credential containing markup characters in a plain-text artifact" {
        $result = Get-SanitizedText -Text "grant_type=client_credentials&client_id=CMSReadOnlyAccess&client_secret=Aa1!<FFRAGA>FFRAGB"

        $result | Should -Not -Match "FFRAGA"
        $result | Should -Not -Match "FFRAGB"
        $result | Should -Match "client_secret=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "client_id=CMSReadOnlyAccess"
    }

    It "redacts an Authorization header token containing a markup character in a plain-text artifact" {
        $result = Get-SanitizedText -Text "Authorization: Bearer eyJhbGci<HFRAGA<HFRAGB"

        $result | Should -Not -Match "HFRAGA"
        $result | Should -Not -Match "HFRAGB"
        $result | Should -Match "Authorization: Bearer \*\*\*REDACTED\*\*\*"
    }

    It "redacts an environment-style secret containing markup characters in a plain-text artifact" {
        # A SECRET-suffixed name, which only the env-secret rule covers: its value stops at whitespace,
        # so the prose after it is preserved while the markup characters inside the value are not a
        # terminator. (A PASSWORD-suffixed name is instead claimed by the connection-string rule, whose
        # bare value deliberately runs past spaces to the ';' terminator.)
        $result = Get-SanitizedText -Text "starting stack with DMS_CONFIG_IDENTITY_CLIENT_SECRET=Aa1!<EFRAGA>EFRAGB and continuing"

        $result | Should -Not -Match "EFRAGA"
        $result | Should -Not -Match "EFRAGB"
        $result | Should -Match "DMS_CONFIG_IDENTITY_CLIENT_SECRET=\*\*\*REDACTED\*\*\*"
        $result | Should -Match "and continuing"
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

    It "keeps a .trx artifact parseable by stopping the redaction before the closing tag" {
        $trxFile = Join-Path $TestDrive "results.trx"
        Set-Content -LiteralPath $trxFile -Value '<Output><StdErr>login failed for Server=s;Password=Aa1!TrxFileSecret</StdErr></Output>' -NoNewline

        Invoke-ArtifactSanitization -Path $trxFile

        $content = Get-Content -LiteralPath $trxFile -Raw
        $content | Should -Not -Match "Aa1!TrxFileSecret"
        { [xml]$content } | Should -Not -Throw
    }

    It "redacts a markup-bearing secret in a plain-text log through its terminator" {
        $logFile = Join-Path $TestDrive "markup-bearing-secret.log"
        Set-Content -LiteralPath $logFile -Value "connecting Password=Aa1!<LFRAGA<LFRAGB;host=dms-mssql" -NoNewline

        Invoke-ArtifactSanitization -Path $logFile

        $content = Get-Content -LiteralPath $logFile -Raw
        $content | Should -Not -Match "LFRAGA"
        $content | Should -Not -Match "LFRAGB"
        $content | Should -Match "host=dms-mssql"
    }

    It "does not fail when the path does not exist" {
        { Invoke-ArtifactSanitization -Path (Join-Path $TestDrive "does-not-exist") } | Should -Not -Throw
    }
}
