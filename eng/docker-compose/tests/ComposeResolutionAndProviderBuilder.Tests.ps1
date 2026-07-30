# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'The extracted Resolve-EnvValue reads $envValues from the caller scope via dynamic scoping; the analyzer cannot see that use.')]
param()

# DMS-1284 FR8/FR10: the E2E startup readiness, provisioning, CMS/test, and destructive-safety phases all
# resolve credentials/ports through one Compose-equivalent resolver (database-safety.psm1) so an ambient
# process/shell override, a reference chain, or a special-character credential is read exactly as the
# running container sees it, and provider connection strings are built through DbConnectionStringBuilder
# rather than raw interpolation. These tests exercise the shared primitives directly.

Describe "Shared Compose resolution and safe provider builder (DMS-1284)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
        # Dot-source provision (its dot-source guard returns before any provisioning) to expose the
        # Build-ConnectionString provider-string builder without connecting to a database.
        . (Join-Path $script:dockerComposeRoot "provision-e2e-database.ps1")
    }

    Context "Get-ComposeResolvedEnvValue" {
        It "gives a process/shell value precedence over the env file" {
            $priorExists = Test-Path "Env:FR10_PWD"
            $priorValue = [System.Environment]::GetEnvironmentVariable("FR10_PWD")
            try {
                [System.Environment]::SetEnvironmentVariable("FR10_PWD", "AmbientPort9!")
                Get-ComposeResolvedEnvValue -EnvironmentValues @{ FR10_PWD = "FileValue" } -Name "FR10_PWD" |
                    Should -Be "AmbientPort9!"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("FR10_PWD", $priorValue) }
                else { Remove-Item Env:FR10_PWD -ErrorAction SilentlyContinue }
            }
        }

        It "lets an ambient override of a referenced variable win through a reference chain" {
            $priorExists = Test-Path "Env:FR10_REF"
            $priorValue = [System.Environment]::GetEnvironmentVariable("FR10_REF")
            try {
                [System.Environment]::SetEnvironmentVariable("FR10_REF", "AmbientRef9!")
                Get-ComposeResolvedEnvValue -EnvironmentValues @{ TOP = '${FR10_REF}'; FR10_REF = "FileRef" } -Name "TOP" |
                    Should -Be "AmbientRef9!"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("FR10_REF", $priorValue) }
                else { Remove-Item Env:FR10_REF -ErrorAction SilentlyContinue }
            }
        }

        It "keeps a single-quoted referenced value literal through a reference chain" {
            $values = @{ TOP = '${SHARED}'; SHARED = "'`${OTHER}'"; OTHER = "should-not-expand" }
            Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name "TOP" | Should -Be '${OTHER}'
        }

        It "preserves connection-string metacharacters in a resolved value" {
            $special = 'Aa1!;=,"x'
            Get-ComposeResolvedEnvValue -EnvironmentValues @{ P = $special } -Name "P" | Should -Be $special
        }

        It "falls back to the documented default when the key is absent" {
            Get-ComposeResolvedEnvValue -EnvironmentValues @{} -Name "ABSENT" -DefaultValue "def" | Should -Be "def"
        }
    }

    Context "Compose default-value and nested interpolation (DMS-1270)" {
        # Ground truth for every expectation here was captured from a real `docker compose config`
        # render (Compose v2, 2026-07-28) over an env file defining CPI270_EMPTY= (set-but-empty),
        # CPI270_SET=set-value, CPI270_NESTED=nested-value, and leaving CPI270_UNSET undefined:
        # ':-' substitutes when unset OR empty, '-' only when unset; ':+' substitutes when set AND
        # non-empty, '+' whenever set (even empty); '?' errors only when unset while ':?' also errors
        # when empty; defaults interpolate recursively, including the nested ${A:-${B}} form, and an
        # escaped '$$' inside an operator word stays literal. Docker documents these operators for
        # .env interpolation, and a caller-authored connection string using them must resolve here
        # exactly as Compose renders it, or a valid configuration is rejected by validation.
        BeforeAll {
            $script:interpolationValues = @{
                CPI270_EMPTY  = ''
                CPI270_SET    = 'set-value'
                CPI270_NESTED = 'nested-value'
            }
        }

        It "resolves <_.v> to '<_.e>'" -ForEach @(
            @{ v = '${CPI270_UNSET:-def}'; e = 'def' }
            @{ v = '${CPI270_EMPTY:-def}'; e = 'def' }
            @{ v = '${CPI270_SET:-def}'; e = 'set-value' }
            @{ v = '${CPI270_UNSET-def}'; e = 'def' }
            @{ v = '${CPI270_EMPTY-def}'; e = '' }
            @{ v = '${CPI270_SET-def}'; e = 'set-value' }
            @{ v = '${CPI270_EMPTY:+alt}'; e = '' }
            @{ v = '${CPI270_SET:+alt}'; e = 'alt' }
            @{ v = '${CPI270_UNSET:+alt}'; e = '' }
            @{ v = '${CPI270_EMPTY+alt}'; e = 'alt' }
            @{ v = '${CPI270_UNSET+alt}'; e = '' }
            @{ v = '${CPI270_UNSET:-${CPI270_NESTED}}'; e = 'nested-value' }
            @{ v = '${CPI270_UNSET:-pre${CPI270_NESTED}post}'; e = 'prenested-valuepost' }
            @{ v = 'database=${CPI270_UNSET:-${CPI270_NESTED}}'; e = 'database=nested-value' }
            @{ v = '$${CPI270_NESTED}'; e = '${CPI270_NESTED}' }
            @{ v = '${1BAD}'; e = '${1BAD}' }
            @{ v = '${CPI270_SET|x}'; e = '${CPI270_SET|x}' }
            # Every remaining set/empty/unset branch of the two "error" and two "alternate"
            # operators, so a mutation in any one of them is observable. '?' errors only on UNSET
            # (a set-but-empty value passes through); ':?' also errors on empty; '+' substitutes
            # for any set value; ':+' requires non-empty. Both error branches are asserted below.
            @{ v = '${CPI270_SET?boom}'; e = 'set-value' }
            @{ v = '${CPI270_EMPTY?boom}'; e = '' }
            @{ v = '${CPI270_SET:?boom}'; e = 'set-value' }
            @{ v = '${CPI270_SET+alt}'; e = 'alt' }
            # Escaped interpolation INSIDE an operator word stays literal, braces and all: Compose
            # pairs every '{' with a '}' while finding the expression's end, so the escaped
            # reference's own closing brace does not terminate the outer expression. Verified live -
            # ${CPI270_UNSET:-pre$${X}post} rendered the literal pre${X}post. A scanner that counted
            # only '$'-prefixed opens would close early here and corrupt the result, which for a
            # connection-string default means silently connecting with the wrong value.
            @{ v = '${CPI270_UNSET:-pre$${CPI270_NESTED}post}'; e = 'pre${CPI270_NESTED}post' }
            @{ v = '${CPI270_UNSET:-a$$b}'; e = 'a$b' }
            # A brace pair with no '$' is ordinary literal text inside the word.
            @{ v = '${CPI270_UNSET:-{x}}'; e = '{x}' }
        ) {
            Resolve-ComposeEnvReference -EnvironmentValues $script:interpolationValues -Value $_.v |
                Should -BeExactly $_.e
        }

        It "resolves operators inside an env-file value reached through a plain reference" {
            # Compose interpolates the env file's own values with the same operators; verified live
            # (CHAIN=`${A:-`${B}} in the env file rendered the nested default).
            $values = $script:interpolationValues.Clone()
            $values['CPI270_CHAIN'] = '${CPI270_UNSET:-${CPI270_NESTED}}'
            Resolve-ComposeEnvReference -EnvironmentValues $values -Value '${CPI270_CHAIN}' |
                Should -BeExactly 'nested-value'
        }

        It "surfaces the :? and ? required-variable errors instead of resolving to empty" {
            # Both error operators, on both of the states that must raise: ':?' on unset AND on
            # set-but-empty, '?' on unset only. The passing states are in the matrix above.
            { Resolve-ComposeEnvReference -EnvironmentValues $script:interpolationValues -Value '${CPI270_UNSET:?var is required}' } |
                Should -Throw "*required variable 'CPI270_UNSET'*var is required*"
            { Resolve-ComposeEnvReference -EnvironmentValues $script:interpolationValues -Value '${CPI270_EMPTY:?must be non-empty}' } |
                Should -Throw "*required variable 'CPI270_EMPTY'*"
            { Resolve-ComposeEnvReference -EnvironmentValues $script:interpolationValues -Value '${CPI270_UNSET?plain form}' } |
                Should -Throw "*required variable 'CPI270_UNSET'*plain form*"
            # '?' (without ':') accepts a set-but-empty value.
            Resolve-ComposeEnvReference -EnvironmentValues $script:interpolationValues -Value '${CPI270_EMPTY?msg}' |
                Should -BeExactly ''
        }
    }

    # Docker Compose resolves an --env-file sequentially, and a hashtable-based resolution cannot
    # express that. Every expectation in this Context was captured from a real `docker compose config`
    # render before the code was written; the comments name the captured value so a future reader can
    # tell a pinned observation from an assumption.
    Context "Sequential dotenv evaluation (DMS-1270)" {
        BeforeAll {
            # These fixtures resolve names that must not be inherited from the developer's shell.
            $script:seqNames = @('A', 'AMB_ONLY', 'NAME', 'DUP', 'PASSWORD', 'LATE_SECRET', 'CONN')
            $script:seqSnapshot = @{}
            foreach ($name in $script:seqNames) {
                $script:seqSnapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
                Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
            }
        }

        AfterAll {
            foreach ($name in $script:seqNames) {
                if ($null -eq $script:seqSnapshot[$name]) {
                    Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
                }
                else {
                    [System.Environment]::SetEnvironmentVariable($name, $script:seqSnapshot[$name])
                }
            }
        }

        It "freezes each line against only what precedes it, and keeps the last declaration as effective" {
            # Captured: this exact file rendered CONN as host=h;db=first-value;pw=; while ${DUP} at the
            # compose-file level rendered second-value. One duplicated key, two effective values.
            $evaluation = Resolve-DotenvFileSequentially -Line @(
                'DUP=first-value'
                'PASSWORD=${LATE_SECRET}'
                'CONN=host=h;db=${DUP};pw=${PASSWORD};'
                'LATE_SECRET=late'
                'DUP=second-value'
            )

            ($evaluation.Declarations | Where-Object { $_.Key -eq 'CONN' }).ResolvedValue |
                Should -BeExactly 'host=h;db=first-value;pw=;' -Because "Compose froze the first DUP and an as-yet-undeclared PASSWORD"
            ($evaluation.Declarations | Where-Object { $_.Key -eq 'PASSWORD' }).ResolvedValue |
                Should -BeExactly '' -Because "LATE_SECRET is declared after PASSWORD, so Compose froze it empty"
            $evaluation.Effective['DUP'] | Should -BeExactly 'second-value' -Because "the compose file sees the last declaration"
            $evaluation.DuplicateKeys | Should -Be @('DUP')
        }

        It "records the names each declaration actually evaluated" {
            $evaluation = Resolve-DotenvFileSequentially -Line @(
                'DUP=x'
                'PASSWORD=y'
                'CONN=host=h;db=${DUP};pw=${PASSWORD};'
            )
            ($evaluation.Declarations | Where-Object { $_.Key -eq 'CONN' }).References |
                Should -Be @('DUP', 'PASSWORD')
        }

        It "treats an already-resolved value as terminal and never re-expands it" {
            # Captured: with NAME=secret, A_ESCAPED=$${NAME} rendered the literal ${NAME}, and
            # B_REFS_A=${A_ESCAPED} rendered that SAME literal - not "secret". Same for a single-quoted
            # source value, and a literal '$' in a resolved value is not reinterpreted either. A model
            # that fed accumulated values back through raw-value resolution would leak "secret" here.
            $evaluation = Resolve-DotenvFileSequentially -Line @(
                'NAME=secret'
                'A_ESCAPED=$${NAME}'
                'B_REFS_A=${A_ESCAPED}'
                "S_SQUOTED='`${NAME}'"
                'T_REFS_S=${S_SQUOTED}'
                'D_DQUOTED="${NAME}"'
                'E_REFS_D=${D_DQUOTED}'
                'F_LITERAL=pa$$word'
                'G_REFS_F=${F_LITERAL}'
            )

            $evaluation.Effective['A_ESCAPED'] | Should -BeExactly '${NAME}'
            $evaluation.Effective['B_REFS_A'] | Should -BeExactly '${NAME}' -Because "a resolved value is terminal"
            $evaluation.Effective['S_SQUOTED'] | Should -BeExactly '${NAME}'
            $evaluation.Effective['T_REFS_S'] | Should -BeExactly '${NAME}' -Because "a single-quoted literal stays literal through a reference"
            $evaluation.Effective['D_DQUOTED'] | Should -BeExactly 'secret' -Because "double quotes do interpolate"
            $evaluation.Effective['E_REFS_D'] | Should -BeExactly 'secret'
            $evaluation.Effective['F_LITERAL'] | Should -BeExactly 'pa$word'
            $evaluation.Effective['G_REFS_F'] | Should -BeExactly 'pa$word' -Because "the literal '$' must not be reinterpreted"
        }

        It "gives an ambient value precedence even over an earlier declaration in the same file" {
            # Captured: with ambient A=ambient-a, B=${A} rendered ambient-a even though the file
            # declares A=file-a on the preceding line.
            [System.Environment]::SetEnvironmentVariable('A', 'ambient-a')
            [System.Environment]::SetEnvironmentVariable('AMB_ONLY', 'ambient-only')
            try {
                $evaluation = Resolve-DotenvFileSequentially -Line @('A=file-a', 'B=${A}', 'C=${AMB_ONLY}')
                $evaluation.Effective['B'] | Should -BeExactly 'ambient-a'
                $evaluation.Effective['C'] | Should -BeExactly 'ambient-only'
            }
            finally {
                Remove-Item Env:\A -ErrorAction SilentlyContinue
                Remove-Item Env:\AMB_ONLY -ErrorAction SilentlyContinue
            }
        }

        It "accepts the whole assignment grammar Compose accepts" {
            # Captured per line: value trimmed; 'KEY = value' valid with the key trimmed; leading indent
            # valid; 'export KEY=value' valid with the prefix stripped; inline comment stripped; an
            # empty value after whitespace is still a declaration.
            $evaluation = Resolve-DotenvFileSequentially -Line @(
                'LEAD=   trimmed-value'
                'SPACED_KEY = spaced'
                '  INDENTED=indented'
                'export EXPORTED=exported'
                'INLINE=value # comment'
                'EMPTY_SP ='
                '# COMMENTED=ignored'
                'not an assignment'
            )

            $evaluation.Effective['LEAD'] | Should -BeExactly 'trimmed-value'
            $evaluation.Effective['SPACED_KEY'] | Should -BeExactly 'spaced'
            $evaluation.Effective['INDENTED'] | Should -BeExactly 'indented'
            $evaluation.Effective['EXPORTED'] | Should -BeExactly 'exported'
            $evaluation.Effective['INLINE'] | Should -BeExactly 'value'
            $evaluation.Effective['EMPTY_SP'] | Should -BeExactly ''
            $evaluation.Effective.ContainsKey('export EXPORTED') | Should -BeFalse -Because "the export prefix is not part of the key"
            $evaluation.Effective.ContainsKey('COMMENTED') | Should -BeFalse
        }

        It "parses '<line>' as key '<key>'" -ForEach @(
            @{ line = 'K=v'; key = 'K' }
            @{ line = '  K=v'; key = 'K' }
            @{ line = 'K = v'; key = 'K' }
            @{ line = 'export K=v'; key = 'K' }
        ) {
            (Get-DotenvAssignment -Line $_.line).Key | Should -BeExactly $_.key
        }

        It "does not parse '<_>' as an assignment" -ForEach @(
            '# K=v', '', '   ', 'no-equals-here', '1BAD=v', '=novalue'
        ) {
            Get-DotenvAssignment -Line $_ | Should -BeNullOrEmpty
        }
    }

    Context "Resolution-time reference reporting (DMS-1270)" {
        # An operator word is evaluated only in the branch that fires - captured live:
        # ${SET:-${MISSING:?boom}} rendered the set value with no error. So the names a value depends
        # on are a function of the environment state, not of the text, and a lexical scan over-reports.
        BeforeAll {
            $script:refValues = @{ SEQ_SET = 'set'; SEQ_EMPTY = ''; SEQ_W = 'word' }
        }

        It "reports only the names an unfired operator actually needed" {
            $trace = [System.Collections.Generic.List[string]]::new()
            $value = Resolve-ComposeEnvReference -EnvironmentValues $script:refValues -Value '${SEQ_SET:-${SEQ_W}}' -ReferenceTrace $trace

            $value | Should -BeExactly 'set'
            $trace | Should -Be @('SEQ_SET') -Because "the ':-' word was never evaluated, so SEQ_W is not a dependency"
        }

        It "reports nothing for escaped dollars, which are literals and not references" {
            $trace = [System.Collections.Generic.List[string]]::new()
            $value = Resolve-ComposeEnvReference -EnvironmentValues @{} -Value 'pa$$word and $${NAME}' -ReferenceTrace $trace

            $value | Should -BeExactly 'pa$word and ${NAME}'
            $trace.Count | Should -Be 0
        }

        It "reports '<v>' as depending on '<expected>'" -ForEach @(
            @{ v = '${SEQ_SET:-${SEQ_W}}';    expected = 'SEQ_SET' }
            @{ v = '${SEQ_EMPTY:-${SEQ_W}}';  expected = 'SEQ_EMPTY,SEQ_W' }
            @{ v = '${SEQ_MISSING:-${SEQ_W}}'; expected = 'SEQ_MISSING,SEQ_W' }
            @{ v = '${SEQ_SET-${SEQ_W}}';     expected = 'SEQ_SET' }
            @{ v = '${SEQ_EMPTY-${SEQ_W}}';   expected = 'SEQ_EMPTY' }
            @{ v = '${SEQ_MISSING-${SEQ_W}}'; expected = 'SEQ_MISSING,SEQ_W' }
            @{ v = '${SEQ_SET:+${SEQ_W}}';    expected = 'SEQ_SET,SEQ_W' }
            @{ v = '${SEQ_EMPTY:+${SEQ_W}}';  expected = 'SEQ_EMPTY' }
            @{ v = '${SEQ_MISSING:+${SEQ_W}}'; expected = 'SEQ_MISSING' }
            @{ v = '${SEQ_SET+${SEQ_W}}';     expected = 'SEQ_SET,SEQ_W' }
            @{ v = '${SEQ_EMPTY+${SEQ_W}}';   expected = 'SEQ_EMPTY,SEQ_W' }
            @{ v = '${SEQ_MISSING+${SEQ_W}}'; expected = 'SEQ_MISSING' }
        ) {
            $trace = [System.Collections.Generic.List[string]]::new()
            $null = Resolve-ComposeEnvReference -EnvironmentValues $script:refValues -Value $_.v -ReferenceTrace $trace
            ($trace -join ',') | Should -BeExactly $_.expected
        }
    }

    Context "Terminal-value lookup delegate (DMS-1270)" {
        It "uses the delegate's value verbatim, without resolving it again" {
            $lookup = { param($n) if ($n -eq 'FROZEN') { return '${INNER}' } ; return 'should-not-be-used' }
            Resolve-ComposeEnvReference -Value '${FROZEN}' -NameLookup $lookup |
                Should -BeExactly '${INNER}' -Because "an already-resolved value is terminal"
        }

        It "preserves unset versus set-but-empty across every operator" {
            # The delegate returns '' for EMPTY and $null for anything else, so this pins the one
            # distinction the ':-'/'-' and ':+'/'+' pairs key on.
            $lookup = { param($n) if ($n -eq 'EMPTY') { return '' } ; return $null }

            Resolve-ComposeEnvReference -Value '${EMPTY:-def}' -NameLookup $lookup | Should -BeExactly 'def'
            Resolve-ComposeEnvReference -Value '${EMPTY-def}' -NameLookup $lookup | Should -BeExactly ''
            Resolve-ComposeEnvReference -Value '${GONE-def}' -NameLookup $lookup | Should -BeExactly 'def'
            Resolve-ComposeEnvReference -Value '${EMPTY:+alt}' -NameLookup $lookup | Should -BeExactly ''
            Resolve-ComposeEnvReference -Value '${EMPTY+alt}' -NameLookup $lookup | Should -BeExactly 'alt'
            Resolve-ComposeEnvReference -Value '${GONE+alt}' -NameLookup $lookup | Should -BeExactly ''
        }

        It "leaves existing raw-map callers on recursive resolution when no delegate is supplied" {
            Resolve-ComposeEnvReference -EnvironmentValues @{ TOP = '${INNER}'; INNER = 'deep' } -Value '${TOP}' |
                Should -BeExactly 'deep'
            Get-ComposeResolvedEnvValue -EnvironmentValues @{ TOP = '${S}'; S = "'`${OTHER}'"; OTHER = 'x' } -Name 'TOP' |
                Should -BeExactly '${OTHER}'
        }
    }

    Context "Get-RequiredComposeResolvedEnvValue" {
        It "returns the resolved value when present" {
            Get-RequiredComposeResolvedEnvValue -EnvironmentValues @{ K = "value" } -Name "K" | Should -Be "value"
        }

        It "throws when the required value is absent in both the environment and the file" {
            { Get-RequiredComposeResolvedEnvValue -EnvironmentValues @{} -Name "MISSING_REQUIRED" } |
                Should -Throw "*MISSING_REQUIRED*is not set*"
        }
    }

    Context "provision environment map preserves raw Compose values" {
        It "keeps a single-quoted password literal through the shared resolver" {
            # Get-EnvironmentValueMap must store RAW env-file values: the resolver's single-quote
            # literal rule keys off the raw leading quote, so a pre-stripped map would let a
            # single-quoted password be interpolated ('$$' collapsed to '$') while Docker Compose
            # gives the container the literal value - the provision/reset phase would then use a
            # password the running SQL Server never received.
            $envFile = Join-Path ([System.IO.Path]::GetTempPath()) "dms1284-raw-map-$([Guid]::NewGuid().ToString('N')).env"
            'MSSQL_SA_PASSWORD=''Pa$$w0rd!''' | Set-Content -LiteralPath $envFile -Encoding utf8
            try {
                $map = Get-EnvironmentValueMap $envFile

                Get-ComposeResolvedEnvValue -EnvironmentValues $map -Name "MSSQL_SA_PASSWORD" |
                    Should -Be 'Pa$$w0rd!'
            }
            finally {
                Remove-Item -LiteralPath $envFile -ErrorAction SilentlyContinue
            }
        }
    }

    Context "setup-openiddict Resolve-EnvValue (ENV: indirection)" {
        BeforeAll {
            # Extract just the Resolve-EnvValue function from setup-openiddict.ps1 via the AST so the
            # ENV: indirection used for identity-store database values can be exercised without the
            # script's top-level Docker/SQL orchestration. The function reads $envValues from the
            # caller's scope (dynamic scoping), so each test defines it locally.
            $parseErrors = $null
            $tokens = $null
            $setupScript = Join-Path $script:dockerComposeRoot "setup-openiddict.ps1"
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($setupScript, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Resolve-EnvValue" }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Resolve-EnvValue was not found in setup-openiddict.ps1." }
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }

        It "resolves an ENV: value with ambient process precedence over the env file (Compose precedence)" {
            # The container received the ambient value through Compose interpolation, so the
            # identity-store setup must connect with the same value or authentication fails on any
            # ambient credential override.
            $priorExists = Test-Path "Env:DMS1284_OPENIDDICT_PROBE"
            $priorValue = [System.Environment]::GetEnvironmentVariable("DMS1284_OPENIDDICT_PROBE")
            try {
                [System.Environment]::SetEnvironmentVariable("DMS1284_OPENIDDICT_PROBE", "ambient-value")
                $envValues = @{ DMS1284_OPENIDDICT_PROBE = "file-value" }

                Resolve-EnvValue "ENV:DMS1284_OPENIDDICT_PROBE" | Should -Be "ambient-value"
            }
            finally {
                if ($priorExists) { [System.Environment]::SetEnvironmentVariable("DMS1284_OPENIDDICT_PROBE", $priorValue) }
                else { Remove-Item Env:DMS1284_OPENIDDICT_PROBE -ErrorAction SilentlyContinue }
            }
        }

        It "resolves an ENV: value from the env file when no ambient override exists" {
            Remove-Item Env:DMS1284_OPENIDDICT_PROBE -ErrorAction SilentlyContinue
            $envValues = @{ DMS1284_OPENIDDICT_PROBE = "file-value" }

            Resolve-EnvValue "ENV:DMS1284_OPENIDDICT_PROBE" | Should -Be "file-value"
        }

        It "returns a non-ENV: value verbatim" {
            $envValues = @{}
            Resolve-EnvValue "literal-value" | Should -Be "literal-value"
        }

        It "throws by key name, without echoing any value, when an ENV: value is configured nowhere" {
            Remove-Item Env:DMS1284_OPENIDDICT_MISSING -ErrorAction SilentlyContinue
            $envValues = @{}

            { Resolve-EnvValue "ENV:DMS1284_OPENIDDICT_MISSING" } | Should -Throw "*DMS1284_OPENIDDICT_MISSING*"
        }
    }

    Context "setup-openiddict New-MssqlCreateDatabaseStatement" {
        BeforeAll {
            # Same AST extraction as above: the statement builder is a pure function, so it is exercised
            # without the script's Docker/SQL orchestration.
            $parseErrors = $null
            $tokens = $null
            $setupScript = Join-Path $script:dockerComposeRoot "setup-openiddict.ps1"
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($setupScript, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "New-MssqlCreateDatabaseStatement" }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "New-MssqlCreateDatabaseStatement was not found in setup-openiddict.ps1." }
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }

        It "creates the configured database when the name is an ordinary identifier" {
            New-MssqlCreateDatabaseStatement -DatabaseName "edfi_configurationservice" |
                Should -Be "IF DB_ID(N'edfi_configurationservice') IS NULL CREATE DATABASE [edfi_configurationservice];"
        }

        It "doubles a single quote so the name cannot terminate the N'...' literal" {
            # The name is configuration-supplied (env file, or an ambient value that wins Compose
            # precedence), so an unescaped quote would end the literal and leave the remainder to run as
            # statement text against master.
            New-MssqlCreateDatabaseStatement -DatabaseName "db'; DROP DATABASE [edfi_datamanagementservice]; --" |
                Should -Be "IF DB_ID(N'db''; DROP DATABASE [edfi_datamanagementservice]; --') IS NULL CREATE DATABASE [db'; DROP DATABASE [edfi_datamanagementservice]]; --];"
        }

        It "doubles a closing bracket so the name cannot terminate the [...] identifier" {
            New-MssqlCreateDatabaseStatement -DatabaseName "db]; DROP DATABASE [edfi_datamanagementservice" |
                Should -Be "IF DB_ID(N'db]; DROP DATABASE [edfi_datamanagementservice') IS NULL CREATE DATABASE [db]]; DROP DATABASE [edfi_datamanagementservice];"
        }

        It "keeps a legal name that a bare identifier could not carry" {
            New-MssqlCreateDatabaseStatement -DatabaseName "edfi-config service" |
                Should -Be "IF DB_ID(N'edfi-config service') IS NULL CREATE DATABASE [edfi-config service];"
        }
    }

    Context "phase wiring for the Compose-equivalent resolver" {
        # Wiring guards for the two seams that cannot be invoked without a Docker stack: the
        # published startup's inline readiness/data-store block and the standard E2E setup wrapper's
        # target-database read. The resolver behavior itself is covered by the invoked tests above.
        BeforeAll {
            $script:startPublishedSource = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-published-dms.ps1") -Raw
            $script:setupLocalDmsSource = Get-Content -LiteralPath ([System.IO.Path]::GetFullPath((Join-Path $script:dockerComposeRoot "../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))) -Raw
        }

        It "start-published-dms.ps1 imports the shared resolver and reads no protected value from raw env-file properties" {
            $script:startPublishedSource | Should -Match 'Import-Module \(Join-Path \$PSScriptRoot "database-safety\.psm1"\)'
            foreach ($rawRead in @(
                    '\$envValues\.MSSQL_SA_PASSWORD',
                    '\$envValues\.MSSQL_DB_NAME',
                    '\$envValues\.POSTGRES_DB_NAME',
                    '\$envValues\.POSTGRES_USER',
                    '\$envValues\.POSTGRES_PASSWORD',
                    '\$envValues\.CONFIG_SERVICE_TENANT',
                    '\$envValues\.DMS_CONFIG_MULTI_TENANCY'
                )) {
                $script:startPublishedSource | Should -Not -Match $rawRead
            }
        }

        It "setup-local-dms.ps1 resolves E2E_DATABASE_NAME through the shared resolver" {
            $script:setupLocalDmsSource | Should -Match 'Import-Module \./database-safety\.psm1'
            $script:setupLocalDmsSource | Should -Match 'Get-ComposeResolvedEnvValue -EnvironmentValues \$envValues -Name "E2E_DATABASE_NAME"'
        }
    }

    Context "provision Build-ConnectionString safe builder" {
        It "quotes a <Dialect> password with connection-string metacharacters so it round-trips intact" -ForEach @(
            @{ Dialect = "mssql"; DbHost = "127.0.0.1"; Port = "1435" }
            @{ Dialect = "pgsql"; DbHost = "localhost"; Port = "5435" }
        ) {
            # Raw string interpolation would let the ';' terminate the connection string early and drop
            # the rest of the password; the DbConnectionStringBuilder form quotes it per ADO.NET rules.
            $password = 'pa;ss"wo''rd=x'
            # Build the SecureString via AppendChar (as provision-e2e-database.ps1 does) rather than
            # ConvertTo-SecureString -AsPlainText, which PSScriptAnalyzer rejects.
            $securePassword = [System.Security.SecureString]::new()
            foreach ($character in $password.ToCharArray()) { $securePassword.AppendChar($character) }
            $securePassword.MakeReadOnly()
            $credential = [System.Management.Automation.PSCredential]::new("sa", $securePassword)

            $connectionString = Build-ConnectionString -ServerHost $DbHost -Port $Port -Credential $credential -DatabaseName "edfi_e2e" -Dialect $Dialect

            $parsed = [System.Data.Common.DbConnectionStringBuilder]::new()
            $parsed.set_ConnectionString($connectionString)
            $parsed["Password"] | Should -Be $password
        }
    }
}
