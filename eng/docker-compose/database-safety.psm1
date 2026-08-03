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

        A trailing ":<port>" is only taken as a port when the suffix really is one, so a host spelling
        that merely contains a colon is left whole rather than split into a bogus host and port.

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

            $separatorIndex = $text.LastIndexOf(':')
            $suffix = if ($separatorIndex -gt 0) { $text.Substring($separatorIndex + 1).Trim() } else { "" }

            if ($separatorIndex -gt 0 -and (Test-PortNumberEquivalent -Left $suffix -Right $suffix)) {
                [pscustomobject]@{
                    Host = $text.Substring(0, $separatorIndex).Trim()
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

function Get-EndpointFromResolvedConnectionString {
    <#
    .SYNOPSIS
        Parses every host/server value out of an already-fully-resolved ADO.NET connection string,
        splitting a "host,port" compound (the MSSQL Server=host,port shape) into separate Host/Port
        fields. No further ${VAR} resolution is performed, matching
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

          - MSSQL encodes host and port together in one "host,port" value (optionally behind a "tcp:"
            protocol prefix), and does not recognize a standalone Port keyword at all, so honoring one
            there would accept a keyword the real provider rejects. One endpoint per host key.
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
                        $parts = $value.Split(',', 2)
                        # "tcp:host,port" is documented SqlClient syntax
                        # (learn.microsoft.com/troubleshoot/sql/connect/use-server-name-parameter-connection-string)
                        # naming the same server "host,port" does, so exactly one leading "tcp:" is removed
                        # case-insensitively before the host is reported. Without this the caller compared
                        # "tcp:dms-mssql" against the Compose service aliases and rejected a valid CMS
                        # connection string. Reporting only - the caller's connection string is never
                        # rewritten - and no other protocol prefix is stripped, so np:, lpc: and the rest
                        # still fail the alias comparison as they must.
                        $mssqlHost = $parts[0].Trim()
                        if ($mssqlHost.StartsWith("tcp:", [System.StringComparison]::OrdinalIgnoreCase)) {
                            $mssqlHost = $mssqlHost.Substring(4).Trim()
                        }
                        [pscustomobject]@{
                            Host = $mssqlHost
                            Port = if ($parts.Count -gt 1) { $parts[1].Trim() } else { $null }
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
    Get-PostgresHostCandidateEndpoint, `
    Test-PortNumberEquivalent, `
    Get-CmsDatabaseTopologyDefaultConnectionString, `
    Test-PostgresDuplicateDatabaseError, `
    Test-MssqlDuplicateDatabaseError, `
    Assert-E2EDatabaseIsDedicated
