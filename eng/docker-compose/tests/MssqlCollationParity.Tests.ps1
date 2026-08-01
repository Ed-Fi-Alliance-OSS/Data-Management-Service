# Live parity suite for the MSSQL fail-closed reserved-name contract.
#
# WHAT THIS PROVES, against a real SQL Server: (1) every name the shared authority ADMITS is
# genuinely a different database than the reserved 'edfi_configurationservice' under the live
# collation - the invariant that makes a false negative impossible to reintroduce silently; (2)
# every refusal the authority classifies as a measured collision really is the same database; and
# (3) the three exhaustive scans the printable-ASCII admitted universe stands on still hold (the
# only equal pairs are the 26 case pairs, no member is ignorable or folds onto a space, nothing
# expands to or from a digraph). A future reviewer example becomes one added candidate row here;
# the invariant does the arguing.
#
# OPT-IN AND READ-ONLY BY CONSTRUCTION. The live probes run only when
# DMS_MSSQL_COLLATION_FIXTURE_CONTAINER names a running SQL Server container (the sqlcmd inside
# the container is the one dependency-free client this repo's tooling can rely on - pwsh ships no
# SqlClient). DMS_MSSQL_COLLATION_FIXTURE_SA_PASSWORD overrides the documented local fixture
# password. When the variable is absent - every CI lane and the Linux hermetic run - the
# emitted-SQL contract test still RUNS (it needs no server: it inspects what the shared SQL
# generator emits), and each live test records an intentional skip; docker is never touched. Every
# probe is a SELECT, and the SQL travels to sqlcmd over STDIN: the suite copies no file into the
# container and issues no CREATE or DROP, so pointing it at a shared server cannot mutate it in
# any way. DB_ID corroboration is gated by a PRESENCE CHECK: when the reserved database does not
# exist on the target, only that corroboration skips - a null DB_ID is absence of the database,
# never evidence that a candidate is distinct.
#
# The scans use GENERATE_SERIES, so the target must be SQL Server 2022 or later; the pinned
# measurement fixture is mcr.microsoft.com/mssql/server:2025-latest. Every non-ASCII candidate is
# built from [char] code points so this file stays ASCII-only.

BeforeDiscovery {
    $script:fixtureContainer = $env:DMS_MSSQL_COLLATION_FIXTURE_CONTAINER
    $script:parityEnabled = -not [string]::IsNullOrWhiteSpace($script:fixtureContainer)
}

BeforeAll {
    # Shared by BOTH Describes below. This function is the ONE SQL-generation authority for the
    # universe scans: the emitted-SQL contract Describe asserts against exactly what it returns,
    # and the live Describe sends exactly what it returns, so the contract can never drift from
    # the SQL actually used. Pure string construction - no docker, no server.
    $script:pinnedCollation = 'SQL_Latin1_General_CP1_CI_AS'

    function Script:Get-UniverseScanStatement {
        # Every equality binds BOTH operands to the pinned collation explicitly: without the
        # clause these literal/NCHAR comparisons inherit the CURRENT DATABASE's default
        # collation - the login's default database decides which that is, sqlcmd selects none,
        # and SERVERPROPERTY('Collation') binds nothing (measured: the unbound pair scan returns
        # 26 in master and 0 in a Latin1_General_100_CS_AS database on the same server, while
        # SERVERPROPERTY reports the pinned collation in both). The emitted-SQL contract test
        # exists because for the space-class and expansion scans no database collation can
        # expose a missing clause behaviorally: all 5,540 server collations agree on their
        # results. The pair and zero-weight scans DO deviate - under Maori_100_CS_AS_SC_UTF8 the
        # hyphen is ignorable and space compares equal to hyphen.
        param([Parameter(Mandatory)][string]$PinnedCollation)

        $pinnedCollate = "COLLATE $PinnedCollation"
        [ordered]@{
            'pair'        = @"
SELECT 'pair=' + FORMAT(a.value, 'X2') + '-' + FORMAT(b.value, 'X2')
FROM GENERATE_SERIES(32, 126) a CROSS JOIN GENERATE_SERIES(32, 126) b
WHERE a.value < b.value AND NCHAR(a.value) $pinnedCollate = NCHAR(b.value) $pinnedCollate;
"@
            'zero-weight' = "SELECT 'ascii-zw=' + FORMAT(value, 'X2') FROM GENERATE_SERIES(32, 126) WHERE (N'a' + NCHAR(value) + N'b') $pinnedCollate = N'ab' $pinnedCollate;"
            'space-class' = "SELECT 'ascii-sp=' + FORMAT(value, 'X2') FROM GENERATE_SERIES(32, 126) WHERE (N'a' + NCHAR(value) + N'b') $pinnedCollate = N'a b' $pinnedCollate;"
            'expansion'   = @"
SELECT 'expansion=' + FORMAT(a.value, 'X2') + '+' + FORMAT(b.value, 'X2') + '=' + FORMAT(c.value, 'X2')
FROM (SELECT value FROM GENERATE_SERIES(95, 122) WHERE value = 95 OR value >= 97) a
CROSS JOIN (SELECT value FROM GENERATE_SERIES(95, 122) WHERE value = 95 OR value >= 97) b
CROSS JOIN (SELECT value FROM GENERATE_SERIES(95, 122) WHERE value = 95 OR value >= 97) c
WHERE (NCHAR(a.value) + NCHAR(b.value)) $pinnedCollate = NCHAR(c.value) $pinnedCollate;
"@
        }
    }
}

Describe "MSSQL collation parity: the emitted-SQL contract (no server required)" {
    # Deliberately NO live-fixture gate on this Describe. For the space-class and expansion
    # scans the emitted-SQL assertion is the ONLY permanent guard (no database collation can
    # expose a stripped COLLATE behaviorally - measured across all 5,540), so it must run in
    # every PR lane and in the hermetic check, not only when a live fixture is configured. It
    # needs no docker and no server: it inspects the SQL the shared generator emits, which is
    # byte-for-byte what the live Describe sends.
    It "binds the pinned collation to BOTH operands of every universe scan equality" {
        $statements = Get-UniverseScanStatement -PinnedCollation $script:pinnedCollation
        @($statements.Keys).Count | Should -Be 4 -Because "the pair, zero-weight, space-class and expansion scans must all be generated"
        foreach ($scanName in $statements.Keys) {
            ([regex]::Matches($statements[$scanName], [regex]::Escape("COLLATE $($script:pinnedCollation)"))).Count |
                Should -Be 2 -Because "the '$scanName' scan has one equality and must bind both of its operands explicitly; the current database's collation must never decide a proof comparison"
        }
    }
}

Describe "MSSQL collation parity: the fail-closed contract against a live server" -Skip:(-not $script:parityEnabled) {
    BeforeAll {
        # Re-read at RUN time: Pester's discovery phase (where BeforeDiscovery set the -Skip gate)
        # and its run phase do not share script variables, so the container name must be resolved
        # again here or docker exec would receive an empty container name.
        $script:fixtureContainer = $env:DMS_MSSQL_COLLATION_FIXTURE_CONTAINER

        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        $script:reservedName = 'edfi_configurationservice'
        $script:saPassword =
            if ([string]::IsNullOrWhiteSpace($env:DMS_MSSQL_COLLATION_FIXTURE_SA_PASSWORD)) { 'EdFi_Dms1!' }
            else { $env:DMS_MSSQL_COLLATION_FIXTURE_SA_PASSWORD }

        # One SQL Server expression per candidate, generated from the SAME string the predicate
        # judges: each UTF-16 code unit becomes NCHAR(0xNNNN), so no encoding layer can diverge.
        function Script:ConvertTo-NcharExpression([string]$Value) {
            @($Value.ToCharArray() | ForEach-Object { 'NCHAR(0x{0:X4})' -f [int]$_ }) -join ' + '
        }

        function Script:Invoke-FixtureSql([string]$Sql) {
            # Stdin transport, read-only by construction: the SQL travels through docker exec -i
            # into sqlcmd's standard input, so the suite writes NOTHING into the container - no
            # docker cp, no fixed /tmp path to overwrite or leave behind, no cleanup to depend on.
            # The terminal GO makes sqlcmd execute the batch deterministically at end of input.
            # The thrown diagnostic carries sqlcmd's output, never the credentials.
            $output = ($Sql + [System.Environment]::NewLine + 'GO') |
                docker exec -i $script:fixtureContainer /opt/mssql-tools18/bin/sqlcmd `
                    -S localhost -U sa -P $script:saPassword -C -h -1
            if ($LASTEXITCODE -ne 0) { throw "sqlcmd in fixture container failed: $($output -join [System.Environment]::NewLine)" }
            @($output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne '' })
        }

        # The measured candidate table: every fixture row from the stabilization measurement. Blank
        # names are deliberately absent - callers treat blank as "no name supplied" before the
        # authority ever runs, so a live verdict for one would pin nothing.
        $reserved = $script:reservedName
        $fullWidth = -join ([char[]]@(0xFF45, 0xFF44, 0xFF46, 0xFF49, 0xFF3F, 0xFF43, 0xFF4F, 0xFF4E, 0xFF46, 0xFF49,
                0xFF47, 0xFF55, 0xFF52, 0xFF41, 0xFF54, 0xFF49, 0xFF4F, 0xFF4E, 0xFF53, 0xFF45, 0xFF52, 0xFF56, 0xFF49, 0xFF43, 0xFF45))
        $script:candidates = [ordered]@{
            'exact'                = $reserved
            'case-upper'           = 'EDFI_CONFIGURATIONSERVICE'
            'case-mixed'           = 'EDFI_ConfigurationService'
            'trail-space'          = "$reserved "
            'trail-two-spaces'     = "$reserved  "
            'trail-ideo'           = "$reserved$([char]0x3000)"
            'fw-first-e'           = "$([char]0xFF45)dfi_configurationservice"
            'fw-first-E-caps'      = "$([char]0xFF25)DFI_CONFIGURATIONSERVICE"
            'fw-all'               = $fullWidth
            'fw-underscore-only'   = "edfi$([char]0xFF3F)configurationservice"
            'fw-trail-space'       = "$([char]0xFF45)dfi_configurationservice "
            'fw-trail-ideo'        = "$([char]0xFF45)dfi_configurationservice$([char]0x3000)"
            'fw-case-mix-space'    = "$([char]0xFF25)dfi_ConfigurationService "
            'compat-fi-ligature'   = "ed$([char]0xFB01)_configurationservice"
            'zw-zwj-embedded'      = "edfi$([char]0x200D)_configurationservice"
            'zw-word-joiner-emb'   = "edfi$([char]0x2060)_configurationservice"
            'zw-zwnbsp-emb'        = "edfi$([char]0xFEFF)_configurationservice"
            'emoji-trail'          = "$reserved$([char]0xD83D)$([char]0xDE00)"
            'trail-tab'            = "$reserved`t"
            'trail-lf'             = "$reserved`n"
            'trail-cr'             = "$reserved`r"
            'trail-vt'             = "$reserved$([char]0x0B)"
            'trail-ff'             = "$reserved$([char]0x0C)"
            'trail-nbsp'           = "$reserved$([char]0x00A0)"
            'lead-space'           = " $reserved"
            'lead-ideo'            = "$([char]0x3000)$reserved"
            'accent-eacute'        = "$([char]0xE9)dfi_configurationservice"
            'accent-combining'     = "e$([char]0x0301)dfi_configurationservice"
            'compat-circled-e'     = "$([char]0x24D4)dfi_configurationservice"
            'compat-superscript-e' = "$([char]0x1D49)dfi_configurationservice"
            'zw-zwsp-trail'        = "$reserved$([char]0x200B)"
            'zw-zwsp-embedded'     = "edfi$([char]0x200B)_configurationservice"
            'zw-zwnj-embedded'     = "edfi$([char]0x200C)_configurationservice"
            'zw-softhyphen-emb'    = "edfi$([char]0x00AD)_configurationservice"
            'ctrl-01-trail'        = "$reserved$([char]0x01)"
            'ctrl-01-embedded'     = "edfi$([char]0x01)_configurationservice"
            'del-7f-trail'         = "$reserved$([char]0x7F)"
            'pua-e000-trail'       = "$reserved$([char]0xE000)"
            'ascii-hyphen-emb'     = 'edfi-_configurationservice'
            'ascii-apostrophe-emb' = "edfi'_configurationservice"
            'ascii-period-emb'     = 'edfi._configurationservice'
            'ascii-space-emb'      = 'edfi _configurationservice'
            'ascii-suffixed'       = 'edfi_configurationservice_v2'
        }

        # The predicate's verdict and its classification. A refusal is "measured-rule" (step 3)
        # exactly when the name is inside the printable-ASCII universe; every other refusal is the
        # conservative kind, whose live verdict the contract deliberately leaves unconstrained.
        $script:verdicts = @{}
        foreach ($label in $script:candidates.Keys) {
            $name = $script:candidates[$label]
            $refused = Test-MssqlPhysicalDatabaseNameMatchesReservedCmsDatabase -DatabaseName $name
            $insideUniverse = $name -match '\A[\x20-\x7E]*\z'
            $script:verdicts[$label] = [pscustomobject]@{
                Name           = $name
                Refused        = $refused
                MeasuredRule   = ($refused -and $insideUniverse)
                Admitted       = (-not $refused)
            }
        }

        # One read-only round trip for everything: collation, per-candidate COLLATE equality,
        # reserved-database presence, per-candidate DB_ID resolution, and the three universe scans.
        $reservedExpression = ConvertTo-NcharExpression $script:reservedName
        $sqlLines = [System.Collections.Generic.List[string]]::new()
        [void]$sqlLines.Add('SET NOCOUNT ON;')
        [void]$sqlLines.Add("SELECT 'collation=' + CONVERT(varchar(128), SERVERPROPERTY('Collation'));")
        [void]$sqlLines.Add("SELECT 'reserved-present=' + CASE WHEN DB_ID($reservedExpression) IS NULL THEN '0' ELSE '1' END;")
        foreach ($label in $script:candidates.Keys) {
            $expression = ConvertTo-NcharExpression $script:candidates[$label]
            [void]$sqlLines.Add("SELECT 'eq:$label=' + CASE WHEN ($expression) COLLATE $($script:pinnedCollation) = ($reservedExpression) COLLATE $($script:pinnedCollation) THEN '1' ELSE '0' END;")
            [void]$sqlLines.Add("SELECT 'dbid:$label=' + CASE WHEN DB_ID($expression) IS NULL THEN 'null' WHEN DB_ID($expression) = DB_ID($reservedExpression) THEN 'reserved' ELSE 'other' END;")
        }
        # The four universe scans, from the ONE shared generator the emitted-SQL contract
        # Describe asserts against - what travels to the server here is byte-for-byte what that
        # contract inspected. See the generator for the measured collation-inheritance facts.
        $script:scanStatements = Get-UniverseScanStatement -PinnedCollation $script:pinnedCollation
        foreach ($statement in $script:scanStatements.Values) { [void]$sqlLines.Add($statement) }

        $script:probeLines = Invoke-FixtureSql ($sqlLines -join [System.Environment]::NewLine)

        $script:liveEqual = @{}
        $script:liveDbId = @{}
        foreach ($line in $script:probeLines) {
            if ($line -like 'eq:*') {
                $body = $line.Substring(3)
                $separator = $body.LastIndexOf('=')
                $script:liveEqual[$body.Substring(0, $separator)] = ($body.Substring($separator + 1) -eq '1')
            }
            elseif ($line -like 'dbid:*') {
                $body = $line.Substring(5)
                $separator = $body.LastIndexOf('=')
                $script:liveDbId[$body.Substring(0, $separator)] = $body.Substring($separator + 1)
            }
        }
        $script:liveCollation = @($script:probeLines | Where-Object { $_ -like 'collation=*' } | ForEach-Object { $_.Substring(10) }) | Select-Object -First 1
        $script:reservedPresent = @($script:probeLines | Where-Object { $_ -like 'reserved-present=*' }) -contains 'reserved-present=1'
        $script:pairLines = @($script:probeLines | Where-Object { $_ -like 'pair=*' } | ForEach-Object { $_.Substring(5) })
        $script:asciiZeroWeightLines = @($script:probeLines | Where-Object { $_ -like 'ascii-zw=*' })
        $script:asciiSpaceClassLines = @($script:probeLines | Where-Object { $_ -like 'ascii-sp=*' } | ForEach-Object { $_.Substring(9) })
        $script:expansionLines = @($script:probeLines | Where-Object { $_ -like 'expansion=*' })
    }

    It "is talking to the pinned collation, the only one these measurements are claims about" {
        $script:liveCollation | Should -BeExactly $script:pinnedCollation -Because "the fixture collation drifted; every parity claim below is scoped to $($script:pinnedCollation)"
    }

    It "admits no candidate the live server folds onto the reserved name - the no-false-negative invariant" {
        foreach ($label in $script:candidates.Keys) {
            $verdict = $script:verdicts[$label]
            if ($verdict.Admitted) {
                $script:liveEqual[$label] | Should -BeFalse -Because "'$label' is admitted by the authority, so the live server must see a distinct name - a failure here is a physical-separation hole"
            }
        }
    }

    It "refuses as a MEASURED collision only names the live server really folds onto the reserved one" {
        foreach ($label in $script:candidates.Keys) {
            $verdict = $script:verdicts[$label]
            if ($verdict.MeasuredRule) {
                $script:liveEqual[$label] | Should -BeTrue -Because "'$label' is refused by the inside-universe measured rule, which claims live equality; conservative refusals make no such claim and are not asserted here"
            }
        }
    }

    It "still finds exactly the 26 unordered case pairs equal in printable ASCII" {
        # Unordered pairs: the scan is restricted to a.value < b.value, so identity pairs are
        # excluded by construction (the directional form of the same scan would count 52).
        $expected = @(0x41..0x5A | ForEach-Object { '{0:X2}-{1:X2}' -f $_, ($_ + 0x20) })
        $script:pairLines.Count | Should -Be 26 -Because "the admitted universe's exactness claim rests on case pairs being the ONLY printable-ASCII equalities"
        Compare-Object -ReferenceObject $expected -DifferenceObject $script:pairLines | Should -BeNullOrEmpty
    }

    It "still finds no printable-ASCII character that is zero-weight, and only the space in the space class" {
        $script:asciiZeroWeightLines | Should -BeNullOrEmpty -Because "a zero-weight character inside the universe would let a name collide invisibly to the measured rule"
        $script:asciiSpaceClassLines | Should -Be @('20') -Because "only the space itself may fold onto a space, or TrimEnd's explicit set is no longer the whole story"
    }

    It "still finds no expansion between single characters and digraphs of the reserved name's alphabet" {
        $script:expansionLines | Should -BeNullOrEmpty -Because "an expansion inside the universe (the way U+FB01 equals 'fi' outside it) would break the measured rule's exactness"
    }

    It "corroborates the verdicts with DB_ID resolution when the reserved database exists" {
        # PRESENCE CHECK, not an assertion: on a target without the reserved database only this
        # corroboration skips - the COLLATE oracle and the scans above have already run in full.
        if (-not $script:reservedPresent) {
            Set-ItResult -Skipped -Because "the reserved database does not exist on this target; a null DB_ID is absence, never evidence of distinctness"
            return
        }
        foreach ($label in $script:candidates.Keys) {
            $verdict = $script:verdicts[$label]
            if ($verdict.Admitted) {
                $script:liveDbId[$label] | Should -Not -Be 'reserved' -Because "'$label' is admitted, so DB_ID must not resolve it to the reserved database"
            }
            elseif ($verdict.MeasuredRule) {
                $script:liveDbId[$label] | Should -Be 'reserved' -Because "'$label' is refused as a measured collision, so DB_ID must resolve it to the reserved database"
            }
        }
    }
}
