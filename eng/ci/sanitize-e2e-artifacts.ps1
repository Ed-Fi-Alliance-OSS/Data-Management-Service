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

Redaction breadth varies by artifact type. A TRX or XML file must still parse for the CI test reporter
after sanitization, so its values stop before markup; every other artifact is plain text, where a value
runs to its real terminator so no suffix of a secret survives.

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

# Placeholder inside a rule's value character class, replaced with that rule's MarkupExclusions for a
# markup-bearing artifact and with nothing for a plain-text one. Every class it appears in carries at
# least one other member, so the empty (plain-text) expansion is still a valid class.
$script:MarkupExclusionToken = "{markup}"

# Ordered redaction rules. Each rule keeps its non-secret capture group(s) and replaces the secret
# value with the marker. Rules are intentionally conservative about non-secret text: they anchor on a
# key name or scheme so ordinary diagnostics (ids, hostnames, ports, timings) are preserved.
#
# MarkupExclusions holds the characters a rule's value must additionally stop at in a TRX/XML artifact
# (see Get-SanitizedText -PreserveMarkup), because a redaction that consumed a closing tag or an
# attribute's closing quote would leave the document unparseable for the CI test reporter. Inside
# well-formed XML those exclusions cost no coverage: a literal '<' cannot appear in element content or
# an attribute value (it arrives as &lt;), and a '"'-bearing connection-string value is quoted and so
# matched by a quoted alternative. In a plain-text artifact the same exclusions would end the match
# inside a secret that happens to contain '<' or '"' and publish its suffix, so the token expands to
# nothing there and each value runs to its real terminator.
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
    # The whitespace around '=' is horizontal only: a `\s*` span crosses a newline, so a key with an empty
    # value at end of line consumed the next line's first token as its value and replaced it with the
    # marker - over-redaction that corrupts the following line rather than leaking anything.
    [pscustomobject]@{
        Name             = "connection-string-password"
        Pattern          = "(?i)((?:password|pwd)[ \t]*=[ \t]*)(&quot;(?:(?!&quot;).|&quot;&quot;)*&quot;|""(?:[^""]|"""")*""|'(?:[^']|'')*'|[^;{markup}\r\n]+)"
        MarkupExclusions = '"<'
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # JSON string values for credential-bearing property names. A JSON-escaped quote (\") is consumed
    # as part of the value rather than treated as the closing delimiter, so an embedded quote cannot
    # end the match inside the secret and leave its remainder in the artifact.
    [pscustomobject]@{
        Name             = "json-credential"
        Pattern          = "(?i)(""(?:password|secret|client_?secret|client_?key|clientkey|clientsecret|access_?token|refresh_?token|token|api_?key|encryption_?key)""\s*:\s*"")((?:\\.|[^""\\{markup}])*)("")"
        MarkupExclusions = '<'
        Replacement      = "`${1}$($script:RedactionMarker)`${3}"
    },
    # The same JSON body with its quotes XML-escaped, which is how a scenario log that echoes a
    # credential response arrives inside a TRX <Output> block. The literal-quote rule above cannot see
    # this shape, so without this alternative the value was published verbatim.
    [pscustomobject]@{
        Name             = "json-credential-xml-escaped"
        Pattern          = "(?i)(&quot;(?:password|secret|client_?secret|client_?key|clientkey|clientsecret|access_?token|refresh_?token|token|api_?key|encryption_?key)&quot;\s*:\s*&quot;)((?:\\&quot;|(?!&quot;)[^{markup}\r\n])*)(&quot;)"
        MarkupExclusions = '<'
        Replacement      = "`${1}$($script:RedactionMarker)`${3}"
    },
    # Form-encoded / query-string credential parameters.
    [pscustomobject]@{
        Name             = "form-credential"
        Pattern          = "(?i)(\b(?:password|client_secret|secret|access_token|refresh_token|token|api_?key)=)([^&\s;""'{markup}\r\n]+)"
        MarkupExclusions = '<>'
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # Authorization headers (Bearer / Basic). The token carries no whitespace, so the value ends at the
    # first space rather than taking the rest of the line.
    [pscustomobject]@{
        Name             = "authorization-header"
        Pattern          = "(?i)(Authorization\s*:\s*(?:Bearer|Basic)\s+)([^\s{markup}\r\n]+)"
        MarkupExclusions = '"<>'
        Replacement      = "`${1}$($script:RedactionMarker)"
    },
    # Bare bearer tokens that appear outside a header (e.g. logged token values). The value is a
    # positive base64url class, which already cannot reach markup.
    [pscustomobject]@{
        Name             = "bearer-token"
        Pattern          = "(?i)(\bBearer\s+)([A-Za-z0-9\-._~+/]+=*)"
        MarkupExclusions = ''
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
    # spaces survives, and in a markup artifact (where the class also excludes '"') a quoted value does
    # not match at all. PASSWORD-suffixed names are also reached by the connection-string rule's quoted
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
        Pattern          = "(?im)(^|[^A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*(?:PASSWORD|SECRET|TOKEN|KEY)[ \t]*=[ \t]*)(&quot;(?:(?!&quot;)[^\r\n]|&quot;&quot;)*&quot;|""(?:[^""\r\n]|"""")*""|'(?:[^'\r\n]|'')*'|[^\s{markup}\r\n]+)"
        MarkupExclusions = '"<>'
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
        Pattern          = "(?i)(\[\s*(?:ClientSecret|ClientKey|AccessToken|RefreshToken|EncryptionKey|ApiKey|Password|Secret|Token)\s*,\s*)([^\]{markup}]+?)(\s*\])"
        MarkupExclusions = '<'
        Replacement      = "`${1}$($script:RedactionMarker)`${3}"
    }
)

function Get-SanitizedText {
    <#
    .SYNOPSIS
    Returns the input text with all recognized secrets replaced by the redaction marker.

    .PARAMETER PreserveMarkup
    Treat the text as markup the CI test reporter must still be able to parse (a TRX or XML artifact):
    each value stops before markup instead of running to its terminator, so a redaction cannot consume
    a closing tag or an attribute's closing quote. Off by default, which is the correct mode for
    plain-text artifacts: there the exclusions would end a match inside a secret containing '<' or '"'
    and publish its suffix, and no reporter parses the file.
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

    $sanitized = $Text
    foreach ($rule in $script:RedactionRules) {
        $markupExclusions = if ($PreserveMarkup) { $rule.MarkupExclusions } else { "" }
        $pattern = $rule.Pattern.Replace($script:MarkupExclusionToken, $markupExclusions)
        $sanitized = [regex]::Replace($sanitized, $pattern, $rule.Replacement)
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

        # A TRX or XML artifact is parsed by the CI test reporter, so its redactions must stop before
        # markup; every other artifact is plain text and gets the value classes that run to the real
        # terminator, which is what keeps the suffix of a '<'-bearing secret out of the upload.
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
