# Focused suite for the server-backed MSSQL physical-identity authority: the UTF-16 hex
# transport, the batch generator, the strict output parser, the sqlcmd argument vector, and the
# Assert boundary that ties them to the bounded runner.
#
# Everything here runs WITHOUT docker or a server: the transport seam
# (Invoke-NativeCommandWithInput) is mocked inside the module, so the boundary's gating,
# transport mapping, strict parsing, collision handling, and redaction are all pinned at unit
# level. The live half - real sqlcmd verdicts on real instances, including the case-sensitive
# and alternate-default-database scenarios - belongs to the live suite phase.
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
        param(
            [Parameter(Mandatory)] [string]$FileName,
            [Parameter(Mandatory)] [string]$Marker,
            [string]$DatastoreName = ""
        )
        $path = Join-Path $TestDrive $FileName
        $content = "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=$Marker`n"
        if ($DatastoreName -ne "") {
            $content += "MSSQL_DB_NAME=$DatastoreName`n"
        }
        [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
        return $path
    }

    function Script:New-BatchOutput {
        # Well-formed batch output for the given per-key verdicts, matching the generator's
        # token vocabulary exactly (with the blank result-set separators sqlcmd emits).
        param(
            [Parameter(Mandatory)] [System.Collections.IDictionary]$VerdictBySourceKey,
            [string]$ReservedToken = "present",
            [string]$Corroboration = "agree",
            [string]$ContextLine = "CMSTOPOLOGYCTX|db=master|collationAgreement=agree"
        )
        $lines = @($ContextLine, "", "CMSTOPOLOGYRESERVED|$ReservedToken", "")
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

Describe "New-MssqlPhysicalDistinctnessQuery emitted-SQL contract" {

    BeforeAll {
        $script:asciiCandidateName = "edfi_datastore_probe"
        $script:contractQuery = New-MssqlPhysicalDistinctnessQuery -Candidate ([ordered]@{
                "MSSQL_DB_NAME"          = $script:reviewerName
                "-DataStoreDatabaseName" = $script:asciiCandidateName
            })
    }

    It "emits pure ASCII with LF endings regardless of candidate content" {
        $offending = @($script:contractQuery.ToCharArray() | Where-Object {
                [int]$_ -gt 0x7E -or ([int]$_ -lt 0x20 -and [int]$_ -ne 0x0A)
            })
        $offending.Count | Should -Be 0
    }

    It "never embeds candidate text - candidates travel exclusively as hex" {
        # Even a pure-ASCII candidate must not appear as text: hex is the only transport.
        $script:contractQuery | Should -Not -Match ([regex]::Escape($script:asciiCandidateName))
        $script:contractQuery | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $script:asciiCandidateName)))
        $script:contractQuery | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $script:reviewerName)))
    }

    It "carries both in-batch context assertions (mutant M-R4, emitted-SQL leg)" {
        $script:contractQuery | Should -Match ([regex]::Escape("CASE WHEN DB_NAME() = 'master'"))
        $script:contractQuery | Should -Match ([regex]::Escape("DATABASEPROPERTYEX('master', 'Collation')) = CONVERT(nvarchar(128), SERVERPROPERTY('Collation'))"))
    }

    It "hardcodes no collation name - the server's own master context supplies the semantics" {
        $script:contractQuery | Should -Not -Match "COLLATE"
        $script:contractQuery | Should -Not -Match "SQL_Latin1"
    }

    It "gates the DB_ID corroboration on reserved-database presence" {
        $script:contractQuery | Should -Match ([regex]::Escape("CASE WHEN DB_ID(@reserved) IS NULL THEN 'skipped'"))
        $script:contractQuery | Should -Match ([regex]::Escape("CMSTOPOLOGYRESERVED|"))
    }

    It "emits exactly one comparison line per candidate, keyed by source key, in order" {
        [regex]::Matches($script:contractQuery, [regex]::Escape("CMSTOPOLOGYCAND|MSSQL_DB_NAME|")).Count | Should -Be 1
        [regex]::Matches($script:contractQuery, [regex]::Escape("CMSTOPOLOGYCAND|-DataStoreDatabaseName|")).Count | Should -Be 1
        $script:contractQuery.IndexOf("CMSTOPOLOGYCAND|MSSQL_DB_NAME|") |
            Should -BeLessThan $script:contractQuery.IndexOf("CMSTOPOLOGYCAND|-DataStoreDatabaseName|")
        [regex]::Matches($script:contractQuery, [regex]::Escape("= @reserved THEN 'collides'")).Count | Should -Be 2
    }

    It "declares the reserved literal exactly once and terminates the batch" {
        [regex]::Matches($script:contractQuery, [regex]::Escape("N'edfi_configurationservice'")).Count | Should -Be 1
        $script:contractQuery | Should -Match "(?m)^GO$"
        $script:contractQuery | Should -Match ([regex]::Escape("SET NOCOUNT ON;"))
    }

    It "rejects a source key that is not a simple ASCII identifier, and an empty candidate set" {
        { New-MssqlPhysicalDistinctnessQuery -Candidate ([ordered]@{ "bad key" = "x" }) } | Should -Throw "*simple ASCII identifier*"
        { New-MssqlPhysicalDistinctnessQuery -Candidate ([ordered]@{}) } | Should -Throw "*at least one candidate*"
    }
}

Describe "ConvertFrom-MssqlPhysicalDistinctnessQueryOutput strict parsing (mutant M-R8)" {

    It "classifies the exact happy set as ok, tolerating sqlcmd's blank separator lines" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct"; "-DataStoreDatabaseName" = "distinct" })
        $parsed = ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME", "-DataStoreDatabaseName")
        $parsed.Category | Should -Be "ok"
        $parsed.ReservedPresent | Should -BeTrue
        $parsed.Verdict["MSSQL_DB_NAME"] | Should -Be "distinct"
        $parsed.Verdict["-DataStoreDatabaseName"] | Should -Be "distinct"
    }

    It "returns collision verdicts intact" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "collides" })
        $parsed = ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")
        $parsed.Category | Should -Be "ok"
        $parsed.Verdict["MSSQL_DB_NAME"] | Should -Be "collides"
    }

    It "accepts the fresh-stack shape: reserved absent with corroboration skipped" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ReservedToken "absent" -Corroboration "skipped"
        $parsed = ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")
        $parsed.Category | Should -Be "ok"
        $parsed.ReservedPresent | Should -BeFalse
    }

    It "classifies a failed context assertion, distinctly from garbage" {
        foreach ($badContext in @(
                "CMSTOPOLOGYCTX|db=other|collationAgreement=agree",
                "CMSTOPOLOGYCTX|db=master|collationAgreement=disagree"
            )) {
            $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ContextLine $badContext
            (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
                Should -Be "context-assertion"
        }
        $mangled = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ContextLine "CMSTOPOLOGYCTX|db=mangled"
        (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $mangled -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
    }

    It "classifies oracle disagreement" {
        $output = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -Corroboration "disagree"
        (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $output -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "oracle-disagreement"
    }

    It "refuses every malformed or incomplete shape as unexpected-output - exit code zero alone is never success" {
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" })
        $cases = @(
            ""                                                                          # empty output
            "Msg 208, Level 16, State 1"                                                # error text only
            ($happy + "Msg 208, Level 16, State 1`n")                                   # trailing garbage
            ($happy -replace [regex]::Escape("CMSTOPOLOGYCTX|db=master|collationAgreement=agree`n"), "")   # missing context
            ($happy -replace "distinct", "maybe")                                       # unknown verdict token
            ($happy -replace [regex]::Escape("|dbid=agree"), "")                        # missing corroboration
            ($happy + "CMSTOPOLOGYCAND|MSSQL_DB_NAME|distinct|dbid=agree`n")            # duplicated candidate line
            ($happy -replace [regex]::Escape("dbid=agree"), "dbid=skipped")             # skipped while reserved present
            (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ReservedToken "absent" -Corroboration "agree") # agree while absent
            (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "OTHER_KEY" = "distinct" }))                 # wrong source key
        )
        foreach ($case in $cases) {
            (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $case -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
                Should -Be "unexpected-output" -Because "case: $($case.Substring(0, [math]::Min(40, $case.Length)))"
        }
        # A missing candidate line (one key expected, none present) is likewise refused.
        $noCandidates = "CMSTOPOLOGYCTX|db=master|collationAgreement=agree`nCMSTOPOLOGYRESERVED|present`n"
        (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $noCandidates -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
    }

    It "refuses case-mangled and padded token variants - the vocabulary is ordinal-exact" {
        # PowerShell's default string operators fold case, and a lenient trim would accept
        # padded lines: both were review-measured fail-open holes. Every variant below must be
        # unexpected-output - a case-mangled context line is GARBAGE, not a well-formed
        # assertion failure. String.Replace is used deliberately: it is ordinal, so the
        # replacement provably lands on the intended text.
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" })
        $cases = @(
            $happy.Replace("db=master|collationAgreement=agree", "db=MASTER|collationAgreement=AGREE")
            $happy.Replace("CMSTOPOLOGYRESERVED|present", "CMSTOPOLOGYRESERVED|PRESENT")
            $happy.Replace("|distinct|", "|DISTINCT|")
            $happy.Replace("dbid=agree", "dbid=AGREE")
            $happy.Replace("CMSTOPOLOGYCTX|", " CMSTOPOLOGYCTX|")                       # leading pad
            $happy.Replace("dbid=agree", "dbid=agree ")                                 # trailing pad
            $happy.Replace("CMSTOPOLOGYRESERVED|present", " CMSTOPOLOGYRESERVED|present ")
            ($happy + "   `n")                                                          # whitespace-only line is content
        )
        foreach ($case in $cases) {
            (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $case -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
                Should -Be "unexpected-output" -Because "variant: $($case.Substring(0, [math]::Min(60, $case.Length)))"
        }
    }

    It "removes at most one terminal CR per line: CRLF output is accepted, a CR CR LF token line is refused" {
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" })

        # Control: ordinary CRLF-terminated output must keep parsing ok.
        $crlfOutput = $happy.Replace("`n", "`r`n")
        (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $crlfOutput -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "ok"

        # A token line really ending CR CR LF carries a CR in its CONTENT. TrimEnd erased the
        # whole run and accepted it (review-measured); at-most-one removal keeps the extra CR
        # and refuses the line.
        $doubleCrContext = $happy.Replace("collationAgreement=agree`n", "collationAgreement=agree`r`r`n")
        (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $doubleCrContext -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
            Should -Be "unexpected-output"
        $doubleCrCandidate = $happy.Replace("dbid=agree`n", "dbid=agree`r`r`n")
        (ConvertFrom-MssqlPhysicalDistinctnessQueryOutput -OutputText $doubleCrCandidate -ExpectedSourceKey @("MSSQL_DB_NAME")).Category |
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
                $node.Name -eq "ConvertFrom-MssqlPhysicalDistinctnessQueryOutput"
            }, $true)
        $parserAst | Should -Not -BeNullOrEmpty
        $bareStartsWith = @([regex]::Matches($parserAst.Extent.Text, '\.StartsWith\([^\)]*\)') |
                Where-Object { $_.Value -notmatch 'Ordinal' })
        $bareStartsWith.Count | Should -Be 0
    }
}

Describe "New-MssqlDistinctnessSqlcmdArgument" {

    It "pins the exact argument vector, element by element, -d master included (mutant M-R11, static leg)" {
        $argumentVector = New-MssqlDistinctnessSqlcmdArgument -ContainerName "dms-mssql" -SaPassword "sentinel-pw"
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

Describe "Assert-MssqlPhysicalDatastoreDistinctness boundary" {

    BeforeAll {
        # Ambient hermeticity. Production deliberately gives an ambient MSSQL_DB_NAME Compose
        # precedence over the file, and deliberately IGNORES an ambient marker (raw file read) -
        # so this Describe must control both variables per test AND hand back the caller's exact
        # pre-existing state afterwards: present with its value, absent, or (where the platform
        # can represent it) present-empty. SetEnvironmentVariable is used for the restore
        # because it preserves a present-empty value on Windows; on Unix a blank variable cannot
        # exist, so absent is the faithful representation there.
        $script:ambientNames = @("DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE", "MSSQL_DB_NAME")
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

    It "is a no-op in shared mode and never touches the transport - even with an ambient marker set" {
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult }
        $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"
        $envFile = New-TopologyEnvFile -FileName "shared.env" -Marker "false" -DatastoreName $script:reservedName
        { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 0 -Exactly
    }

    It "verifies the initialized candidate through the runner exactly once, sending the generated batch over the pinned argv" {
        $expectedQuery = New-MssqlPhysicalDistinctnessQuery -Candidate ([ordered]@{ "MSSQL_DB_NAME" = $script:reviewerName })
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedFilePath = $FilePath
            $script:capturedArgumentList = @($ArgumentList)
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $envFile = New-TopologyEnvFile -FileName "sep-ok.env" -Marker "true" -DatastoreName $script:reviewerName
        { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 1 -Exactly
        $script:capturedFilePath | Should -Be "docker"
        $script:capturedInputText | Should -Be $expectedQuery
        ($script:capturedArgumentList -join "`u{1}") |
            Should -Be ((New-MssqlDistinctnessSqlcmdArgument -ContainerName "dms-mssql" -SaPassword "pw") -join "`u{1}")
    }

    It "includes the provider-parsed registered candidate when supplied, and a collision names the parameter - never the value" {
        $registeredValue = [char]0x00E9 + "dfi_registered"
        $collision = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct"; "-DataStoreDatabaseName" = "collides" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $collision
        }

        $envFile = New-TopologyEnvFile -FileName "sep-reg.env" -Marker "true" -DatastoreName "edfi_datastore"
        $thrown = { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" -RegisteredDatastoreDatabaseName $registeredValue }
        $thrown | Should -Throw "*'-DataStoreDatabaseName'*"
        $thrown | Should -Throw "*edfi_configurationservice*"
        try { & $thrown } catch { $failureMessage = $_.Exception.Message }
        $failureMessage | Should -Not -Match ([regex]::Escape($registeredValue))
        $script:capturedInputText | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $registeredValue)))
    }

    It "throws the collision diagnostic for the initialized candidate, withholding the resolved value" {
        $collision = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "collides" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult -StandardOutput $collision }

        $envFile = New-TopologyEnvFile -FileName "sep-collide.env" -Marker "true" -DatastoreName $script:reviewerName
        $thrown = { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
        $thrown | Should -Throw "*CMS database topology mismatch*"
        $thrown | Should -Throw "*'MSSQL_DB_NAME'*"
        try { & $thrown } catch { $failureMessage = $_.Exception.Message }
        $failureMessage | Should -Not -Match ([regex]::Escape($script:reviewerName))
        $failureMessage | Should -Match "withheld"
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
            $thrown = { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" }
            $thrown | Should -Throw "*could not be confirmed*" -Because $shape.Label
            try { & $thrown } catch { $failureMessage = $_.Exception.Message }
            $failureMessage | Should -Not -Match "SENTINEL-STDERR" -Because "child output never reaches diagnostics"
            $failureMessage | Should -Match "withheld"
        }
    }

    It "fails closed for every non-ok parse category - exit code zero alone is never success" {
        $parseShapes = @(
            @{ Label = "garbage stdout with exit zero"; Output = "Msg 208, Level 16, State 1"; Category = "unexpected-output" }
            @{ Label = "context assertion"; Output = (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -ContextLine "CMSTOPOLOGYCTX|db=other|collationAgreement=agree"); Category = "context-assertion" }
            @{ Label = "oracle disagreement"; Output = (New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" }) -Corroboration "disagree"); Category = "oracle-disagreement" }
        )
        foreach ($shape in $parseShapes) {
            $shapeTransport = New-TransportResult -StandardOutput $shape.Output
            Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { $shapeTransport }.GetNewClosure()
            $envFile = New-TopologyEnvFile -FileName "sep-parse.env" -Marker "true" -DatastoreName "edfi_datastore"
            { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
                Should -Throw "*($($shape.Category))*" -Because $shape.Label
        }
    }

    It "resolves the initialized candidate with ambient Compose precedence - the checked name is what the stack will receive" {
        $ambientName = [char]0x00E9 + "dfi_ambient_datastore"
        $happy = New-BatchOutput -VerdictBySourceKey ([ordered]@{ "MSSQL_DB_NAME" = "distinct" })
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith {
            $script:capturedInputText = $InputText
            New-TransportResult -StandardOutput $happy
        }

        $env:MSSQL_DB_NAME = $ambientName
        $envFile = New-TopologyEnvFile -FileName "sep-ambient-name.env" -Marker "true" -DatastoreName "edfi_file_value"
        { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Not -Throw
        $script:capturedInputText | Should -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value $ambientName)))
        $script:capturedInputText | Should -Not -Match ([regex]::Escape((ConvertTo-MssqlUtf16HexLiteral -Value "edfi_file_value")))
    }

    It "reports a blank initialized datastore name as a configuration failure naming the key" {
        Mock Invoke-NativeCommandWithInput -ModuleName env-utility -MockWith { New-TransportResult }
        $envFile = New-TopologyEnvFile -FileName "sep-blank.env" -Marker "true"
        { Assert-MssqlPhysicalDatastoreDistinctness -EnvironmentFile $envFile -ContainerName "dms-mssql" -SaPassword "pw" } |
            Should -Throw "*MSSQL_DB_NAME*"
        Should -Invoke Invoke-NativeCommandWithInput -ModuleName env-utility -Times 0 -Exactly
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
