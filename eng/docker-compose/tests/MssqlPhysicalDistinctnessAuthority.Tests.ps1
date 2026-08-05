# Focused suite for the server-backed MSSQL topology-consistency authority: the UTF-16 hex
# transport, the batch generator, the strict output parser, the sqlcmd argument vector, and the
# Assert boundary that ties them to the bounded runner.
#
# Everything here runs WITHOUT docker or a server: the transport seam
# (Invoke-NativeCommandWithInput) is mocked inside the module, so the boundary's mode selection,
# candidate assembly, transport mapping, strict parsing, relation enforcement, and redaction are
# all pinned at unit level. The live half - real sqlcmd verdicts on real instances, including
# the case-sensitive and default-collation case-variant scenarios - belongs to the live suite.
#
# Every non-ASCII candidate is built from [char] code points so this file stays ASCII-only.

BeforeAll {
    Import-Module "$PSScriptRoot/../env-utility.psm1" -Force

    # Fail-safe module-table precondition - REFUSE, never repair. The boundary Describe's mocks
    # bind with -ModuleName env-utility, and Pester cannot bind them while more than one
    # same-named module is loaded. Whoever loaded another env-utility instance owns it - a
    # caller's own module, or a staging suite's leftover import - so this suite never removes a
    # module it did not load; it refuses to run and NAMES the foreign instances, attributing
    # the leak to its source instead of destroying caller state to keep itself green. The
    # staging suites unload their own staged imports in their own AfterAll; the whole-file
    # provenance Describe at the bottom of this file proves both halves of this contract.
    # -All, because that is the resolution the mocks live or die by: Pester refuses on
    # duplicates found with -All, and a staged wrapper module nests its staged env-utility
    # import - invisible to a top-level Get-Module, fatal to the mock binding (measured).
    $script:realModulePath = (Resolve-Path "$PSScriptRoot/../env-utility.psm1").Path
    $foreignInstances = @(Get-Module -Name env-utility -All | Where-Object {
            -not [string]::Equals($_.Path, $script:realModulePath, [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($foreignInstances.Count -gt 0) {
        $foreignList = @($foreignInstances | ForEach-Object { "'$($_.Path)'" }) -join ", "
        throw "MssqlPhysicalDistinctnessAuthority.Tests.ps1 precondition: additional env-utility module instances are loaded ($foreignList). Pester -ModuleName mocks cannot bind while same-named duplicates exist, and this suite does not remove modules it does not own - unload the extra instances at their source and re-run."
    }

    $script:reservedName = "edfi_configurationservice"
    $script:reviewerName = [char]0x00E9 + "dfi_configurationservice"

    function Script:New-TopologyEnvFile {
        # Writes a minimal effective env file for the boundary. The connection-string database
        # segment defaults to the reserved name so separate-mode tests satisfy the structural
        # requirements by default; pass -ConnectionString to control the string verbatim, and
        # -SeamName (even empty) to add a DMS_CONFIG_DATABASE_NAME declaration.
        param(
            [Parameter(Mandatory)] [string]$FileName,
            [Parameter(Mandatory)] [string]$Marker,
            [string]$DatastoreName = "",
            [string]$SeamName,
            [string]$ConnectionString = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=pw;TrustServerCertificate=true;",
            [switch]$OmitConnectionString
        )
        $path = Join-Path $TestDrive $FileName
        $content = "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=$Marker`n"
        if ($DatastoreName -ne "") {
            $content += "MSSQL_DB_NAME=$DatastoreName`n"
        }
        if ($PSBoundParameters.ContainsKey("SeamName")) {
            $content += "DMS_CONFIG_DATABASE_NAME=$SeamName`n"
        }
        if (-not $OmitConnectionString) {
            $content += "DMS_CONFIG_DATABASE_CONNECTION_STRING=$ConnectionString`n"
        }
        [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
        return $path
    }

    function Script:New-BatchOutput {
        # Well-formed batch output for the given per-key verdicts, matching the generator's
        # token vocabulary exactly (with the blank result-set separators sqlcmd emits).
        param(
            [Parameter(Mandatory)] [System.Collections.IDictionary]$VerdictBySourceKey,
            [string]$ExpectedToken = "present",
            [string]$Corroboration = "agree",
            [string]$ContextLine = "CMSTOPOLOGYCTX|db=master|collationAgreement=agree"
        )
        $lines = @($ContextLine, "", "CMSTOPOLOGYEXPECTED|$ExpectedToken", "")
        foreach ($sourceKey in $VerdictBySourceKey.Keys) {
            $lines += "CMSTOPOLOGYCAND|$sourceKey|$($VerdictBySourceKey[$sourceKey])|dbid=$Corroboration"
        }
        return ($lines -join "`n") + "`n"
    }

    function Script:New-TransportResult {
        param(
            [bool]$Started = $true,
            [bool]$TimedOut = $false,
            [bool]$StdinCompleted = $true,
            $ExitCode = 0,
            [string]$StandardOutput = "",
            [string]$StandardError = "",
            [string]$FailureKind = "None",
            [string]$FailureTypeName = ""
        )
        return [pscustomobject]@{
            Started         = $Started
            TimedOut        = $TimedOut
            StdinCompleted  = $StdinCompleted
            ExitCode        = $ExitCode
            StandardOutput  = $StandardOutput
            StandardError   = $StandardError
            FailureKind     = $FailureKind
            FailureTypeName = $FailureTypeName
        }
    }
}

Describe "ConvertTo-MssqlUtf16HexLiteral" {

    It "encodes per UTF-16 code unit, little-endian, losslessly - including an unpaired surrogate (mutant M-R6)" {
        # Encoding.Unicode.GetBytes replaces the unpaired surrogate with U+FFFD (fdff); the
        # per-code-unit form must preserve 00D8. This round-trip is the behavioral killer for
        # any lossy-encoding mutant.
        $value = "a" + [char]0xD800 + "b"
        $literal = ConvertTo-MssqlUtf16HexLiteral -Value $value
        $literal | Should -Be "0x610000D86200"

        $hex = $literal.Substring(2)
        $decoded = [string]::new(@(
                for ($i = 0; $i -lt $hex.Length; $i += 4) {
                    [char]([Convert]::ToInt32($hex.Substring($i + 2, 2) + $hex.Substring($i, 2), 16))
                }
            ))
        $decoded | Should -Be $value
    }

    It "round-trips representative candidate shapes" {
        foreach ($value in @(
                $script:reservedName,
                $script:reviewerName,
                ([char]0xFF45 + "dfi_configurationservice"),
                ("edfi" + [char]0x200D + "_configurationservice"),
                ($script:reservedName + " ")
            )) {
            $hex = (ConvertTo-MssqlUtf16HexLiteral -Value $value).Substring(2)
            $hex.Length | Should -Be ($value.Length * 4)
            $decoded = [string]::new(@(
                    for ($i = 0; $i -lt $hex.Length; $i += 4) {
                        [char]([Convert]::ToInt32($hex.Substring($i + 2, 2) + $hex.Substring($i, 2), 16))
                    }
                ))
            $decoded | Should -Be $value
        }
    }

    It "encodes the empty string as the empty binary literal" {
        ConvertTo-MssqlUtf16HexLiteral -Value "" | Should -Be "0x"
    }
}

Describe "New-MssqlTopologyConsistencyQuery emitted-SQL contract" {

    BeforeAll {
        $script:asciiCandidateName = "edfi_datastore_probe"
        $script:contractQuery = New-MssqlTopologyConsistencyQuery -ExpectedName $script:reviewerName -Candidate ([ordered]@{
                "MSSQL_DB_NAME"          = $script:reservedName
                "-DataStoreDatabaseName" = $script:asciiCandidateName
            })
    }

    It "emits pure ASCII with LF endings regardless of expected or candidate content" {
        $offending = @($script:contractQuery.ToCharArray() | Where-Object {
                [int]$_ -gt 0x7E -or ([int]$_ -lt 0x20 -and [int]$_ -ne 0x0A)
            })
        $offending.Count | Should -Be 0
    }

    It "never embeds expected or candidate text - names travel exclusively as hex" {
        # Even a pure-ASCII name must not appear as text: hex is the only transport, and no
        # textual N'...' literal exists anywhere in the batch. MatchExactly, because the
        # T-SQL Unicode-literal prefix is a capital N immediately before the quote - a
        # case-folding match would trip over the lowercase n ending 'Collation'.
        $script:contractQuery | Should -Not -Match ([regex]::Escape($script:asciiCandidateName))
        $script:contractQuery | Should -Not -MatchExactly "N'"
        $script:contractQuery | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $script:asciiCandidateName)))
        $script:contractQuery | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $script:reservedName)))
    }

    It "declares the expected name exactly once, as hex (mutant: expected comparand replaced)" {
        $expectedDeclaration = "DECLARE @expected nvarchar(max) = CONVERT(nvarchar(max), $(ConvertTo-MssqlUtf16HexLiteral -Value $script:reviewerName));"
        [regex]::Matches($script:contractQuery, [regex]::Escape($expectedDeclaration)).Count | Should -Be 1
        [regex]::Matches($script:contractQuery, "DECLARE @expected ").Count | Should -Be 1
    }

    It "carries both in-batch context assertions (mutant M-R4, emitted-SQL leg)" {
        $script:contractQuery | Should -Match ([regex]::Escape("CASE WHEN DB_NAME() = 'master'"))
        $script:contractQuery | Should -Match ([regex]::Escape("DATABASEPROPERTYEX('master', 'Collation')) = CONVERT(nvarchar(128), SERVERPROPERTY('Collation'))"))
    }

    It "hardcodes no collation name - the server's own master context supplies the semantics" {
        $script:contractQuery | Should -Not -Match "COLLATE"
        $script:contractQuery | Should -Not -Match "SQL_Latin1"
    }

    It "gates the DB_ID corroboration on expected-database presence" {
        $script:contractQuery | Should -Match ([regex]::Escape("CASE WHEN DB_ID(@expected) IS NULL THEN 'skipped'"))
        $script:contractQuery | Should -Match ([regex]::Escape("CMSTOPOLOGYEXPECTED|"))
        $script:contractQuery | Should -Match ([regex]::Escape("CASE WHEN DB_ID(@expected) IS NULL THEN 'absent' ELSE 'present' END"))
    }

    It "emits exactly one comparison line per candidate, keyed by source key, in order" {
        [regex]::Matches($script:contractQuery, [regex]::Escape("CMSTOPOLOGYCAND|MSSQL_DB_NAME|")).Count | Should -Be 1
        [regex]::Matches($script:contractQuery, [regex]::Escape("CMSTOPOLOGYCAND|-DataStoreDatabaseName|")).Count | Should -Be 1
        $script:contractQuery.IndexOf("CMSTOPOLOGYCAND|MSSQL_DB_NAME|") |
            Should -BeLessThan $script:contractQuery.IndexOf("CMSTOPOLOGYCAND|-DataStoreDatabaseName|")
        [regex]::Matches($script:contractQuery, [regex]::Escape("= @expected THEN 'equal' ELSE 'distinct'")).Count | Should -Be 2
    }

    It "terminates the batch and suppresses row-count chatter" {
        $script:contractQuery | Should -Match "(?m)^GO$"
        $script:contractQuery | Should -Match ([regex]::Escape("SET NOCOUNT ON;"))
    }

    It "rejects a source key that is not a simple ASCII identifier, and an empty candidate set" {
        { New-MssqlTopologyConsistencyQuery -ExpectedName "x" -Candidate ([ordered]@{ "bad key" = "x" }) } | Should -Throw "*simple ASCII identifier*"
        { New-MssqlTopologyConsistencyQuery -ExpectedName "x" -Candidate ([ordered]@{}) } | Should -Throw "*at least one candidate*"
    }
}

Describe "ConvertFrom-MssqlTopologyConsistencyQueryOutput strict parsing (mutant M-R8)" {

    It "classifies the exact happy set as ok, tolerating sqlcmd's blank separator lines" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "distinct" })
        $parsed = ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $output -ExpectedSourceKey @("DMS_CONFIG_DATABASE_CONNECTION_STRING", "MSSQL_DB_NAME")
        $parsed.Category | Should -Be "ok"
        $parsed.ExpectedPresent | Should -BeTrue
        $parsed.Verdict["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be "equal"
        $parsed.Verdict["MSSQL_DB_NAME"] | Should -Be "distinct"
    }

    It "returns each relation verdict intact - equal and distinct are both first-class" {
        foreach ($verdict in @("equal", "distinct")) {
            $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = $verdict })
            $parsed = ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")
            $parsed.Category | Should -Be "ok"
            $parsed.Verdict["MSSQL_DB_NAME"] | Should -Be $verdict
        }
    }

    It "accepts the fresh-stack shape: expected database absent with corroboration skipped" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ExpectedToken "absent" -Corroboration "skipped"
        $parsed = ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")
        $parsed.Category | Should -Be "ok"
        $parsed.ExpectedPresent | Should -BeFalse
    }

    It "classifies a failed context assertion, distinctly from garbage" {
        foreach ($badContext in @(
                "CMSTOPOLOGYCTX|db=other|collationAgreement=agree",
                "CMSTOPOLOGYCTX|db=master|collationAgreement=disagree"
            )) {
            $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ContextLine $badContext
            (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
                Should -Be "context-assertion"
        }
        $mangled = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ContextLine "CMSTOPOLOGYCTX|db=mangled"
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $mangled -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
    }

    It "classifies oracle disagreement" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -Corroboration "disagree"
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "oracle-disagreement"
    }

    It "refuses every malformed or incomplete shape as unexpected-output - exit code zero alone is never success" {
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "equal" })
        $cases = @(
            ""                                                                          # empty output
            "Msg 208, Level 16, State 1"                                                # error text only
            ($happy + "Msg 208, Level 16, State 1`n")                                   # trailing garbage
            ($happy -replace [regex]::Escape("CMSTOPOLOGYCTX|db=master|collationAgreement=agree`n"), "")   # missing context
            ($happy -replace [regex]::Escape("CMSTOPOLOGYEXPECTED|present`n"), "")      # missing expected-presence line
            ($happy -replace "equal", "maybe")                                          # unknown verdict token
            ($happy -replace [regex]::Escape("CMSTOPOLOGYEXPECTED|present"), "CMSTOPOLOGYEXPECTED|maybe") # unknown presence token
            ($happy -replace [regex]::Escape("|dbid=agree"), "")                        # missing corroboration
            ($happy + "CMSTOPOLOGYCAND|MSSQL_DB_NAME|equal|dbid=agree`n")               # duplicated candidate line
            ($happy -replace [regex]::Escape("dbid=agree"), "dbid=skipped")             # skipped while expected present
            (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "equal" }) -ExpectedToken "absent" -Corroboration "agree") # agree while absent
            (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "OTHER_KEY" = "equal" }))                    # wrong source key
        )
        foreach ($case in $cases) {
            (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $case -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
                Should -Be "unexpected-output" -Because "case: $($case.Substring(0, [math]::Min(40, $case.Length)))"
        }
        # A missing candidate line (one key expected, none present) is likewise refused.
        $noCandidates = "CMSTOPOLOGYCTX|db=master|collationAgreement=agree`nCMSTOPOLOGYEXPECTED|present`n"
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $noCandidates -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
        # Out-of-order candidate lines are refused: the batch emits keys in generation order.
        $swapped = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct"; "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal" })
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $swapped -ExpectedSourceKey @("DMS_CONFIG_DATABASE_CONNECTION_STRING", "MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
    }

    It "refuses case-mangled and padded token variants - the vocabulary is ordinal-exact" {
        # PowerShell's default string operators fold case, and a lenient trim would accept
        # padded lines: both were review-measured fail-open holes. Every variant below must be
        # unexpected-output - a case-mangled context line is GARBAGE, not a well-formed
        # assertion failure. String.Replace is used deliberately: it is ordinal, so the
        # replacement provably lands on the intended text.
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "equal" })
        $cases = @(
            $happy.Replace("db=master|collationAgreement=agree", "db=MASTER|collationAgreement=AGREE")
            $happy.Replace("CMSTOPOLOGYEXPECTED|present", "CMSTOPOLOGYEXPECTED|PRESENT")
            $happy.Replace("|equal|", "|EQUAL|")
            $happy.Replace("dbid=agree", "dbid=AGREE")
            $happy.Replace("CMSTOPOLOGYCTX|", " CMSTOPOLOGYCTX|")                       # leading pad
            $happy.Replace("dbid=agree", "dbid=agree ")                                 # trailing pad
            $happy.Replace("CMSTOPOLOGYEXPECTED|present", " CMSTOPOLOGYEXPECTED|present ")
            ($happy + "   `n")                                                          # whitespace-only line is content
        )
        foreach ($case in $cases) {
            (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $case -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
                Should -Be "unexpected-output" -Because "variant: $($case.Substring(0, [math]::Min(60, $case.Length)))"
        }
        $distinctHappy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" })
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $distinctHappy.Replace("|distinct|", "|DISTINCT|") -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
    }

    It "removes at most one terminal CR per line: CRLF output is accepted, a CR CR LF token line is refused" {
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "equal" })

        # Control: ordinary CRLF-terminated output must keep parsing ok.
        $crlfOutput = $happy.Replace("`n", "`r`n")
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $crlfOutput -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "ok"

        # A token line really ending CR CR LF carries a CR in its CONTENT. TrimEnd erased the
        # whole run and accepted it (review-measured); at-most-one removal keeps the extra CR
        # and refuses the line.
        $doubleCrContext = $happy.Replace("collationAgreement=agree`n", "collationAgreement=agree`r`r`n")
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $doubleCrContext -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
        $doubleCrCandidate = $happy.Replace("dbid=agree`n", "dbid=agree`r`r`n")
        (ConvertFrom-MssqlTopologyConsistencyQueryOutput -OutputText $doubleCrCandidate -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
    }

    It "uses ordinal StartsWith overloads throughout the parser (structural - downstream exact checks shield behavior)" {
        # Culture-sensitive StartsWith can match across ignorable characters, but every
        # downstream check is an ordinal exact comparison on a length-based substring, so a
        # culture-matched mangled line still classifies as unexpected-output - the mutant is
        # behaviorally shielded, and this pin is its only killer, reported honestly.
        $tokens = $null
        $parseErrors = $null
        $moduleAst = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path "$PSScriptRoot/../env-utility.psm1").Path, [ref]$tokens, [ref]$parseErrors)
        $parserAst = $moduleAst.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq "ConvertFrom-MssqlTopologyConsistencyQueryOutput"
            }, $true)
        $parserAst | Should -Not -BeNullOrEmpty
        $bareStartsWith = @([regex]::Matches($parserAst.Extent.Text, '\.StartsWith\([^\)]*\)') |
                Where-Object { $_.Value -notmatch 'Ordinal' })
        $bareStartsWith.Count | Should -Be 0
    }
}

Describe "New-MssqlTopologySqlcmdArgument" {

    It "pins the exact argument vector, element by element, -d master included (mutant M-R11, static leg)" {
        $argumentVector = New-MssqlTopologySqlcmdArgument -ContainerName "dms-mssql" -SaPassword "sentinel-pw"
        $expected = @(
            "exec", "-i", "-e", "SQLCMDPASSWORD=sentinel-pw", "dms-mssql",
            "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa",
            "-d", "master", "-C", "-b", "-h", "-1", "-W"
        )
        $argumentVector.Count | Should -Be $expected.Count
        for ($i = 0; $i -lt $expected.Count; $i++) {
            $argumentVector[$i] | Should -Be $expected[$i] -Because "argv element $i"
        }
    }
}

Describe "Assert-MssqlTopologyPhysicalConsistency boundary" {

    BeforeAll {
        # Ambient hermeticity. Production deliberately gives ambient MSSQL_DB_NAME and
        # DMS_CONFIG_DATABASE_NAME Compose precedence over the file, and deliberately IGNORES an
        # ambient marker (raw file read) - so this Describe must control all three variables per
        # test AND hand back the caller's exact pre-existing state afterwards: present with its
        # value, absent, or (where the platform can represent it) present-empty.
        # SetEnvironmentVariable is used for the restore because it preserves a present-empty
        # value on Windows; on Unix a blank variable cannot exist, so absent is the faithful
        # representation there.
        $script:ambientNames = @("DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE", "MSSQL_DB_NAME", "DMS_CONFIG_DATABASE_NAME")
        $script:ambientSnapshot = @{}
        foreach ($name in $script:ambientNames) {
            $script:ambientSnapshot[$name] = @{
                Present = [bool](Test-Path "Env:\$name")
                Value   = [System.Environment]::GetEnvironmentVariable($name)
            }
        }
    }

    BeforeEach {
        foreach ($name in $script:ambientNames) {
            Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
        }
    }

    AfterAll {
        foreach ($name in $script:ambientNames) {
            if ($script:ambientSnapshot[$name].Present) {
                [System.Environment]::SetEnvironmentVariable($name, $script:ambientSnapshot[$name].Value)
            }
            else {
                Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
            }
        }
    }

    It "runs in shared mode - marker read raw from the file, ambient marker ignored - and asks the server with the datastore as expected (mutants: separate-only gate kept, shared-mode comparison removed)" {
        # The review-measured regression this flips: shared mode used to render NO live verdict
        # at all. Now every CMS-participating start asks the server, and the ambient marker
        # cannot flip the mode.
        $sharedDatastore = [char]0x00E9 + "dfi_shared_datastore"
        $expectedQuery = New-MssqlTopologyConsistencyQuery -ExpectedName $sharedDatastore -Candidate ([ordered]@{
                "DMS_CONFIG_DATABASE_CONNECTION_STRING" = $sharedDatastore
            })
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"
        $envFile = New-TopologyEnvFile -FileName "shared.env" -Marker "false" -DatastoreName $sharedDatastore `
            -ConnectionString "Server=dms-mssql,1433;Database=$sharedDatastore;User Id=sa;Password=pw;TrustServerCertificate=true;"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 1 -Exactly
        $script:capturedInputText | Should -Be $expectedQuery
    }

    It "throws in shared mode when the server reports a CMS target physically different from the datastore" {
        $different = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "distinct" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult -StandardOutput $different }

        $envFile = New-TopologyEnvFile -FileName "shared-diff.env" -Marker "false" -DatastoreName "edfi_datastore" `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_other;User Id=sa;Password=pw;TrustServerCertificate=true;"
        $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
        $thrown | Should -Throw "*DIFFERENT physical database*"
        $thrown | Should -Throw "*'DMS_CONFIG_DATABASE_CONNECTION_STRING'*"
        try { & $thrown } catch { $failureMessage = $_.Exception.Message }
        $failureMessage | Should -Not -Match "edfi_other"
        $failureMessage | Should -Match "withheld"
    }

    It "verifies separate mode through the runner exactly once, sending the generated batch over the pinned argv" {
        $expectedQuery = New-MssqlTopologyConsistencyQuery -ExpectedName $script:reservedName -Candidate ([ordered]@{
                "DMS_CONFIG_DATABASE_CONNECTION_STRING" = $script:reservedName
                "MSSQL_DB_NAME"                         = $script:reviewerName
            })
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "distinct" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedFilePath = $FilePath
            $script:capturedArgumentList = @($ArgumentList)
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $envFile = New-TopologyEnvFile -FileName "sep-ok.env" -Marker "true" -DatastoreName $script:reviewerName
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 1 -Exactly
        $script:capturedFilePath | Should -Be "docker"
        $script:capturedInputText | Should -Be $expectedQuery
        ($script:capturedArgumentList -join "`u{1}") |
            Should -Be ((New-MssqlTopologySqlcmdArgument -ContainerName "dms-mssql" -SaPassword "pw") -join "`u{1}")
    }

    It "throws in separate mode when a CMS target is physically different from the reserved database (mutant: separate-mode CMS-agreement comparison removed)" {
        $badCms = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "distinct"; "MSSQL_DB_NAME" = "distinct" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult -StandardOutput $badCms }

        $envFile = New-TopologyEnvFile -FileName "sep-badcms.env" -Marker "true" -DatastoreName "edfi_datastore" `
            -ConnectionString "Server=dms-mssql,1433;Database=$($script:reviewerName);User Id=sa;Password=pw;TrustServerCertificate=true;"
        $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
        $thrown | Should -Throw "*DIFFERENT physical database*"
        $thrown | Should -Throw "*'DMS_CONFIG_DATABASE_CONNECTION_STRING'*"
        try { & $thrown } catch { $failureMessage = $_.Exception.Message }
        $failureMessage | Should -Not -Match ([regex]::Escape($script:reviewerName))
        $failureMessage | Should -Match "withheld"
    }

    It "includes the provider-parsed registered candidate when supplied in separate mode, and a same-database verdict names the parameter - never the value" {
        $registeredValue = [char]0x00E9 + "dfi_registered"
        $collision = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "distinct"; "-DataStoreDatabaseName" = "equal" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $collision
        }

        $envFile = New-TopologyEnvFile -FileName "sep-reg.env" -Marker "true" -DatastoreName "edfi_datastore"
        $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" -RegisteredDatastoreDatabaseName $registeredValue }
        $thrown | Should -Throw "*'-DataStoreDatabaseName'*"
        $thrown | Should -Throw "*SAME physical database*"
        $thrown | Should -Throw "*edfi_configurationservice*"
        try { & $thrown } catch { $failureMessage = $_.Exception.Message }
        $failureMessage | Should -Not -Match ([regex]::Escape($registeredValue))
        $script:capturedInputText | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $registeredValue)))
    }

    It "excludes the registered candidate in shared mode - it is the separate-mode distinctness rule's participant only" {
        $registeredValue = "edfi_registered_shared"
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $envFile = New-TopologyEnvFile -FileName "shared-reg.env" -Marker "false" -DatastoreName "edfi_datastore" `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_datastore;User Id=sa;Password=pw;TrustServerCertificate=true;"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" -RegisteredDatastoreDatabaseName $registeredValue } |
            Should -Not -Throw
        $script:capturedInputText | Should -Not -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $registeredValue)))
        $script:capturedInputText | Should -Not -Match ([regex]::Escape("-DataStoreDatabaseName"))
    }

    It "throws the same-database diagnostic for the datastore candidate in separate mode, withholding the resolved value" {
        $collision = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "equal" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult -StandardOutput $collision }

        $envFile = New-TopologyEnvFile -FileName "sep-collide.env" -Marker "true" -DatastoreName $script:reviewerName
        $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
        $thrown | Should -Throw "*CMS database topology mismatch*"
        $thrown | Should -Throw "*'MSSQL_DB_NAME'*"
        try { & $thrown } catch { $failureMessage = $_.Exception.Message }
        $failureMessage | Should -Not -Match ([regex]::Escape($script:reviewerName))
        $failureMessage | Should -Match "withheld"
    }

    It "sends the declared seam as a first-class candidate, before the connection-string segments (mutant: seam candidate omitted)" {
        $seamValue = [char]0x00E9 + "dfi_seam_value"
        $expectedQuery = New-MssqlTopologyConsistencyQuery -ExpectedName "edfi_datastore" -Candidate ([ordered]@{
                "DMS_CONFIG_DATABASE_NAME"              = $seamValue
                "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "edfi_datastore"
            })
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_NAME" = "equal"; "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $envFile = New-TopologyEnvFile -FileName "shared-seam.env" -Marker "false" -DatastoreName "edfi_datastore" -SeamName $seamValue `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_datastore;User Id=sa;Password=pw;TrustServerCertificate=true;"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        $script:capturedInputText | Should -Be $expectedQuery
    }

    It "verifies every connection-string database segment independently, keyed by position (mutant: connection-string candidate omitted)" {
        $secondSegmentBad = New-BatchOutput -VerdictBySourceKey ([ordered]@{
                "DMS_CONFIG_DATABASE_CONNECTION_STRING"   = "equal"
                "DMS_CONFIG_DATABASE_CONNECTION_STRING.2" = "distinct"
            })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $secondSegmentBad
        }

        $envFile = New-TopologyEnvFile -FileName "shared-dual.env" -Marker "false" -DatastoreName "edfi_datastore" `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_datastore;Initial Catalog=edfi_other;User Id=sa;Password=pw;TrustServerCertificate=true;"
        $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
        $thrown | Should -Throw "*'DMS_CONFIG_DATABASE_CONNECTION_STRING.2'*"
        $script:capturedInputText | Should -Match ([regex]::Escape("CMSTOPOLOGYCAND|DMS_CONFIG_DATABASE_CONNECTION_STRING|"))
        $script:capturedInputText | Should -Match ([regex]::Escape("CMSTOPOLOGYCAND|DMS_CONFIG_DATABASE_CONNECTION_STRING.2|"))
    }

    It "fails closed as unverifiable for every transport failure shape, with type names only" {
        $transportShapes = @(
            @{ Label = "start failure"; Result = New-TransportResult -Started $false -ExitCode $null -FailureKind "StartFailure" -FailureTypeName "System.ComponentModel.Win32Exception" }
            @{ Label = "timeout"; Result = New-TransportResult -TimedOut $true -ExitCode $null -StdinCompleted $false }
            @{ Label = "stdin incomplete"; Result = New-TransportResult -StdinCompleted $false -FailureKind "StdinFailure" -FailureTypeName "System.IO.IOException" }
            @{ Label = "termination failure"; Result = New-TransportResult -ExitCode $null -FailureKind "TerminationFailure" }
            @{ Label = "nonzero exit"; Result = New-TransportResult -ExitCode 1 -StandardError "Sqlcmd: Error: SENTINEL-STDERR." }
        )
        foreach ($shape in $transportShapes) {
            $shapeResult = $shape.Result
            Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { $shapeResult }.GetNewClosure()
            $envFile = New-TopologyEnvFile -FileName "sep-transport.env" -Marker "true" -DatastoreName "edfi_datastore"
            $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
            $thrown | Should -Throw "*could not be confirmed*" -Because $shape.Label
            try { & $thrown } catch { $failureMessage = $_.Exception.Message }
            $failureMessage | Should -Not -Match "SENTINEL-STDERR" -Because "child output never reaches diagnostics"
            $failureMessage | Should -Match "withheld"
        }
    }

    It "fails closed for every non-ok parse category - exit code zero alone is never success (mutant: strict parsing weakened)" {
        $parseShapes = @(
            @{ Label = "garbage stdout with exit zero"; Output = "Msg 208, Level 16, State 1"; Category = "unexpected-output" }
            @{ Label = "context assertion"; Output = (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "distinct" }) -ContextLine "CMSTOPOLOGYCTX|db=other|collationAgreement=agree"); Category = "context-assertion" }
            @{ Label = "oracle disagreement"; Output = (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "distinct" }) -Corroboration "disagree"); Category = "oracle-disagreement" }
        )
        foreach ($shape in $parseShapes) {
            $shapeTransport = New-TransportResult -StandardOutput $shape.Output
            Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { $shapeTransport }.GetNewClosure()
            $envFile = New-TopologyEnvFile -FileName "sep-parse.env" -Marker "true" -DatastoreName "edfi_datastore"
            { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
                Should -Throw "*($($shape.Category))*" -Because $shape.Label
        }
    }

    It "resolves candidates with ambient Compose precedence - the checked names are what the stack will receive" {
        # Ambient MSSQL_DB_NAME moves the running datastore, so in shared mode it moves the
        # EXPECTED name; ambient DMS_CONFIG_DATABASE_NAME is a first-class candidate even when
        # the file never declares the seam.
        $ambientDatastore = [char]0x00E9 + "dfi_ambient_datastore"
        $ambientSeam = [char]0x00E9 + "dfi_ambient_seam"
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_NAME" = "equal"; "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $env:MSSQL_DB_NAME = $ambientDatastore
        $env:DMS_CONFIG_DATABASE_NAME = $ambientSeam
        $envFile = New-TopologyEnvFile -FileName "shared-ambient.env" -Marker "false" -DatastoreName "edfi_file_value" `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_file_value;User Id=sa;Password=pw;TrustServerCertificate=true;"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        $expectedQuery = New-MssqlTopologyConsistencyQuery -ExpectedName $ambientDatastore -Candidate ([ordered]@{
                "DMS_CONFIG_DATABASE_NAME"              = $ambientSeam
                "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "edfi_file_value"
            })
        $script:capturedInputText | Should -Be $expectedQuery
    }

    It "fails before any transport when the ambient environment supplies a blank seam - named key only, value withheld (mutant: whitespace-skipping ambient guard restored)" {
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult }
        $envFile = New-TopologyEnvFile -FileName "shared-blankseam.env" -Marker "false" -DatastoreName "edfi_datastore" `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_datastore;User Id=sa;Password=pw;TrustServerCertificate=true;"
        foreach ($blankShape in @("", " ", "`t")) {
            if ($blankShape -eq "" -and -not $IsWindows) {
                continue # a present-empty environment variable cannot exist on Unix
            }
            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", $blankShape)
            $thrown = { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
            $thrown | Should -Throw "*empty or whitespace-only value*" -Because "ambient shape: [$blankShape]"
            $thrown | Should -Throw "*'DMS_CONFIG_DATABASE_NAME'*"
            Remove-Item "Env:\DMS_CONFIG_DATABASE_NAME" -ErrorAction SilentlyContinue
        }
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 0 -Exactly
    }

    It "fails before any transport when the file declares a blank seam" {
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult }
        $envFile = New-TopologyEnvFile -FileName "shared-declblank.env" -Marker "false" -DatastoreName "edfi_datastore" -SeamName "" `
            -ConnectionString "Server=dms-mssql,1433;Database=edfi_datastore;User Id=sa;Password=pw;TrustServerCertificate=true;"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Throw "*resolves to an empty or whitespace-only value*"
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 0 -Exactly
    }

    It "reports structural configuration failures before any transport, naming the key" {
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult }

        $blankDatastore = New-TopologyEnvFile -FileName "sep-blank.env" -Marker "true"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $blankDatastore -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Throw "*MSSQL_DB_NAME*"

        $noConnectionString = New-TopologyEnvFile -FileName "sep-nocs.env" -Marker "true" -DatastoreName "edfi_datastore" -OmitConnectionString
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $noConnectionString -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Throw "*DMS_CONFIG_DATABASE_CONNECTION_STRING*"

        $noSegment = New-TopologyEnvFile -FileName "sep-noseg.env" -Marker "true" -DatastoreName "edfi_datastore" `
            -ConnectionString "Server=dms-mssql,1433;User Id=sa;Password=pw;TrustServerCertificate=true;"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $noSegment -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Throw "*Database or Initial Catalog*"

        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 0 -Exactly
    }

    It "interprets raw marker spellings exactly like the topology validator: only a declared ordinal 'true' selects separate semantics" {
        # The mode decides which EXPECTED name the batch compares against, so the declared
        # @expected hex is the observable verdict for each spelling row. The mock returns no
        # output, so every call ends unverifiable - the captured batch is the assertion target.
        $rows = @(
            @{ Line = $null; Ambient = $null; Separate = $false; Label = "no declaration" }
            @{ Line = "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false"; Ambient = $null; Separate = $false; Label = "declared false" }
            @{ Line = "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=TRUE"; Ambient = $null; Separate = $false; Label = "case variant is not a topology declaration (ordinal)" }
            @{ Line = "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true"; Ambient = $null; Separate = $true; Label = "declared true" }
            @{ Line = 'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE="true"'; Ambient = $null; Separate = $true; Label = "double-quoted true unwraps like Compose" }
            @{ Line = $null; Ambient = "true"; Separate = $false; Label = "ambient-only value is not a file declaration" }
        )
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult
        }
        $datastoreHexDeclaration = "DECLARE @expected nvarchar(max) = CONVERT(nvarchar(max), $(ConvertTo-MssqlUtf16HexLiteral -Value 'edfi_datastore'));"
        $reservedHexDeclaration = "DECLARE @expected nvarchar(max) = CONVERT(nvarchar(max), $(ConvertTo-MssqlUtf16HexLiteral -Value $script:reservedName));"
        foreach ($row in $rows) {
            if ($null -ne $row.Ambient) { $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = $row.Ambient }
            $lines = @("MSSQL_DB_NAME=edfi_datastore", "DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=pw;TrustServerCertificate=true;")
            if ($null -ne $row.Line) { $lines = @($row.Line) + $lines }
            $path = Join-Path $TestDrive ".env.marker-$([Guid]::NewGuid().ToString('N'))"
            [System.IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
            $script:capturedInputText = $null
            try { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $path -ContainerName "dms-mssql" -SaPassword "pw" } catch { $null = $_ }
            $expectedDeclaration = if ($row.Separate) { $reservedHexDeclaration } else { $datastoreHexDeclaration }
            $script:capturedInputText | Should -Match ([regex]::Escape($expectedDeclaration)) -Because $row.Label
            Remove-Item "Env:\DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE" -Force -ErrorAction SilentlyContinue
        }
    }

    It "selects separate semantics from the file marker even when the ambient marker says otherwise" {
        $expectedQuery = New-MssqlTopologyConsistencyQuery -ExpectedName $script:reservedName -Candidate ([ordered]@{
                "DMS_CONFIG_DATABASE_CONNECTION_STRING" = $script:reservedName
                "MSSQL_DB_NAME"                         = "edfi_datastore"
            })
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "DMS_CONFIG_DATABASE_CONNECTION_STRING" = "equal"; "MSSQL_DB_NAME" = "distinct" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "false"
        $envFile = New-TopologyEnvFile -FileName "sep-ambientmarker.env" -Marker "true" -DatastoreName "edfi_datastore"
        { Assert-MssqlTopologyPhysicalConsistency -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        $script:capturedInputText | Should -Be $expectedQuery
    }
}

Describe "single SQL-generation authority (mutant M-R7)" {

    It "keeps query construction and its token vocabulary out of every production script and module except env-utility" {
        $productionFiles = @(
            Get-ChildItem -Path "$PSScriptRoot/.." -File -Force |
                Where-Object { $_.Extension -in @(".ps1", ".psm1") -and $_.Name -ne "env-utility.psm1" }
        )
        $productionFiles.Count | Should -BeGreaterThan 10
        foreach ($file in $productionFiles) {
            $content = Get-Content -LiteralPath $file.FullName -Raw
            $content | Should -Not -Match "CMSTOPOLOGYCAND" -Because $file.Name
            $content | Should -Not -Match ([regex]::Escape("CONVERT(nvarchar(max), 0x")) -Because $file.Name
        }
    }
}

Describe "whole-file module-table provenance (post-Invoke-Pester, isolated children)" {
    # A green in-file assertion proves nothing about what the file leaves in - or takes from -
    # the caller's module table; only inspecting the session AFTER Invoke-Pester returns does.
    # Both children exclude this tag, so there is no recursion. Child processes launch via
    # [Environment]::ProcessPath, never a literal executable name.

    BeforeAll {
        $script:childWork = Join-Path $TestDrive "module-provenance"
        New-Item -ItemType Directory -Path $script:childWork -Force | Out-Null
    }

    It "leaves exactly the one real env-utility instance after an ordinary run" -Tag "WholeFileModuleProvenance" {
        $childScript = Join-Path $script:childWork "provenance-ordinary.ps1"
        @(
            "`$result = Invoke-Pester -Path '$PSCommandPath' -ExcludeTagFilter 'WholeFileModuleProvenance' -Output None -PassThru",
            "`$instances = @(Get-Module -Name env-utility -All)",
            "[pscustomobject]@{",
            "    Failed = `$result.FailedCount; Total = `$result.TotalCount",
            "    InstanceCount = `$instances.Count",
            "    InstancePath = @(`$instances | ForEach-Object { `$_.Path }) -join ';'",
            "} | ConvertTo-Json -Compress"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ([Environment]::ProcessPath) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        $childState.Failed | Should -Be 0
        $childState.Total | Should -BeGreaterThan 25
        $childState.InstanceCount | Should -Be 1 -Because "the suite imports the one real module and nothing else survives it"
        $childState.InstancePath | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../env-utility.psm1"))) -Because "provenance: the surviving instance must be the repository module"
    }

    It "refuses to run beside a caller-owned same-name module and hands it back untouched" -Tag "WholeFileModuleProvenance" {
        # The review-measured regression this pins: a suite that deletes every env-utility
        # instance to make its mocks bindable runs green while destroying a caller-owned
        # module. The contract is the opposite - with a caller-owned env-utility loaded, the
        # suite FAILS on its own named precondition (never Pester's raw duplicate-module
        # error), and the caller's module, its exported command, and its provenance all
        # survive the complete Pester lifecycle.
        $childScript = Join-Path $script:childWork "provenance-sentinel.ps1"
        @(
            "`$null = New-Module -Name env-utility -ScriptBlock {",
            "    function Get-EnvUtilityCallerSentinel { 'caller-owned-sentinel' }",
            "    Export-ModuleMember -Function Get-EnvUtilityCallerSentinel",
            "} | Import-Module -PassThru",
            "`$result = Invoke-Pester -Path '$PSCommandPath' -ExcludeTagFilter 'WholeFileModuleProvenance' -Output None -PassThru",
            "# A root-BeforeAll throw is recorded on the CONTAINER's error record (measured), so the",
            "# attribution probe reads both the per-test and the container records.",
            "`$errorText = @(",
            "    @(`$result.Failed | ForEach-Object { `$_.ErrorRecord } | ForEach-Object { [string]`$_ }) +",
            "    @(`$result.Containers | ForEach-Object { `$_.ErrorRecord } | ForEach-Object { [string]`$_ })",
            ") -join ' '",
            "`$sentinelModule = @(Get-Module -Name env-utility | Where-Object { `$_.ExportedCommands.ContainsKey('Get-EnvUtilityCallerSentinel') })",
            "`$sentinelCommand = Get-Command Get-EnvUtilityCallerSentinel -ErrorAction SilentlyContinue",
            "[pscustomobject]@{",
            "    Failed = `$result.FailedCount",
            "    PreconditionNamed = `$errorText.Contains('does not remove modules it does not own')",
            "    SentinelModulePresent = (`$sentinelModule.Count -eq 1)",
            "    SentinelOutput = if (`$sentinelCommand) { Get-EnvUtilityCallerSentinel } else { 'command-gone' }",
            "    SentinelProvenance = if (`$sentinelCommand) { [string]`$sentinelCommand.Module.Name } else { 'command-gone' }",
            "} | ConvertTo-Json -Compress"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ([Environment]::ProcessPath) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        $childState.Failed | Should -BeGreaterThan 0 -Because "the suite must refuse rather than run beside an unbindable duplicate"
        $childState.PreconditionNamed | Should -BeTrue -Because "the refusal must be this suite's own attributing precondition, not Pester's raw duplicate-module error"
        $childState.SentinelModulePresent | Should -BeTrue -Because "a caller-owned module is not this suite's to remove"
        $childState.SentinelOutput | Should -Be 'caller-owned-sentinel'
        $childState.SentinelProvenance | Should -Be 'env-utility' -Because "the surviving command must still come from the caller's own module"
    }
}
