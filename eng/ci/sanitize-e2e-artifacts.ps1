# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
Redacts secrets from E2E diagnostic artifacts (logs, setup/provisioning output, container
diagnostics) before they are uploaded as CI artifacts.

.DESCRIPTION
CI uploads SQL Server / PostgreSQL / DMS / CMS logs and setup output on failure. Those artifacts can
contain connection strings, passwords, client keys/secrets, bearer tokens, and Authorization headers.
This script rewrites matching artifact files in place with the sensitive values replaced by a fixed
redaction marker, while leaving benign diagnostics untouched. Get-SanitizedText is a pure function so
the redaction rules can be unit tested without touching the filesystem.

.PARAMETER Path
File or directory to sanitize in place. When a directory, matching files are sanitized recursively.

.PARAMETER Include
Filename globs to sanitize when -Path is a directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]
    $Path,

    [string[]]
    $Include = @("*.log", "*.txt", "*.json", "*.trx", "*.out", "*.err")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RedactionMarker = "***REDACTED***"

# Ordered redaction rules. Each rule keeps its non-secret capture group(s) and replaces the secret
# value with the marker. Rules are intentionally conservative about non-secret text: they anchor on a
# key name or scheme so ordinary diagnostics (ids, hostnames, ports, timings) are preserved.
$script:RedactionRules = @(
    # Connection-string secrets: password=... / pwd=... in PostgreSQL and SQL Server connection
    # strings. The value is redacted whether it is wrapped - double-quoted ("..."), single-quoted
    # ('...'), or XML-escaped (&quot;...&quot;) as it appears inside a TRX - or bare. Each quoted form
    # consumes ADO.NET-doubled quote pairs ("" / '' / &quot;&quot;) as part of the value so an embedded
    # quote does not terminate the match early and leak the remainder, stopping only at a single
    # (undoubled) closing delimiter. The bare (unquoted) alternative runs to the real ';' terminator
    # (or end of line): commas and spaces are legal inside an unquoted ADO.NET value, so stopping at a
    # comma or space left the remainder of the secret (e.g. Password=Aa1!,tail) in the artifact. The
    # whole matched span is redacted so the enclosed secret is not left behind; a following key/value
    # after the real delimiter is preserved.
    [pscustomobject]@{
        Name        = "connection-string-password"
        Pattern     = "(?i)((?:password|pwd)\s*=\s*)(&quot;(?:(?!&quot;).|&quot;&quot;)*&quot;|""(?:[^""]|"""")*""|'(?:[^']|'')*'|[^;\r\n]+)"
        Replacement = "`${1}$($script:RedactionMarker)"
    },
    # JSON string values for credential-bearing property names.
    [pscustomobject]@{
        Name        = "json-credential"
        Pattern     = "(?i)(""(?:password|secret|client_?secret|client_?key|clientkey|clientsecret|access_?token|refresh_?token|token|api_?key|encryption_?key)""\s*:\s*"")([^""]*)("")"
        Replacement = "`${1}$($script:RedactionMarker)`${3}"
    },
    # Form-encoded / query-string credential parameters.
    [pscustomobject]@{
        Name        = "form-credential"
        Pattern     = "(?i)(\b(?:password|client_secret|secret|access_token|refresh_token|token|api_?key)=)([^&\s;""'\r\n]+)"
        Replacement = "`${1}$($script:RedactionMarker)"
    },
    # Authorization headers (Bearer / Basic).
    [pscustomobject]@{
        Name        = "authorization-header"
        Pattern     = "(?i)(Authorization\s*:\s*(?:Bearer|Basic)\s+)(\S+)"
        Replacement = "`${1}$($script:RedactionMarker)"
    },
    # Bare bearer tokens that appear outside a header (e.g. logged token values).
    [pscustomobject]@{
        Name        = "bearer-token"
        Pattern     = "(?i)(\bBearer\s+)([A-Za-z0-9\-._~+/]+=*)"
        Replacement = "`${1}$($script:RedactionMarker)"
    },
    # Environment-variable-style secrets: any NAME ending in PASSWORD/SECRET/TOKEN/KEY = value.
    [pscustomobject]@{
        Name        = "env-secret"
        Pattern     = "(?im)^(\s*[A-Za-z_][A-Za-z0-9_]*(?:PASSWORD|SECRET|TOKEN|KEY)\s*=\s*)(\S+)$"
        Replacement = "`${1}$($script:RedactionMarker)"
    },
    # Bracketed PowerShell key/value credential output, e.g. build-dms.ps1 CMS-bootstrap logging that
    # renders a dictionary entry as `[ClientSecret, <value>]`. Key-anchored to the credential key set so
    # ordinary bracketed diagnostics ([Id, 123], log levels, timestamps) are preserved. The value is
    # captured non-greedily up to the closing bracket; the whitespace classes tolerate a value that wraps
    # onto following lines (PowerShell console line wrapping) between the comma and the bracket. The key
    # alternation lists compound names before their shorter suffixes so the whole key is matched.
    [pscustomobject]@{
        Name        = "bracketed-key-value-credential"
        Pattern     = "(?i)(\[\s*(?:ClientSecret|ClientKey|AccessToken|RefreshToken|EncryptionKey|ApiKey|Password|Secret|Token)\s*,\s*)([^\]]+?)(\s*\])"
        Replacement = "`${1}$($script:RedactionMarker)`${3}"
    }
)

function Get-SanitizedText {
    <#
    .SYNOPSIS
    Returns the input text with all recognized secrets replaced by the redaction marker.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Text
    )

    $sanitized = $Text
    foreach ($rule in $script:RedactionRules) {
        $sanitized = [regex]::Replace($sanitized, $rule.Pattern, $rule.Replacement)
    }

    return $sanitized
}

function Invoke-ArtifactSanitization {
    <#
    .SYNOPSIS
    Sanitizes matching artifact files in place under the supplied path.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Non-interactive CI utility that rewrites its own diagnostic artifacts.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path,

        [string[]]
        $Include = @("*.log", "*.txt", "*.json", "*.trx", "*.out", "*.err")
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Information "Sanitizer: path '$Path' does not exist; nothing to sanitize." -InformationAction Continue
        return
    }

    $files =
        if (Test-Path -LiteralPath $Path -PathType Container) {
            Get-ChildItem -LiteralPath $Path -Recurse -File -Include $Include
        }
        else {
            @(Get-Item -LiteralPath $Path)
        }

    foreach ($file in $files) {
        $original = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        if ($null -eq $original) {
            continue
        }

        $sanitized = Get-SanitizedText -Text $original
        if ($sanitized -ne $original) {
            Set-Content -LiteralPath $file.FullName -Value $sanitized -NoNewline -Encoding utf8
            Write-Information "Sanitized secrets in artifact: $($file.FullName)" -InformationAction Continue
        }
    }
}

# Execute only when run as a script (not when dot-sourced by tests).
if ($MyInvocation.InvocationName -ne ".") {
    Invoke-ArtifactSanitization -Path $Path -Include $Include
}
