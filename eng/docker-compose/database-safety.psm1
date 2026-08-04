# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Shared database-name safety guards for E2E provisioning.
.DESCRIPTION
    The repository's safe-name and dedicated-E2E database rules, extracted so both
    provision-e2e-database.ps1 and the Instance Management E2E orchestration validate route-context
    database names with the same logic before any DROP/CREATE mutation. Assert-SafeDatabaseName rejects
    unsupported characters and reserved PostgreSQL/SQL Server system databases; Assert-E2EDatabaseIsDedicated
    additionally rejects names that collide with the primary/CMS databases (by name or by the database
    embedded in the admin/CMS connection strings).
#>

Set-StrictMode -Version Latest

function ConvertFrom-ComposeEnvironmentValue {
    <#
    .SYNOPSIS
        Returns the effective value of a Docker Compose env-file entry, stripping surrounding quotes
        and inline comments the way Docker Compose does.
    #>
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return $Value
    }

    if ([string]::IsNullOrWhiteSpace($Value)) {
        # An unquoted value is trimmed, so a whitespace-only value is empty. Verified live on both
        # platforms: PASSWORD=<spaces> renders as empty, while "<spaces>" and '<spaces>' keep their
        # spaces (a quoted value is not whitespace-only as raw text, so it never reaches here).
        # Returning the spaces verbatim let a value pass preflight that Compose renders differently.
        return ""
    }

    $trimmedValue = $Value.Trim()
    $firstCharacter = $trimmedValue[0]

    if ($firstCharacter -in @("'", '"')) {
        $closingQuoteIndex = -1
        $escaped = $false

        for ($index = 1; $index -lt $trimmedValue.Length; $index++) {
            $character = $trimmedValue[$index]

            if ($character -eq "\" -and -not $escaped) {
                $escaped = $true
                continue
            }

            if ($character -eq $firstCharacter -and -not $escaped) {
                $closingQuoteIndex = $index
                break
            }

            $escaped = $false
        }

        if ($closingQuoteIndex -gt 0) {
            $trailingContent = $trimmedValue.Substring($closingQuoteIndex + 1).Trim()
            if ([string]::IsNullOrEmpty($trailingContent) -or $trailingContent.StartsWith("#")) {
                $unquotedValue = $trimmedValue.Substring(1, $closingQuoteIndex - 1)
                if ($firstCharacter -eq "'") {
                    return $unquotedValue.Replace("\'", "'")
                }

                return $unquotedValue.Replace('\"', '"').Replace('\\', '\')
            }
        }
    }

    # Docker Compose treats a # preceded by whitespace as an inline comment for an unquoted
    # value. A # without leading whitespace remains part of the value.
    return ($trimmedValue -replace '[ \t]+#.*$', '').Trim()
}

function Resolve-ComposeEnvRawValue {
    <#
    .SYNOPSIS
        Applies Docker Compose value semantics to a single raw env-file map value: strips surrounding
        quotes and inline comments (ConvertFrom-ComposeEnvironmentValue), then resolves ${VAR}/$VAR
        references EXCEPT when the raw value is single-quoted, which Compose treats as literal (no
        interpolation). Single place that decides convert-then-resolve vs. literal, so the rule applies
        identically to the top-level requested key and to every value reached through a ${VAR} chain.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [AllowEmptyString()][string]$RawValue,
        [int]$Depth = 0,
        [scriptblock]$NameLookup,
        [System.Collections.Generic.List[string]]$ReferenceTrace
    )

    $converted = ConvertFrom-ComposeEnvironmentValue -Value $RawValue

    if ($RawValue.TrimStart().StartsWith("'")) {
        # Single-quoted: Compose keeps the value literal (quotes stripped), no ${VAR} interpolation.
        return $converted
    }

    return Resolve-ComposeEnvReference `
        -EnvironmentValues $EnvironmentValues `
        -Value $converted `
        -Depth $Depth `
        -NameLookup $NameLookup `
        -ReferenceTrace $ReferenceTrace
}

function Resolve-ComposeEnvReference {
    <#
    .SYNOPSIS
        Resolves ${VAR}/$VAR references in a Compose-converted value against the other environment
        values, following Docker Compose interpolation: a literal '$' is written '$$' and preserved;
        $NAME and ${NAME} expand (recursively, bounded); the braced form supports Compose's full
        operator set - ${NAME:-default}, ${NAME-default}, ${NAME:?error}, ${NAME?error},
        ${NAME:+replacement}, ${NAME+replacement} - including nested references inside the
        default/replacement word (e.g. ${A:-${B}}); an unset plain reference expands to empty; and a
        value set in the process/shell environment wins over the env file. A referenced value is
        resolved through Resolve-ComposeEnvRawValue so its own quoting (single-quote literal)
        semantics hold. Operator semantics were confirmed against a real `docker compose config`
        render: ':-' substitutes when unset OR empty, '-' only when unset; ':+' substitutes when set
        AND non-empty, '+' whenever set (even empty); the error forms fail interpolation, surfaced
        here as a thrown error.

    .PARAMETER NameLookup
        Optional. When supplied, every variable NAME is resolved by invoking this scriptblock with the
        name instead of consulting the ambient environment and the EnvironmentValues map. The value it
        returns is used VERBATIM as a terminal literal: it is not resolved again, and its '$'
        characters are not reinterpreted. That is what Docker Compose does for a value it has already
        resolved - verified live: with NAME=secret and A=$$'{NAME}', a later B=${A} renders the literal
        ${NAME}, not "secret", and the same holds for a single-quoted source value. Returning $null
        means "unset", which is distinct from returning "" (set-but-empty); the ':-' vs '-' and ':+'
        vs '+' operators key on exactly that difference. Omit this parameter and every existing
        caller keeps today's behavior, where map entries are RAW dotenv values resolved recursively.

    .PARAMETER ReferenceTrace
        Optional. Receives the name of every variable actually evaluated during this resolution, in
        evaluation order. Because operator words are resolved lazily (only in the branch that fires),
        this is the set of names the value genuinely depends on GIVEN the current environment state -
        something no purely lexical scan can determine, since ${A:-${B}} depends on B only when A is
        unset or empty and ${A:+${B}} has the opposite condition.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [AllowEmptyString()][string]$Value,
        [int]$Depth = 0,
        [scriptblock]$NameLookup,
        [System.Collections.Generic.List[string]]$ReferenceTrace
    )

    if ([string]::IsNullOrEmpty($Value) -or $Value.IndexOf('$') -lt 0 -or $Depth -ge 8) {
        return $Value
    }

    # Protect Compose's literal-'$' escape ('$$') before resolving any reference, then restore it.
    $placeholder = [char]0x1
    $working = $Value.Replace('$$', $placeholder)

    $working = Resolve-ComposeInterpolatedText `
        -EnvironmentValues $EnvironmentValues `
        -Text $working `
        -Depth $Depth `
        -NameLookup $NameLookup `
        -ReferenceTrace $ReferenceTrace

    return $working.Replace($placeholder, '$')
}

function Resolve-ComposeNamedReference {
    <#
    .SYNOPSIS
        Resolves a single variable NAME with Compose precedence: the process/shell environment wins,
        then the env-file map (whose value is itself resolved through Resolve-ComposeEnvRawValue so
        quoting and nested references hold), then $null for "unset". Distinguishing $null (unset)
        from "" (set-but-empty) is what the ':-' vs '-' and ':+' vs '+' operators key on.

        This is the single place a NAME becomes a value, so it is also the single place that records
        the reference trace and honors a caller-supplied terminal-value lookup.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)] [string]$Name,
        [int]$Depth = 0,
        [scriptblock]$NameLookup,
        [System.Collections.Generic.List[string]]$ReferenceTrace
    )

    if ($null -ne $ReferenceTrace) { $ReferenceTrace.Add($Name) }

    if ($null -ne $NameLookup) {
        # Terminal lookup: the caller owns precedence, and whatever it returns is already resolved.
        # No recursion and no re-interpretation of '$' - see the NameLookup note on
        # Resolve-ComposeEnvReference.
        $looked = & $NameLookup $Name
        if ($null -eq $looked) { return $null }
        return [string]$looked
    }

    $ambient = [System.Environment]::GetEnvironmentVariable($Name)
    if ($null -ne $ambient) { return $ambient }
    if ($null -ne $EnvironmentValues -and $EnvironmentValues.ContainsKey($Name)) {
        return Resolve-ComposeEnvRawValue -EnvironmentValues $EnvironmentValues -RawValue ([string]$EnvironmentValues[$Name]) -Depth ($Depth + 1)
    }
    return $null
}

function Resolve-ComposeInterpolatedText {
    <#
    .SYNOPSIS
        Scanner behind Resolve-ComposeEnvReference. Operates on placeholder-protected text ('$$'
        already replaced), so recursive word resolution re-enters here rather than the public
        function and the single final restore stays correct. A regex cannot express the braced
        form's nesting (${A:-${B}}), hence a hand-rolled scan matching compose-go's matching-brace
        behavior: '${' opens a nesting level, '}' closes the innermost one.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [AllowEmptyString()][string]$Text,
        [int]$Depth = 0,
        [scriptblock]$NameLookup,
        [System.Collections.Generic.List[string]]$ReferenceTrace
    )

    if ([string]::IsNullOrEmpty($Text) -or $Text.IndexOf('$') -lt 0 -or $Depth -ge 8) {
        return $Text
    }

    $builder = [System.Text.StringBuilder]::new()
    $i = 0
    while ($i -lt $Text.Length) {
        $character = $Text[$i]
        if ($character -ne '$' -or $i + 1 -ge $Text.Length) {
            [void]$builder.Append($character)
            $i++
            continue
        }

        $next = $Text[$i + 1]

        if ($next -eq '{') {
            # Find the matching close brace. Compose pairs EVERY '{' with a '}' during matching, not
            # only '${' opens - verified live: ${PA:-pre$$'{PD}'post} keeps the escaped reference
            # intact as literal pre${PD}post, and ${PA:-{x}} yields literal {x}. Counting only
            # '$'-prefixed opens would let the escaped reference's '}' close the outer expression
            # early and corrupt the resolved value.
            $scan = $i + 2
            $nesting = 1
            while ($scan -lt $Text.Length -and $nesting -gt 0) {
                if ($Text[$scan] -eq '{') { $nesting++ }
                elseif ($Text[$scan] -eq '}') { $nesting--; if ($nesting -eq 0) { break } }
                $scan++
            }
            if ($nesting -ne 0) {
                # Unterminated ${...: leave the rest literal, matching the old leniency.
                [void]$builder.Append($Text.Substring($i))
                break
            }

            $inner = $Text.Substring($i + 2, $scan - ($i + 2))
            [void]$builder.Append((Resolve-ComposeBracedReference `
                -EnvironmentValues $EnvironmentValues `
                -Inner $inner `
                -Depth $Depth `
                -NameLookup $NameLookup `
                -ReferenceTrace $ReferenceTrace))
            $i = $scan + 1
            continue
        }

        if ($next -match '[A-Za-z_]') {
            # Bare $NAME form: no operators possible.
            $scan = $i + 1
            while ($scan -lt $Text.Length -and $Text[$scan] -match '[A-Za-z0-9_]') { $scan++ }
            $name = $Text.Substring($i + 1, $scan - ($i + 1))
            $resolved = Resolve-ComposeNamedReference `
                -EnvironmentValues $EnvironmentValues `
                -Name $name `
                -Depth $Depth `
                -NameLookup $NameLookup `
                -ReferenceTrace $ReferenceTrace
            [void]$builder.Append([string]($resolved ?? ""))
            $i = $scan
            continue
        }

        [void]$builder.Append($character)
        $i++
    }

    return $builder.ToString()
}

function Resolve-ComposeBracedReference {
    <#
    .SYNOPSIS
        Resolves the inner content of one ${...} occurrence: NAME alone, or NAME followed by one of
        Compose's six operators and a word. The word is itself interpolated - lazily, only in the
        branch that actually uses it, matching compose-go. Content that does not parse as a valid
        NAME[op word] form is left literal (the pre-existing leniency for non-reference text).
    #>
    param(
        [hashtable]$EnvironmentValues,
        [AllowEmptyString()][string]$Inner,
        [int]$Depth = 0,
        [scriptblock]$NameLookup,
        [System.Collections.Generic.List[string]]$ReferenceTrace
    )

    $nameMatch = [regex]::Match($Inner, '^[A-Za-z_][A-Za-z0-9_]*')
    if (-not $nameMatch.Success) {
        return '${' + $Inner + '}'
    }

    $name = $nameMatch.Value
    $rest = $Inner.Substring($name.Length)

    if ($rest.Length -eq 0) {
        $resolved = Resolve-ComposeNamedReference `
            -EnvironmentValues $EnvironmentValues `
            -Name $name `
            -Depth $Depth `
            -NameLookup $NameLookup `
            -ReferenceTrace $ReferenceTrace
        return [string]($resolved ?? "")
    }

    $operator =
        if ($rest.StartsWith(':-') -or $rest.StartsWith(':?') -or $rest.StartsWith(':+')) { $rest.Substring(0, 2) }
        elseif ($rest[0] -eq '-' -or $rest[0] -eq '?' -or $rest[0] -eq '+') { $rest.Substring(0, 1) }
        else { $null }

    if ($null -eq $operator) {
        return '${' + $Inner + '}'
    }

    $word = $rest.Substring($operator.Length)
    $value = Resolve-ComposeNamedReference `
        -EnvironmentValues $EnvironmentValues `
        -Name $name `
        -Depth $Depth `
        -NameLookup $NameLookup `
        -ReferenceTrace $ReferenceTrace

    # The word is resolved only inside the branch that uses it. Compose evaluates an operator word
    # only when the operator fires - verified live: ${SET:-${MISSING:?boom}} renders the set value
    # with no error. That laziness is also why ReferenceTrace reports genuine, state-dependent
    # dependencies rather than every name that merely appears in the text. The call is repeated per
    # branch rather than hoisted into a closure: a closure created here loses this module's session
    # state, so the module-private resolver would not be resolvable when it is finally invoked.
    switch ($operator) {
        ':-' {
            if ($null -eq $value -or $value -eq '') {
                return Resolve-ComposeInterpolatedText -EnvironmentValues $EnvironmentValues -Text $word -Depth ($Depth + 1) -NameLookup $NameLookup -ReferenceTrace $ReferenceTrace
            }
            return $value
        }
        '-' {
            if ($null -eq $value) {
                return Resolve-ComposeInterpolatedText -EnvironmentValues $EnvironmentValues -Text $word -Depth ($Depth + 1) -NameLookup $NameLookup -ReferenceTrace $ReferenceTrace
            }
            return $value
        }
        ':+' {
            if ($null -ne $value -and $value -ne '') {
                return Resolve-ComposeInterpolatedText -EnvironmentValues $EnvironmentValues -Text $word -Depth ($Depth + 1) -NameLookup $NameLookup -ReferenceTrace $ReferenceTrace
            }
            return ""
        }
        '+' {
            if ($null -ne $value) {
                return Resolve-ComposeInterpolatedText -EnvironmentValues $EnvironmentValues -Text $word -Depth ($Depth + 1) -NameLookup $NameLookup -ReferenceTrace $ReferenceTrace
            }
            return ""
        }
        ':?' {
            if ($null -eq $value -or $value -eq '') {
                $message = Resolve-ComposeInterpolatedText -EnvironmentValues $EnvironmentValues -Text $word -Depth ($Depth + 1) -NameLookup $NameLookup -ReferenceTrace $ReferenceTrace
                throw "Docker Compose interpolation error: required variable '$name' is missing a value: $message"
            }
            return $value
        }
        '?' {
            if ($null -eq $value) {
                $message = Resolve-ComposeInterpolatedText -EnvironmentValues $EnvironmentValues -Text $word -Depth ($Depth + 1) -NameLookup $NameLookup -ReferenceTrace $ReferenceTrace
                throw "Docker Compose interpolation error: required variable '$name' is missing a value: $message"
            }
            return $value
        }
    }
}

function Get-DotenvAssignment {
    <#
    .SYNOPSIS
        Parses one env-file line as a Docker Compose assignment, or returns $null when the line is a
        comment, blank, or not an assignment.

    .DESCRIPTION
        The single assignment grammar for this module. Every site that detects, replaces, or moves an
        env-file line must use it, because a detection grammar wider than the write grammar silently
        turns a repair into a no-op.

        The accepted shape was confirmed against real `docker compose config` renders: leading
        whitespace is allowed; an `export ` prefix is accepted and stripped; whitespace is allowed on
        both sides of '='; the key is trimmed; and an empty value is a valid assignment. Value
        trimming, quote handling, and inline-comment stripping are NOT done here - they belong to
        ConvertFrom-ComposeEnvironmentValue, which the resolver already applies - so RawValue is the
        verbatim text after '=' and callers keep the ability to rewrite a line without disturbing its
        authored quoting.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Line
    )

    $match = [regex]::Match($Line, '^[ \t]*(?:export[ \t]+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=(?<value>.*)$')
    if (-not $match.Success) { return $null }

    return [pscustomobject]@{
        Key      = $match.Groups['key'].Value
        RawValue = $match.Groups['value'].Value
        Text     = $Line
    }
}

function Test-DotenvAssignmentLine {
    <#
    .SYNOPSIS
        True when the line assigns the given key, using the shared assignment grammar.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Line,
        [Parameter(Mandatory)] [string]$Key
    )

    # Ordinal: a dotenv identifier is case-sensitive on the Linux CI and runtime path, and PowerShell's
    # -eq is case-insensitive. A case-insensitive match here would let a lowercase decoy declaration be
    # mistaken for the uppercase key during replacement or movement.
    $assignment = Get-DotenvAssignment -Line $Line
    return ($null -ne $assignment -and [string]::Equals($assignment.Key, $Key, [System.StringComparison]::Ordinal))
}

function Resolve-DotenvFileSequentially {
    <#
    .SYNOPSIS
        Resolves an env file the way Docker Compose actually resolves an --env-file: line by line, in
        declaration order, so a reference sees only what precedes it.

    .DESCRIPTION
        Docker Compose does not resolve an env file against its own finished contents. It walks the
        file, and each value is resolved against what is in effect at that point. Ground truth captured
        from real `docker compose config` renders:

          - a reference resolves to the ambient value if the name is set in the process environment,
            else to the most recent PRECEDING declaration in the same file, else unset;
          - the final environment the compose file interpolates against keeps the LAST declaration of
            each key (ambient still winning), so a duplicated key can hold two different effective
            values in one run - an earlier one frozen into intervening lines and the last one seen by
            the compose file;
          - a value that has been resolved is terminal: referencing it yields that literal verbatim,
            with no further interpolation and no re-interpretation of '$'.

        A hashtable-based resolution cannot express any of that, which is why validation built on one
        can approve a file Compose renders differently.

    .OUTPUTS
        A PSCustomObject with:
          Declarations - ordered list of every assignment, each carrying Key, RawValue, LineIndex,
                         ResolvedValue (the value frozen at that line) and References (the names
                         actually evaluated while resolving it, in evaluation order).
          Effective    - hashtable of the final effective value per key: ambient if set, else the last
                         declaration's resolved value. This is what the compose file sees.
          DuplicateKeys- keys declared more than once, in first-declaration order.
    #>
    param(
        [Parameter(Mandatory, ParameterSetName = 'Path')] [string]$Path,
        # AllowEmptyString is required in ADDITION to AllowEmptyCollection: a mandatory [string[]]
        # rejects empty-string ELEMENTS, not merely an empty array, so a blank separator line - which
        # every composed env file contains - fails parameter binding without it.
        [Parameter(Mandatory, ParameterSetName = 'Line')]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line
    )

    # Re-wrap with @(): PowerShell unwraps a single-element array argument to a scalar, and under
    # Set-StrictMode a scalar string has no .Count, so a one-line file iterated zero times and produced
    # an empty evaluation instead of its single declaration.
    $lines = @(if ($PSCmdlet.ParameterSetName -eq 'Path') { [System.IO.File]::ReadAllLines($Path) } else { $Line })

    # Terminal values of the declarations seen so far. Never fed back through raw-value resolution:
    # see the NameLookup note on Resolve-ComposeEnvReference.
    #
    # ORDINAL dictionaries, not PowerShell hashtables. A hashtable compares keys case-insensitively,
    # but a dotenv identifier is case-sensitive on the Linux CI and runtime path: verified live in a
    # Linux container, ${upper_name} against an UPPER_NAME declaration renders UNSET, while Windows
    # Docker Desktop normalizes case and resolves it. Case-insensitive storage would let a lowercase
    # typo satisfy an uppercase lookup in the preflight while Compose leaves the real reference unset.
    # Ambient lookups deliberately keep the platform's own semantics.
    $accumulated = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    $declarations = [System.Collections.Generic.List[object]]::new()
    $declarationCounts = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)

    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $text = [string]$lines[$lineIndex]
        if ($text -match '^\s*#') { continue }

        $assignment = Get-DotenvAssignment -Line $text
        if ($null -eq $assignment) { continue }

        $trace = [System.Collections.Generic.List[string]]::new()
        # Provenance matters downstream: when ambient supplied a value, the file's own declarations of
        # that name are inert, so dependency traversal must not descend into them and duplicate
        # declarations of it cannot affect anything.
        $ambientTrace = [System.Collections.Generic.List[string]]::new()
        $lookup = {
            param([string]$name)
            $ambientValue = [System.Environment]::GetEnvironmentVariable($name)
            if ($null -ne $ambientValue) {
                if (-not $ambientTrace.Contains($name)) { $ambientTrace.Add($name) }
                return $ambientValue
            }
            if ($accumulated.ContainsKey($name)) { return [string]$accumulated[$name] }
            return $null
        }.GetNewClosure()

        # Resolve-ComposeEnvRawValue applies Compose's value semantics to the raw text first (quote
        # stripping, inline comments, and the single-quote literal rule), then interpolates - so a
        # single-quoted value stays literal here exactly as Compose leaves it.
        $resolved = Resolve-ComposeEnvRawValue `
            -EnvironmentValues @{} `
            -RawValue $assignment.RawValue `
            -NameLookup $lookup `
            -ReferenceTrace $trace

        $accumulated[$assignment.Key] = $resolved
        $existingCount = 0
        if ($declarationCounts.ContainsKey($assignment.Key)) { $existingCount = $declarationCounts[$assignment.Key] }
        $declarationCounts[$assignment.Key] = $existingCount + 1

        $declarations.Add([pscustomobject]@{
            Key               = $assignment.Key
            RawValue          = $assignment.RawValue
            LineIndex         = $lineIndex
            ResolvedValue     = $resolved
            References        = @($trace)
            AmbientReferences = @($ambientTrace)
        })
    }

    # Ambient precedence applies to the final environment too, so a key set in the shell overrides
    # whatever the file last declared.
    $effective = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($key in $accumulated.Keys) { $effective[$key] = $accumulated[$key] }
    foreach ($key in @($effective.Keys)) {
        $ambientValue = [System.Environment]::GetEnvironmentVariable($key)
        if ($null -ne $ambientValue) { $effective[$key] = $ambientValue }
    }

    $duplicates = [System.Collections.Generic.List[string]]::new()
    foreach ($declaration in $declarations) {
        if ($declarationCounts[$declaration.Key] -gt 1 -and -not $duplicates.Contains($declaration.Key)) {
            $duplicates.Add($declaration.Key)
        }
    }

    return [pscustomobject]@{
        Declarations  = @($declarations)
        Effective     = $effective
        DuplicateKeys = @($duplicates)
    }
}

function Get-ComposeResolvedEnvValue {
    <#
    .SYNOPSIS
        Reads an env-file value the way Docker Compose does: a value set for the same key in the
        process/shell environment wins (interpolation precedence); otherwise strips surrounding quotes
        and inline comments and resolves ${VAR}/$VAR references (single-quoted values stay literal).
        Falls back to the documented default when the key is absent, blank, or resolves to empty. This
        is the single Compose-equivalent resolver shared by the connection-string factory, the
        destructive-safety guard, and the E2E startup/provision phases. Never logs the value.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)][string]$Name,
        [string]$DefaultValue = ""
    )

    # Docker Compose interpolation gives a value set in the process/shell environment precedence over
    # the same key in the env file. Honour that for the requested key itself: when $Name is set in the
    # ambient environment, the container receives that value verbatim (an interpolation result is not
    # itself re-interpolated), so it wins over the file value. Otherwise use the file value through the
    # shared convert + quote-aware resolver, or the documented default when the key is absent.
    $ambient = [System.Environment]::GetEnvironmentVariable($Name)
    $resolved =
        if ($null -ne $ambient) {
            $ambient
        }
        elseif ($null -eq $EnvironmentValues -or -not $EnvironmentValues.ContainsKey($Name)) {
            $DefaultValue
        }
        else {
            Resolve-ComposeEnvRawValue -EnvironmentValues $EnvironmentValues -RawValue ([string]$EnvironmentValues[$Name])
        }

    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return $DefaultValue
    }

    return $resolved
}

function Get-RequiredComposeResolvedEnvValue {
    <#
    .SYNOPSIS
        Resolves a required env value with Docker Compose precedence (ambient wins, references followed,
        single quotes literal) and throws when it is absent in both the process environment and the env
        file. Used by the E2E startup/provision phases so a required credential/port is read exactly as
        the running stack sees it, and never logs the value.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)][string]$Name
    )

    $value = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment value '$Name' is not set (checked the process environment and the env file)."
    }

    return $value
}

function Assert-SafeDatabaseName {
    <#
    .SYNOPSIS
        Throws when a database name contains unsupported characters or names a reserved
        PostgreSQL/SQL Server system database, so it can never target shared state.
    #>
    param([string]$DatabaseName)

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    if ($DatabaseName -iin @("postgres", "template0", "template1")) {
        throw "Database name '$DatabaseName' is a reserved PostgreSQL system database and cannot be used for E2E provisioning."
    }

    if ($DatabaseName -iin @("master", "model", "msdb", "tempdb")) {
        throw "Database name '$DatabaseName' is a reserved SQL Server system database and cannot be used for E2E provisioning."
    }
}

function Test-ProtectedKeyConfigured {
    <#
    .SYNOPSIS
        Returns true when a protected key is configured for the running stack - present in the env
        file (even with a blank value) or present in the process/shell environment (Docker Compose
        would consume an ambient value even when the file omits it). An explicitly blank env-file
        value still counts as configured: Compose's ${VAR:-default} substitutes the default for a
        blank value, so the running container can be on the compose-file default database while the
        configured value resolves to nothing - the dedicated-E2E guard must then fail closed rather
        than skip the collision check. Only a genuinely absent key is skippable.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -ne [System.Environment]::GetEnvironmentVariable($Name)) {
        return $true
    }

    return $null -ne $EnvironmentValues -and $EnvironmentValues.ContainsKey($Name)
}

function Get-DatabaseNameFromResolvedConnectionString {
    <#
    .SYNOPSIS
        Parses every database / initial-catalog value out of an already-fully-resolved ADO.NET
        connection string, with no further ${VAR} resolution on the extracted values.
    .DESCRIPTION
        Unlike Get-DatabaseNameFromConnectionString, this function performs no secondary reference
        resolution: it assumes the caller already resolved the whole connection string with full
        Docker Compose precedence (e.g. via Get-ComposeResolvedEnvValue) before calling this, so an
        entirely-ambient connection string that happens to contain literal, un-interpolated
        "${...}"-shaped text - which Compose itself never reinterpolates for a value it already
        took verbatim from the shell - is returned as-is rather than incorrectly resolved a second
        time. Database and Initial Catalog are provider synonyms; every candidate present is
        returned so a caller can require all of them to agree rather than guessing which one wins
        (DbConnectionStringBuilder does not preserve which alias appeared later in the source
        text). Returns an empty array when the string is blank or carries no database keyword.

    .PARAMETER ConnectionString
        An already Compose-precedence-resolved connection string.
    #>
    param(
        [string]$ConnectionString
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return @()
    }

    try {
        $connectionStringBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connectionStringBuilder.PSBase.ConnectionString = $ConnectionString

        return @(
            foreach ($key in $connectionStringBuilder.PSBase.Keys) {
                if ([string]$key -imatch '^(database|initial\s+catalog)$') {
                    [string]$connectionStringBuilder.PSBase.get_Item($key)
                }
            }
        )
    }
    catch {
        throw "Could not parse the resolved connection string to extract the database name: $($_.Exception.Message)"
    }
}

function Test-PortNumberEquivalent {
    <#
    .SYNOPSIS
        True when two port spellings denote the same TCP port under the providers' own parsing rules.

    .DESCRIPTION
        The single interpretation both endpoint boundaries use - the provisioning phase's local-target
        classification and the CMS pre-start endpoint agreement - so neither can drift into its own
        reading of a port. Invariant NumberStyles::Integer (a leading sign and surrounding whitespace,
        but no thousands separator and no decimal point, both of which Npgsql rejects outright) plus a
        1-65535 range check, so '05432', '+5432' and '5432' are the same port while anything that is not
        a port is never claimed equivalent to one.
    #>
    param(
        [string]$Left,
        [string]$Right
    )

    [int]$leftNumber = 0
    [int]$rightNumber = 0

    return (
        [int]::TryParse(
            ([string]$Left).Trim(),
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$leftNumber) -and
        [int]::TryParse(
            ([string]$Right).Trim(),
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$rightNumber) -and
        $leftNumber -ge 1 -and $leftNumber -le 65535 -and
        $rightNumber -ge 1 -and $rightNumber -le 65535 -and
        $leftNumber -eq $rightNumber
    )
}

function Get-PostgresHostCandidateEndpoint {
    <#
    .SYNOPSIS
        Expands an Npgsql Host value into the endpoints it can actually connect to: the documented
        comma-separated candidate list, each entry optionally carrying its own "host:port".

    .DESCRIPTION
        The ONE parser for this grammar, shared by every caller that has to reason about a PostgreSQL
        Host value, so the provisioning classifier and the CMS endpoint validator cannot drift into
        different readings of the same string again.

        Npgsql's Host is not a scalar hostname: it supports an ordered comma-separated host list with
        failover and load balancing (npgsql.org/doc/failover-and-load-balancing.html). An entry without
        its own port uses the standalone Port value, which is the list's global default; when that is
        absent too the port is reported as $null and the caller applies its own default.

        The host/port split follows Npgsql's own TrySplitHostPort decision
        (github.com/npgsql/npgsql/blob/v8.0.4/src/Npgsql/NpgsqlConnectionStringBuilder.cs) rather than a
        heuristic of our own, because the two disagreed on unbracketed IPv6. Given the last colon in an
        entry, the last ']' and the last colon before it, the final colon is a port separator only when
        there is NO earlier colon, or the final colon is after ']' while the earlier colon is inside the
        brackets. Otherwise the whole entry is one host taking the standalone port.

        That is what stops an unbracketed IPv6 address being torn apart: '::ffff:7f00:1' has an earlier
        colon and no bracket, so it is one host - previously its final ':1' was read as port 1, leaving
        host '::ffff:7f00'. Since that address maps to 127.0.0.1, the provider reached the local Compose
        database while classification called it external. A final numeric IPv6 segment is not a port just
        because it parses as an integer.

        A suffix the algorithm does identify as a port is still validated as one, so a trailing ":<junk>"
        leaves the entry whole rather than producing a bogus port.

        Note what this does NOT decide: whether a candidate is acceptable. The provisioning classifier
        asks whether ANY candidate is the local Compose endpoint (a local member anywhere means the
        reserved-database guard must run); CMS validation requires EVERY candidate to be the composed
        service (Npgsql may fail over to any member, so one external member is a real misconfiguration).
        Same grammar, deliberately opposite quantifiers.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the collection of candidate endpoints in the host list; the singular noun names one element of the return shape.')]
    param(
        [string]$HostValue,
        [string]$DefaultPort
    )

    return @(
        foreach ($entry in ([string]$HostValue).Split(',')) {
            $text = $entry.Trim()
            if ([string]::IsNullOrEmpty($text)) { continue }

            # Npgsql's TrySplitHostPort decision, in its terms.
            $portSeparator = $text.LastIndexOf(':')
            $isPortSeparator = $false
            $suffix = ""

            if ($portSeparator -gt 0) {
                $closingBracket = $text.LastIndexOf(']')
                $previousColon = $text.Substring(0, $portSeparator).LastIndexOf(':')

                # No earlier colon -> "host:port". An earlier colon means IPv6 text, and then the final
                # colon is a port separator only when it sits outside a bracketed address that the earlier
                # colon sits inside.
                if ($previousColon -eq -1 -or
                    ($portSeparator -gt $closingBracket -and $previousColon -lt $closingBracket)) {
                    $suffix = $text.Substring($portSeparator + 1).Trim()
                    $isPortSeparator = Test-PortNumberEquivalent -Left $suffix -Right $suffix
                }
            }

            if ($isPortSeparator) {
                [pscustomobject]@{
                    Host = $text.Substring(0, $portSeparator).Trim()
                    Port = $suffix
                }
            }
            else {
                [pscustomobject]@{
                    Host = $text
                    Port = $DefaultPort
                }
            }
        }
    )
}

function Get-SqlServerDataSourceEndpoint {
    <#
    .SYNOPSIS
        The ONE parser for SqlClient's data-source (Server=) grammar: protocol, host, explicit port and
        instance suffix, as Microsoft.Data.SqlClient itself decides them.

    .DESCRIPTION
        Every MSSQL endpoint decision in this repository goes through here - the provisioning phase's
        local-target classification and host-side translation, and the CMS pre-start endpoint agreement.
        There were previously two independent readings of this one grammar, and they were not merely
        duplicated but UNEQUAL, so each review round found a spelling one of them mishandled. A single
        provider-derived grammar is what stops that: a form is either in the grammar or it is not, and
        both subsystems get the same answer.

        The decision follows Microsoft.Data.SqlClient 6.1.4's managed SNI implementation
        (github.com/dotnet/SqlClient/blob/v6.1.4/src/Microsoft.Data.SqlClient/src/Microsoft/Data/SqlClient/ManagedSni/SniProxy.netcore.cs)
        - DataSource.PopulateProtocol for the protocol token, DataSource.InferConnectionDetails for the
        endpoint split, and DataSource.InferLocalServerName for an empty server name. Managed SNI is the
        deliberate choice of contract: it is what runs in Linux containers and CI, and where it and the
        Windows native SNI library disagree it is the MORE PERMISSIVE of the two, so a target it can
        reach is a target this classification must be prepared to guard. Measured difference: managed SNI
        connects for "Server=,<port>" while native SNI refuses it outright.

        PROTOCOL. The token is the text before the FIRST colon, trimmed, compared case-insensitively
        against PopulateProtocol's recognized set - tcp, np and admin. So "tcp:host" and "tcp : host" are
        both the TCP path. A token OUTSIDE that set means no protocol was specified at all and the colon
        belongs to the host text, which is what keeps an IPv6 address whole: for "::1" the token is empty
        and for "[::1],1433" it is "[", neither of which is a protocol. That same rule is why "lpc:" is
        not stripped - it is not in managed SNI's set - so an lpc value stays whole as its host name and
        can never equal a local token or a composed service alias. It is nonmatching because of what its
        host name is, not because this parser claims lpc is a recognized protocol.

        No protocol is SqlClient's default TCP path, so IsTcp covers both. admin (the DAC prefix)
        dispatches through CreateTcpHandle, so it is ALSO the TCP transport - only its default port
        differs: a portless, non-instance admin endpoint resolves to DAC port 1434, reported in Port while
        HasExplicitPort stays false because the provider supplied it rather than the caller authoring it.
        That matters here because mssql.yml permits MSSQL_PORT=1434, so an admin target can reach the
        published Compose listener; reporting it as non-TCP classified it external and skipped the
        separate-topology authority. SqlClient accepts no comma parameters on admin, so an authored port
        there is refused rather than supported. np (named pipes) remains a genuinely different transport:
        IsTcp false, no endpoint. LocalDB and named-instance discovery stay unverifiable here.

        A '/' anywhere in the post-protocol text is rejected before any endpoint is inferred, matching the
        provider, so a string it refuses cannot be reported as a plausible host and port.

        ENDPOINT SPLIT. InferConnectionDetails splits the endpoint on BOTH ',' and '\' and selects by
        POSITION among the resulting tokens. Token 0 is the server. When a comma is present the port is
        token 2 if a backslash exists and the comma follows it, and token 1 otherwise. So "host\inst,1433"
        and "host,1433\inst" name the same endpoint, "host,1433,ignored" uses 1433, and
        "host\inst\15433,9999" uses 15433 - the token BEFORE the comma, not the text after it. With an
        explicit comma port the instance suffix is irrelevant, so it does not require SSRP resolution.

        INVALID ENDPOINTS THROW. An empty explicit port ("host,") and an empty instance token ("host\")
        are not omitted forms - SqlClient rejects them. They are reported as a parse failure with a fixed,
        credential-free message rather than as blank fields, because every caller defaults an absent port
        to the engine's expected one; blank fields let a value the provider refuses pass as a valid local
        endpoint. A genuinely omitted port ("host") stays valid and keeps the caller's default.

        WITHOUT an explicit comma port an instance suffix DOES require SSRP, which nothing here can
        resolve. That is reported as RequiresInstanceResolution rather than decided, so callers keep
        their existing safety policy: an unresolved named instance is never claimed to be a known
        listener. This is also what keeps "(localdb)\MSSQLLocalDB" nonmatching.

        EMPTY SERVER. An empty server token becomes "localhost", per InferLocalServerName. Reported with
        HostNameWasInferred so a caller can tell an inferred host from a written one.

        CONTEXT-NEUTRAL BY CONSTRUCTION. This function decides what SqlClient would parse, never whether
        the result is acceptable. Accepted-host policy differs by caller and stays with the caller: the
        provisioning phase may recognize host-side "localhost" as the published Compose listener, while
        CMS validation must REJECT container-side "localhost" because that connection string has to name
        the composed database service. Baking either policy in here would recreate the divergence this
        function exists to remove.

    .PARAMETER DataSource
        A SqlClient data-source value - the Server / Data Source / Addr / Address / Network Address value
        already resolved from a connection string. Never rendered in diagnostics by this function.

    .OUTPUTS
        A [pscustomobject] carrying RawValue, Protocol ("" when none was specified), IsTcp, HostName,
        HostNameWasInferred, Port (the effective port: the authored comma port, or the protocol's provider
        default where one applies, "" when neither), HasExplicitPort (whether the CALLER authored the port,
        which is not the same as Port being nonblank), InstanceName ("" when none) and
        RequiresInstanceResolution - enough for every caller to decide without reparsing text.

        Throws a fixed, credential-free diagnostic for the data sources SqlClient rejects outright: a '/'
        in the post-protocol text, an empty explicit port, an empty instance name, or a comma parameter on
        the admin prefix.
    #>
    param(
        [string]
        $DataSource
    )

    $text = ([string]$DataSource).Trim()

    # FIXED text covering every rejection below, and deliberately says nothing about the value: a data
    # source can carry credentials in adjacent keys and this message travels into pre-start diagnostics.
    $invalidDataSourceMessage = "Invalid SQL Server data source: the endpoint is one Microsoft.Data.SqlClient rejects outright - it contains a forward slash, or carries an empty explicit port or an empty instance name, none of which the provider treats as omitted. The value is withheld."

    # PopulateProtocol's recognized set. Anything else is not a protocol, so the colon stays with the host.
    $recognizedProtocols = @("tcp", "np", "admin")

    $protocol = ""
    $endpointText = $text
    $colonIndex = $text.IndexOf(':')
    if ($colonIndex -ge 0) {
        $token = $text.Substring(0, $colonIndex).Trim().ToLowerInvariant()
        if ($recognizedProtocols -contains $token) {
            $protocol = $token
            $endpointText = $text.Substring($colonIndex + 1).Trim()
        }
    }

    # SqlClient rejects a post-protocol data source containing '/' before it infers any endpoint from it, so
    # this precedes both the non-TCP return and the split. Accepting it reported a plausible host and port
    # for a string the provider refuses, which let the CMS preflight pass a value that cannot connect.
    if ($endpointText.Contains('/')) {
        throw $invalidDataSourceMessage
    }

    # Admin (the DAC prefix) dispatches through CreateTcpHandle, so it IS the TCP transport for endpoint
    # purposes - only its default port differs. Treating it as non-TCP classified an admin target as
    # external and skipped the separate-topology authority, even though mssql.yml permits MSSQL_PORT=1434
    # and the host's published listener then answers exactly the port admin resolves to.
    $isTcp = [string]::IsNullOrEmpty($protocol) -or $protocol -eq "tcp" -or $protocol -eq "admin"

    if (-not $isTcp) {
        # np: named pipes is a genuinely different transport. The whole value is reported as the host so no
        # caller can mistake part of it for a reachable TCP endpoint.
        return [pscustomobject]@{
            RawValue                   = $text
            Protocol                   = $protocol
            IsTcp                      = $false
            HostName                   = $text
            HostNameWasInferred        = $false
            Port                       = ""
            HasExplicitPort            = $false
            InstanceName               = ""
            RequiresInstanceResolution = $false
        }
    }

    $commaIndex = $endpointText.IndexOf(',')
    $backslashIndex = $endpointText.IndexOf('\')

    # InferConnectionDetails splits the endpoint on BOTH delimiters and then selects by POSITION in the
    # resulting token list, which is not the same as reading the text after the first comma. Token 0 is
    # always the server. Where the port sits depends on whether the comma follows a backslash: with an
    # instance segment ahead of it the port is token 2, otherwise token 1. Reading "after the first
    # comma" agreed with that only for the shapes that happen to have at most one segment before the
    # port; 'localhost\ignored\15433,9999' selects 15433, not 9999, so the earlier reading produced a
    # port the provider never connects to - in both directions, since the mirror spelling
    # 'localhost\ignored\9999,15433' was classified local while SqlClient reaches 9999.
    # [char[]] is load-bearing: a bare @(',', '\') binds the String[] overload, which returns the value
    # UNSPLIT and made every explicit port read as empty.
    $endpointTokens = @($endpointText.Split([char[]]@(',', '\')) | ForEach-Object { $_.Trim() })

    $hostName = $endpointTokens[0]

    $hasExplicitPort = $commaIndex -ge 0
    $portText = ""
    if ($hasExplicitPort) {
        # SqlClient does not accept comma parameters on the admin prefix, so an authored port there is a
        # rejected data source rather than a DAC endpoint on that port. Refused instead of quietly
        # supporting a form the provider does not.
        if ($protocol -eq "admin") {
            throw $invalidDataSourceMessage
        }

        $portTokenIndex = if ($backslashIndex -ge 0 -and $commaIndex -gt $backslashIndex) { 2 } else { 1 }
        if ($portTokenIndex -lt $endpointTokens.Count) {
            $portText = $endpointTokens[$portTokenIndex]
        }

        # SqlClient does not treat an empty explicit port as an omitted one - it rejects the data source.
        # Reported as a parse failure rather than as blank fields, because every caller defaults an absent
        # port to the engine's expected one, which would launder an endpoint the provider refuses into an
        # apparently valid local endpoint.
        if ([string]::IsNullOrWhiteSpace($portText)) {
            throw $invalidDataSourceMessage
        }
    }

    # The instance suffix runs from the backslash to the next comma, so it is read the same way whether it
    # precedes or follows the port.
    $instanceName = ""
    if ($backslashIndex -ge 0) {
        $instanceStart = $backslashIndex + 1
        $instanceEnd = $endpointText.IndexOf(',', $instanceStart)
        if ($instanceEnd -lt 0) { $instanceEnd = $endpointText.Length }
        $instanceName = $endpointText.Substring($instanceStart, $instanceEnd - $instanceStart).Trim()

        # An empty instance token is likewise rejected, not read as "no instance". Without this,
        # 'dms-mssql\' reported exactly what the valid 'dms-mssql' reports and inherited its default port.
        if ((-not $hasExplicitPort) -and [string]::IsNullOrWhiteSpace($instanceName)) {
            throw $invalidDataSourceMessage
        }
    }

    # InferLocalServerName: an empty server name is the local server.
    $hostNameWasInferred = $false
    if ([string]::IsNullOrEmpty($hostName)) {
        $hostName = "localhost"
        $hostNameWasInferred = $true
    }

    $requiresInstanceResolution =
        ((-not [string]::IsNullOrEmpty($instanceName)) -and (-not $hasExplicitPort))

    # A portless admin: endpoint resolves to the DAC port, which the provider supplies rather than the
    # caller authoring it - so the port is reported while HasExplicitPort stays false. An admin endpoint
    # naming an INSTANCE still needs SSRP, so it gets no default and remains unresolved.
    if ($protocol -eq "admin" -and (-not $hasExplicitPort) -and (-not $requiresInstanceResolution)) {
        $portText = "1434"
    }

    return [pscustomobject]@{
        RawValue                   = $text
        Protocol                   = $protocol
        IsTcp                      = $true
        HostName                   = $hostName
        HostNameWasInferred        = $hostNameWasInferred
        Port                       = $portText
        HasExplicitPort            = $hasExplicitPort
        InstanceName               = $instanceName
        RequiresInstanceResolution = $requiresInstanceResolution
    }
}

function Get-EndpointFromResolvedConnectionString {
    <#
    .SYNOPSIS
        Parses every host/server value out of an already-fully-resolved ADO.NET connection string into
        separate Host/Port fields, delegating each engine's grammar to its one shared parser
        (Get-SqlServerDataSourceEndpoint for MSSQL, Get-PostgresHostCandidateEndpoint for PostgreSQL).
        No further ${VAR} resolution is performed, matching
        Get-DatabaseNameFromResolvedConnectionString's contract. Returns an empty array when the
        string is blank or carries no recognized host keyword for the given engine.

    .DESCRIPTION
        Host-key recognition is engine-specific, not a single union applied to both: MSSQL recognizes
        Server / Data Source / Addr / Address / Network Address; PostgreSQL recognizes Host / Server.
        An engine-only alias (e.g. Address= for PostgreSQL, or Host= for MSSQL) is not recognized for
        the other engine, so a connection string authored for one engine cannot be mistaken for a
        valid endpoint under the other engine's rules - the runtime ADO.NET provider would reject it
        too.

        Port parsing is also engine-specific, because the two grammars genuinely differ:

          - MSSQL encodes host and port together in one data-source value and does not recognize a
            standalone Port keyword at all, so honoring one there would accept a keyword the real
            provider rejects. That value is read by the shared SqlClient grammar
            (Get-SqlServerDataSourceEndpoint), which owns the protocol token, the comma/backslash
            ordering, later comma tokens and the empty-server-name inference. One endpoint per host key.
          - PostgreSQL carries port as a standalone Port key AND accepts a comma-separated candidate
            list whose entries may each carry their own "host:port"
            (npgsql.org/doc/failover-and-load-balancing.html). So a comma there is a candidate
            separator, not part of one hostname: the list is expanded through
            Get-PostgresHostCandidateEndpoint, giving one reported endpoint per candidate.

        An earlier revision of this function treated a comma-bearing PostgreSQL Host as a single
        malformed hostname. That was wrong about the provider - the syntax is valid and documented - and
        it made a correctly configured multi-host CMS connection string unvalidatable. The concern behind
        it was real, though, and is preserved by the expansion: a candidate without its own port takes
        the standalone Port, so an explicit Port= can never be hidden behind a coincidental comma.

    .PARAMETER ConnectionString
        An already Compose-precedence-resolved connection string.

    .PARAMETER DatabaseEngine
        "postgresql" or "mssql". Selects which host-key aliases are recognized and which port-parsing
        rule applies.

    .OUTPUTS
        An array of [pscustomobject] with Host and Port (Port is $null when the applicable
        engine-specific port form - a comma compound for MSSQL, a standalone "port" key for PostgreSQL
        - was not present).
    #>
    param(
        [string]$ConnectionString,
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return @()
    }

    $hostKeyPattern = if ($DatabaseEngine -eq "mssql") {
        '^(server|data\s+source|addr|address|network\s+address)$'
    }
    else {
        '^(host|server)$'
    }

    try {
        $connectionStringBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connectionStringBuilder.PSBase.ConnectionString = $ConnectionString

        $standalonePort = $null
        if ($DatabaseEngine -eq "postgresql") {
            foreach ($key in $connectionStringBuilder.PSBase.Keys) {
                if ([string]$key -ieq "port") {
                    $standalonePort = [string]$connectionStringBuilder.PSBase.get_Item($key)
                }
            }
        }

        return @(
            foreach ($key in $connectionStringBuilder.PSBase.Keys) {
                if ([string]$key -imatch $hostKeyPattern) {
                    $value = [string]$connectionStringBuilder.PSBase.get_Item($key)
                    if ($DatabaseEngine -eq "mssql") {
                        # The ONE SqlClient data-source grammar (Get-SqlServerDataSourceEndpoint), the same
                        # one the provisioning phase classifies with, so a spelling cannot be a valid
                        # endpoint on one boundary and malformed on the other. A comma-only reading here
                        # rejected connection strings naming exactly the composed service: it took
                        # "dms-mssql\ignored" as a host name and "1433\ignored" as a port, when SqlClient
                        # selects the explicit comma port and does not resolve the suffix through SSRP.
                        $parsed = Get-SqlServerDataSourceEndpoint -DataSource $value
                        if ((-not $parsed.IsTcp) -or $parsed.RequiresInstanceResolution) {
                            # Not an endpoint this phase can name: a non-TCP protocol (np:, admin:/DAC) or a
                            # named instance whose port only SSRP could supply. Reported as the RAW value
                            # with no port, deliberately: the caller defaults an absent port to the engine's
                            # expected one, so reporting a bare host here would turn an unresolved instance
                            # into an apparently matching endpoint. The raw text can never equal a composed
                            # service alias, so these keep failing the host comparison exactly as before.
                            [pscustomobject]@{
                                Host = $parsed.RawValue
                                Port = $null
                            }
                        }
                        else {
                            # Any port the parser resolved is reported, whether the caller AUTHORED it or the
                            # provider supplied it: a portless admin: endpoint resolves to the DAC port, and
                            # gating on HasExplicitPort alone reported it as omitted, so this caller's
                            # absent-port default replaced 1434 with 1433. An ordinary portless TCP endpoint
                            # still has no parser port and keeps using that default.
                            [pscustomobject]@{
                                Host = $parsed.HostName
                                Port = if ([string]::IsNullOrWhiteSpace($parsed.Port)) { $null } else { $parsed.Port }
                            }
                        }
                    }
                    else {
                        # One reported endpoint per Npgsql candidate. A single-host value yields exactly
                        # one, so existing behavior is unchanged; a list yields one per member, each
                        # carrying its own port or the standalone one.
                        Get-PostgresHostCandidateEndpoint -HostValue $value -DefaultPort $standalonePort
                    }
                }
            }
        )
    }
    catch {
        throw "Could not parse the resolved connection string to extract the endpoint: $($_.Exception.Message)"
    }
}

function Get-CmsDatabaseTopologyDefaultConnectionString {
    <#
    .SYNOPSIS
        Constructs the concrete PostgreSQL connection-string default
        Confirm-CmsDatabaseTopologyAgreement validates against when
        DMS_CONFIG_DATABASE_CONNECTION_STRING is entirely absent, in exactly the shape the checked-in
        local-config.yml / published-config.yml nested Compose fallback renders (confirmed against a
        real `docker compose config` invocation - see CmsDatabaseTopology.Tests.ps1's Compose-rendering
        oracle).

    .DESCRIPTION
        PostgreSQL-only, and durably so rather than pending a later phase: both .yml fallbacks
        hardcode a PostgreSQL host, port, and username because Compose interpolation cannot branch on
        the database engine. Only the database segment could be made topology-aware, and it has been.
        So there is no MSSQL default to construct - for an MSSQL run with the key absent, Compose
        would render a PostgreSQL-shaped string that is not a usable SQL Server connection at all.
        That case cannot arise in practice, because the .env.mssql overlay always supplies the key
        explicitly; Confirm-CmsDatabaseTopologyAgreement fails clearly if it ever does, rather than
        validating against a value Compose would never produce.

        Extracted into its own function specifically so it can be asserted, in isolation, against a
        real Compose-rendered oracle string byte-for-byte - not merely indirectly by observing that
        Confirm-CmsDatabaseTopologyAgreement does not throw when validating its own construction
        against itself.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'PostgresPassword', Justification = 'Constructs the plaintext connection string Docker Compose itself renders for the CMS container; the password necessarily appears in that string, so SecureString adds no protection across this boundary.')]
    param(
        [Parameter(Mandatory)] [string]$ExpectedHost,
        [Parameter(Mandatory)] [string]$ExpectedPort,
        [Parameter(Mandatory)] [string]$ExpectedDatabaseName,
        [Parameter(Mandatory)] [string]$PostgresPassword
    )

    return "host=$ExpectedHost;port=$ExpectedPort;username=postgres;password=$PostgresPassword;database=$ExpectedDatabaseName;"
}

function Test-PostgresDuplicateDatabaseError {
    <#
    .SYNOPSIS
        Returns true when captured psql output (run with -v VERBOSITY=sqlstate) reports one of the
        two benign concurrent-database-creation races: 42P04 (a direct duplicate CREATE DATABASE)
        or 23505 (the narrower internal catalog-index race two \gexec-generated CREATE DATABASE
        statements can hit).

    .DESCRIPTION
        Empirically captured against a real PostgreSQL 16 instance (DMS-1270 Phase 1a spike): a
        direct duplicate CREATE DATABASE reports "ERROR:  42P04"; a genuine concurrent race between
        two \gexec-driven sessions targeting a not-yet-existing database reported
        "psql:<stdin>:2: ERROR:  23505" on the losing side. Matching on the bare SQLSTATE token
        after "ERROR:" is indifferent to whichever prefix, if any, precedes it, and to locale,
        since VERBOSITY=sqlstate suppresses all human-readable message text.

        Returning true here is not proof of success by itself - the caller must still verify the
        postcondition (the target database actually exists and is usable) before declaring the
        guarded-create operation successful; a benign SQLSTATE with a failed postcondition must
        still propagate as a real failure.

    .PARAMETER CapturedOutput
        The combined stdout+stderr text captured from the psql invocation.
    #>
    param(
        [string]$CapturedOutput
    )

    if ([string]::IsNullOrWhiteSpace($CapturedOutput)) {
        return $false
    }

    return [regex]::IsMatch($CapturedOutput, 'ERROR:\s+(42P04|23505)\b')
}

function Test-MssqlDuplicateDatabaseError {
    <#
    .SYNOPSIS
        Returns true when captured sqlcmd output reports SQL Server error 1801 ("database already
        exists"), the benign concurrent-database-creation race for the guarded
        IF DB_ID(...) IS NULL CREATE DATABASE ... check-then-act statement.

    .DESCRIPTION
        Confirmed against a live SQL Server 2025 instance: an unguarded duplicate CREATE DATABASE
        reports "Msg 1801, Level 16, State 3, Server <name>, Line 1" followed by the human-readable
        "already exists" text, and this predicate matches it while correctly rejecting an unrelated
        failure (Msg 208, invalid object name).

        Matches only the structured sqlcmd error-number position ("Msg 1801,"), not a bare "1801"
        anywhere in the output - unrelated text that happens to contain that number (a row count, a
        line number, a timestamp) must not be misclassified as the benign race.

        Returning true here is not proof of success by itself - the caller must still verify the
        postcondition before declaring the guarded-create operation successful.

    .PARAMETER CapturedOutput
        The combined stdout+stderr text captured from the sqlcmd invocation.
    #>
    param(
        [string]$CapturedOutput
    )

    if ([string]::IsNullOrWhiteSpace($CapturedOutput)) {
        return $false
    }

    return [regex]::IsMatch($CapturedOutput, '(?i)Msg\s+1801,')
}

function Get-DatabaseNameFromConnectionString {
    <#
    .SYNOPSIS
        Parses every database / initial-catalog value out of an ADO.NET connection string,
        resolving any env-file indirection, so the dedicated-E2E guard can compare each one.
        Database and Initial Catalog are provider synonyms (SqlClient keeps the LAST occurrence),
        but the generic parser stores both as distinct keys - a string carrying both could
        effectively target the later value, so every candidate must be returned rather than only
        the first. Returns an empty array when the string is blank or carries no database keyword.
    #>
    param(
        [string]$ConnectionString,
        [hashtable]$EnvironmentValues
    )

    $ConnectionString = ConvertFrom-ComposeEnvironmentValue -Value $ConnectionString

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return @()
    }

    try {
        $connectionStringBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        # DbConnectionStringBuilder implements IDictionary, so PowerShell's adapted view treats
        # `.ConnectionString = ...` as an item named "ConnectionString". PSBase selects the real
        # CLR property and exposes the parsed keys/items.
        $connectionStringBuilder.PSBase.ConnectionString = $ConnectionString

        # Streamed (not comma-wrapped) so the caller's @(...) collects a flat string array.
        return @(
            foreach ($key in $connectionStringBuilder.PSBase.Keys) {
                if ([string]$key -imatch '^(database|initial\s+catalog)$') {
                    Resolve-ComposeEnvRawValue `
                        -EnvironmentValues $EnvironmentValues `
                        -RawValue ([string]$connectionStringBuilder.PSBase.get_Item($key))
                }
            }
        )
    }
    catch {
        throw "Could not safely parse a protected database connection string: $($_.Exception.Message)"
    }
}

function Assert-E2EDatabaseIsDedicated {
    <#
    .SYNOPSIS
        Throws unless an E2E route-context database name is safe and dedicated: it must not
        match the primary/CMS database names or the database embedded in the admin/CMS
        connection strings, so E2E provisioning can never drop shared state.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [string]$EnvironmentFilePath,
        [string]$E2EDatabaseName
    )

    Assert-SafeDatabaseName -DatabaseName $E2EDatabaseName

    # Resolve every protected value the way Docker Compose does before comparing it to the E2E reset
    # target: a value set in the process/shell environment wins over the env file, ${VAR} references
    # (including ambient overrides) are followed, and single-quoted values stay literal. Evaluating the
    # raw file value instead would let an ambient MSSQL_DB_NAME / POSTGRES_DB_NAME / admin/CMS
    # connection-string override make the live shared database equal the reset target while this guard
    # sees a different file value and permits a destructive reset/drop.
    #
    # All comparisons are case-insensitive, and that is required on BOTH engines rather than merely
    # conservative on one. SQL Server's default collation treats database identifiers case-insensitively,
    # so a case-variant of a protected name IS the same database there. PostgreSQL folds an UNQUOTED
    # identifier to lower case, and the reset path drops with an unquoted one
    # (Template-Management.psm1's `DROP DATABASE IF EXISTS $DatabaseName;`), so a case-variant resolves to
    # the same physical database there too. An ordinal comparison would let a case-variant of a protected
    # name past a guard standing in front of DROP DATABASE, where a false positive costs a rename and a
    # false negative drops shared state. The guard fails closed: a protected key that is
    # configured (in the file or the ambient environment) but cannot be resolved throws rather than
    # silently skipping the collision check.
    foreach ($databaseNameKey in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) {
        if (-not (Test-ProtectedKeyConfigured -EnvironmentValues $EnvironmentValues -Name $databaseNameKey)) {
            continue
        }

        # A protected database name that is empty (explicitly blank, or an undefined reference) or
        # still contains a '$' (an unresolved or cyclic reference the resolver could not expand)
        # cannot be proven distinct from the reset target, so fail closed. A blank value is not
        # skippable because Compose's ${VAR:-default} would give the running container the compose
        # default database. A real database name never contains '$'.
        $protectedDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $databaseNameKey
        if ([string]::IsNullOrWhiteSpace($protectedDatabaseName) -or $protectedDatabaseName -match '\$') {
            throw "E2E database safety check could not resolve $databaseNameKey in '$EnvironmentFilePath' (blank, or an unresolved or cyclic reference); refusing a destructive reset that cannot be proven dedicated."
        }

        if ($E2EDatabaseName -ieq $protectedDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must be dedicated and cannot match $databaseNameKey."
        }
    }

    foreach ($connectionStringKey in @(
            "DATABASE_CONNECTION_STRING_ADMIN",
            "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        )) {
        if (-not (Test-ProtectedKeyConfigured -EnvironmentValues $EnvironmentValues -Name $connectionStringKey)) {
            continue
        }

        $connectionString = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $connectionStringKey
        if ([string]::IsNullOrWhiteSpace($connectionString)) {
            throw "E2E database safety check could not resolve $connectionStringKey in '$EnvironmentFilePath'; refusing a destructive reset that cannot be proven dedicated."
        }

        $connectionStringDatabaseNames = @(Get-DatabaseNameFromConnectionString `
            -ConnectionString $connectionString `
            -EnvironmentValues $EnvironmentValues)

        if ($connectionStringDatabaseNames.Count -eq 0) {
            throw "E2E database safety check could not determine a database name from $connectionStringKey in '$EnvironmentFilePath'."
        }

        # Database and Initial Catalog are provider synonyms; a connection string can carry both,
        # and SqlClient uses the last occurrence. Every candidate is checked so the effective
        # database can never skip the collision check behind a decoy first value.
        foreach ($connectionStringDatabaseName in $connectionStringDatabaseNames) {
            if ([string]::IsNullOrWhiteSpace($connectionStringDatabaseName)) {
                throw "E2E database safety check could not determine a database name from $connectionStringKey in '$EnvironmentFilePath'."
            }

            # A parsed database name that still contains a '$' came from an unresolved or cyclic reference
            # the resolver could not expand; fail closed rather than compare an indeterminate value.
            if ($connectionStringDatabaseName -match '\$') {
                throw "E2E database safety check could not fully resolve the database name from $connectionStringKey in '$EnvironmentFilePath' (unresolved or cyclic reference); refusing a destructive reset that cannot be proven dedicated."
            }

            if ($E2EDatabaseName -ieq $connectionStringDatabaseName) {
                throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must stay separate from $connectionStringKey."
            }
        }
    }
}

Export-ModuleMember -Function `
    ConvertFrom-ComposeEnvironmentValue, `
    Resolve-ComposeEnvReference, `
    Resolve-ComposeEnvRawValue, `
    Get-DotenvAssignment, `
    Test-DotenvAssignmentLine, `
    Resolve-DotenvFileSequentially, `
    Get-ComposeResolvedEnvValue, `
    Get-RequiredComposeResolvedEnvValue, `
    Assert-SafeDatabaseName, `
    Get-DatabaseNameFromConnectionString, `
    Get-DatabaseNameFromResolvedConnectionString, `
    Get-EndpointFromResolvedConnectionString, `
    Get-SqlServerDataSourceEndpoint, `
    Get-PostgresHostCandidateEndpoint, `
    Test-PortNumberEquivalent, `
    Get-CmsDatabaseTopologyDefaultConnectionString, `
    Test-PostgresDuplicateDatabaseError, `
    Test-MssqlDuplicateDatabaseError, `
    Assert-E2EDatabaseIsDedicated
