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

TRX and XML values are decoded and sanitized separately from markup, then serialized as XML so the CI
test reporter can still parse them. Other artifacts are sanitized as plain text.

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
    # ('...'), or XML-escaped (&quot;...&quot;) in a plain-text diagnostic - or bare. Each quoted form
    # consumes ADO.NET-doubled quote pairs ("" / '' / &quot;&quot;) as part of the value so an embedded
    # quote does not terminate the match early and leak the remainder, stopping only at a single
    # (undoubled) closing delimiter. The bare (unquoted) alternative runs to the real ';' terminator
    # (or end of line): commas and spaces are legal inside an unquoted ADO.NET value, so stopping at a
    # comma or space left the remainder of the secret (e.g. Password=Aa1!,tail) in the artifact.
    # The whole matched span is redacted so the enclosed secret is not left behind; a following key/value
    # after the real delimiter is preserved.
    # The whitespace around '=' is horizontal only: a `\s*` span crosses a newline, so a key with an empty
    # value at end of line consumed the next line's first token as its value and replaced it with the
    # marker - over-redaction that corrupts the following line rather than leaking anything.
    [pscustomobject]@{
        Name             = "connection-string-password"
        Pattern          = "(?i)((?:password|pwd)[ \t]*=[ \t]*)(&quot;(?:(?!&quot;).|&quot;&quot;)*&quot;|""(?:[^""]|"""")*""|'(?:[^']|'')*'|[^;\r\n]+)"
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # JSON string values for credential-bearing property names. A JSON-escaped quote (\") is consumed
    # as part of the value rather than treated as the closing delimiter, so an embedded quote cannot
    # end the match inside the secret and leave its remainder in the artifact.
    [pscustomobject]@{
        Name             = "json-credential"
        Pattern          = "(?i)(""(?:password|secret|client_?secret|client_?key|clientkey|clientsecret|access_?token|refresh_?token|token|api_?key|encryption_?key)""\s*:\s*"")((?:\\.|[^""\\])*)("")"
        Replacement      = "`${1}$($script:RedactionMarker)`${3}"
    },
    # JSON echoed with XML-escaped quotes into a plain-text diagnostic. Parsed XML values use the
    # literal-quote rule above, but logs containing encoded JSON still need this alternative.
    [pscustomobject]@{
        Name             = "json-credential-xml-escaped"
        Pattern          = "(?i)(&quot;(?:password|secret|client_?secret|client_?key|clientkey|clientsecret|access_?token|refresh_?token|token|api_?key|encryption_?key)&quot;\s*:\s*&quot;)((?:\\&quot;|(?!&quot;)[^\r\n])*)(&quot;)"
        Replacement      = "`${1}$($script:RedactionMarker)`${3}"
    },
    # Form-encoded / query-string credential parameters.
    [pscustomobject]@{
        Name             = "form-credential"
        Pattern          = "(?i)(\b(?:password|client_secret|secret|access_token|refresh_token|token|api_?key)=)([^&\s;""'\r\n]+)"
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # Authorization headers (Bearer / Basic). The token carries no whitespace, so the value ends at the
    # first space rather than taking the rest of the line.
    [pscustomobject]@{
        Name             = "authorization-header"
        Pattern          = "(?i)(Authorization\s*:\s*(?:Bearer|Basic)\s+)([^\s\r\n]+)"
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # Bare bearer tokens that appear outside a header (e.g. logged token values). The value is a
    # positive base64url class, which already cannot reach markup.
    [pscustomobject]@{
        Name             = "bearer-token"
        Pattern          = "(?i)(\bBearer\s+)([A-Za-z0-9\-._~+/]+=*)"
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # Environment-variable-style secrets: any NAME ending in PASSWORD/SECRET/TOKEN/KEY = value.
    # Deliberately not line-anchored: the same NAME=value pair appears mid-line in timestamped and
    # prefixed diagnostics (`14:02:03 INFO DMS_CONFIG_IDENTITY_CLIENT_SECRET=...`) and with trailing
    # text after the value, neither of which an anchored rule matches. This rule is also what covers
    # underscore-prefixed credential names (`..._CLIENT_SECRET=`): the form-credential rule's \b cannot
    # match a key boundary made of '_', which is a word character. The preceding character is captured
    # rather than consumed so the match cannot start inside a longer name, and the bare value class keeps
    # ';' and ',' (legal in a secret) while stopping at whitespace so following prose is preserved.
    # A quoted value is matched by its own alternative first, because a diagnostic that echoes an env-file
    # line, a `docker inspect` fragment, or a shell command carries the value wrapped in quotes and the
    # bare class stops at whitespace: without these alternatives the tail of a quoted secret containing
    # spaces survives. PASSWORD-suffixed names are also reached by the connection-string rule's quoted
    # alternatives; the SECRET/TOKEN/KEY suffixes are covered only here. Each quoted alternative consumes
    # doubled delimiter pairs ("" / '' / &quot;&quot;) as part of the value, matching what the
    # connection-string rule does: otherwise the alternative ends at the first quote of a doubled pair and
    # publishes the remainder of the secret. Each also stays on one line so an unterminated quote cannot
    # pair with a later line's delimiter and swallow the diagnostics in between - that shape falls through
    # to the bare class, which takes the whole token including the leading quote.
    # As in the connection-string rule, the whitespace around '=' is horizontal only so an empty value at
    # end of line cannot span the newline and consume the next line's key name.
    [pscustomobject]@{
        Name             = "env-secret"
        Pattern          = "(?im)(^|[^A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*(?:PASSWORD|SECRET|TOKEN|KEY)[ \t]*=[ \t]*)(&quot;(?:(?!&quot;)[^\r\n]|&quot;&quot;)*&quot;|""(?:[^""\r\n]|"""")*""|'(?:[^'\r\n]|'')*'|[^\s\r\n]+)"
        Replacement      = "`${1}`${2}$($script:RedactionMarker)"
    },
    # Bracketed PowerShell key/value credential output, e.g. build-dms.ps1 CMS-bootstrap logging that
    # renders a dictionary entry as `[ClientSecret, <value>]`. Key-anchored to the credential key set so
    # ordinary bracketed diagnostics ([Id, 123], log levels, timestamps) are preserved. The value is
    # captured non-greedily up to the closing bracket; the whitespace classes tolerate a value that wraps
    # onto following lines (PowerShell console line wrapping) between the comma and the bracket. The key
    # alternation lists compound names before their shorter suffixes so the whole key is matched.
    [pscustomobject]@{
        Name             = "bracketed-key-value-credential"
        Pattern          = "(?i)(\[\s*(?:ClientSecret|ClientKey|AccessToken|RefreshToken|EncryptionKey|ApiKey|Password|Secret|Token)\s*,\s*)([^\]]+?)(\s*\])"
        Replacement      = "`${1}$($script:RedactionMarker)`${3}"
    }
)

function Get-SanitizedText {
    <#
    .SYNOPSIS
    Returns the input text with all recognized secrets replaced by the redaction marker.

    .PARAMETER PreserveMarkup
    Parse TRX/XML with DTDs prohibited, sanitize decoded values, and serialize with markup preserved.
    Malformed XML fails sanitization, preventing publication. Off by default for plain-text artifacts.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Text,

        [switch]
        $PreserveMarkup
    )

    if ($PreserveMarkup) {
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $inputReader = [System.IO.StringReader]::new($Text)
        $reader = [System.Xml.XmlReader]::Create($inputReader, $settings)
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.PreserveWhitespace = $true
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
            $inputReader.Dispose()
        }

        # XPath navigator values combine contiguous text/CDATA/whitespace nodes and decode entities.
        # Snapshot the selected nodes before replacing a whole text sequence so later values are visited.
        $textNodeTypes = @(
            [System.Xml.XmlNodeType]::Text,
            [System.Xml.XmlNodeType]::CDATA,
            [System.Xml.XmlNodeType]::Whitespace,
            [System.Xml.XmlNodeType]::SignificantWhitespace
        )
        $changed = $false
        foreach ($node in @($document.SelectNodes('//text() | //@* | //comment() | //processing-instruction()'))) {
            $original = $node.CreateNavigator().Value
            $value = Get-SanitizedText -Text $original
            if ($value -cne $original) {
                if ($node.NodeType -in $textNodeTypes) {
                    $parent = $node.ParentNode
                    # A text node safely serializes even a CDATA terminator joined across fragments.
                    $null = $parent.InsertBefore($document.CreateTextNode($value), $node)
                    $fragment = $node
                    do {
                        $next = $fragment.NextSibling
                        $null = $parent.RemoveChild($fragment)
                        $fragment = $next
                    } while ($null -ne $fragment -and $fragment.NodeType -in $textNodeTypes)
                }
                else {
                    $node.Value = $value
                }
                $changed = $true
            }
        }
        if ($changed) { return $document.OuterXml }
        return $Text
    }

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

        # TRX/XML redactions operate on decoded values, preserving the reporter's markup.
        $preserveMarkup = $file.Extension -in @(".trx", ".xml")

        $sanitized = Get-SanitizedText -Text $original -PreserveMarkup:$preserveMarkup
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
