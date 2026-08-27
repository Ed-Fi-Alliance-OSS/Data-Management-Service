# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Coverage for the fail-closed guards of the Northridge scripts and recipe: the places where a run
# could print PASS having compared something to itself, skipped a table it never knew about, or waited
# forever on a port it assumed. Every Describe here runs without a database and touches no docker: the
# scripts' helpers are lifted out by AST, the scripts themselves are exercised through -WhatIf or with
# the HTTP cmdlets mocked, and the recipe is read from the README the consumer reads.
#
# Each It names the false pass it kills:
#   Compare-DmsSchemaSnapshot.ps1   two database names differing only in case collapsing to one
#                                   snapshot file, so the diff read one file twice
#   Get-DmsResourceCount.ps1        one CSV passed as both sides of the reconciliation; a count row
#                                   that does not parse skipped instead of refused; two projects'
#                                   resources of one name collapsed to one key; no output at all
#                                   failing as a binding error instead of as an empty count set
#   Copy-NorthridgeDataForward.ps1  a dms base table on none of the classification lists; a target
#                                   used as its own source or reference; a measured checkpoint value
#                                   with no expected value; the descriptor load rewriting data rows
#                                   through a host string that also carried pg_restore's diagnostics;
#                                   a row count that did not parse dropped from both sides at once;
#                                   a bulk schema missing from both lists, so the lists agreed on
#                                   the hole
#   Add-NorthridgeGapDocument.ps1   a deferred read recorded with its mid-manifest status; two
#                                   documents sharing a label; a date-time field thrown on instead
#                                   of compared; a token response without access_token reported as
#                                   a property error; a client secret with no path but the argument
#                                   list
#   README restore recipe           hard-coded 8080/8081 and unbounded health waits; "start again
#                                   from step 4" after a failed restore, which set the partial
#                                   restore aside as the reference over the intact deployment; a
#                                   secret or token on a curl argument list; the DMS-to-CMS client
#                                   deleted in one database and recreated in another; the signing
#                                   key replaced in the restored database while CMS reads its own;
#                                   a reference name PostgreSQL would silently truncate

BeforeAll {
    $script:northridgeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $script:compareScript = Join-Path $script:northridgeRoot "Compare-DmsSchemaSnapshot.ps1"
    $script:countScript = Join-Path $script:northridgeRoot "Get-DmsResourceCount.ps1"
    $script:copyScript = Join-Path $script:northridgeRoot "Copy-NorthridgeDataForward.ps1"
    $script:gapScript = Join-Path $script:northridgeRoot "Add-NorthridgeGapDocument.ps1"
    $script:readmePath = Join-Path $script:northridgeRoot "README.md"

    function script:Get-ScriptAst {
        param([Parameter(Mandatory)] [string] $ScriptPath)
        $parseError = $null
        $token = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$token, [ref]$parseError)
        if ($parseError.Count -gt 0) {
            throw "'$ScriptPath' does not parse: $(($parseError | ForEach-Object { $_.Message }) -join '; ')"
        }
        return $ast
    }

    # The scripts have mandatory parameters and run their work at file scope, so helpers are lifted out
    # by AST rather than dot-sourced -- the same extraction style SchemaSnapshotSecurity.Tests.ps1 uses.
    function script:Get-ScriptFunctionAst {
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $FunctionName
        )
        $functionAst = (Get-ScriptAst -ScriptPath $ScriptPath).FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
            }, $true) | Select-Object -First 1
        if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
        return $functionAst
    }

    function script:Get-ScriptFunctionText {
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $FunctionName
        )
        return (Get-ScriptFunctionAst -ScriptPath $ScriptPath -FunctionName $FunctionName).Extent.Text
    }

    # A script-scope list, as the assignment statement that defines it, so a lifted helper reads the
    # same names the script does.
    function script:Get-ScriptAssignmentText {
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $VariablePath
        )
        $assignmentAst = (Get-ScriptAst -ScriptPath $ScriptPath).FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq $VariablePath
            }, $false) | Select-Object -First 1
        if ($null -eq $assignmentAst) { throw "Assignment to '$VariablePath' was not found in '$ScriptPath'." }
        return $assignmentAst.Extent.Text
    }

    # The keys of the hashtable literal a function returns -- Measure-Invariant's measured values.
    function script:Get-FunctionHashtableKey {
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $FunctionName
        )
        $hashtableAst = (Get-ScriptFunctionAst -ScriptPath $ScriptPath -FunctionName $FunctionName).FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.HashtableAst]
            }, $true) | Select-Object -First 1
        if ($null -eq $hashtableAst) { throw "Function '$FunctionName' holds no hashtable literal." }
        return @($hashtableAst.KeyValuePairs | ForEach-Object { [string]$_.Item1.SafeGetValue() })
    }

    # The recipe under test, read from the README the consumer reads, and the same comment-stripped
    # "active" view SchemaSnapshotSecurity.Tests.ps1 asserts on.
    $readme = Get-Content -Raw -LiteralPath $script:readmePath
    $script:recipe = [regex]::Match($readme, '(?ms)^```shell\r?\n(?<recipe>.*?)^```').Groups["recipe"].Value
    $script:activeRecipe = (($script:recipe -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
}

Describe "Compare-DmsSchemaSnapshot.ps1 keeps two databases as two snapshots" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:compareScript -FunctionName "Get-SnapshotPathMap")))
    }

    It "maps names that differ only in case to two entries and two files" {
        # The false pass: a hashtable literal compares keys without regard to case, so 'foo' and 'Foo'
        # shared one entry and the diff read one file against itself.
        $map = Get-SnapshotPathMap -DatabaseName @("foo", "Foo") -Directory $TestDrive
        $map.Count | Should -Be 2
        $map.Comparer | Should -Be ([System.StringComparer]::Ordinal)
        $map["foo"] | Should -Not -BeNullOrEmpty
        $map["Foo"] | Should -Not -BeNullOrEmpty
        $map["foo"] | Should -Not -Be $map["Foo"]
        [string]::Equals($map["foo"], $map["Foo"], [System.StringComparison]::OrdinalIgnoreCase) |
            Should -BeFalse -Because "two paths equal but for case are one file on Windows and macOS, and the diff would read it twice"
    }

    It "keeps the documented file name when the names differ by more than case" {
        $map = Get-SnapshotPathMap -DatabaseName @("northridge_target", "northridge_target_reference") -Directory $TestDrive
        $map["northridge_target"] | Should -Be (Join-Path $TestDrive "schema-snapshot.northridge_target.txt")
        $map["northridge_target_reference"] | Should -Be (Join-Path $TestDrive "schema-snapshot.northridge_target_reference.txt")
        (Get-SnapshotPathMap -DatabaseName @("only") -Directory $TestDrive)["only"] |
            Should -Be (Join-Path $TestDrive "schema-snapshot.only.txt")
    }

    It "refuses the same name twice before contacting anything" {
        { & $script:compareScript -Database "foo", "foo" -OutputDirectory $TestDrive -WhatIf } |
            Should -Throw "*supplied twice*"
    }

    It "accepts names that differ only in case as two databases" {
        $output = @(& $script:compareScript -Database "foo", "Foo" -OutputDirectory $TestDrive -WhatIf)
        ($output -join "`n") | Should -Match "WhatIf: no database was contacted"
    }

    It "builds the snapshot paths through the map and no longer through a hashtable literal" {
        $text = Get-Content -Raw -LiteralPath $script:compareScript
        $text | Should -Not -Match '\$snapshotPath\s*=\s*@\{\}'
        $text | Should -Match '\$snapshotPath = Get-SnapshotPathMap -DatabaseName \$Database -Directory \$OutputDirectory'
    }
}

Describe "Get-DmsResourceCount.ps1 reconcile mode refuses one file as both sides" {
    BeforeAll {
        $script:leftCsv = Join-Path $TestDrive "pg.csv"
        $script:twinCsv = Join-Path $TestDrive "mssql.csv"
        $countSet = @(
            [pscustomobject]@{ ProjectName = "Ed-Fi"; ResourceName = "schools"; DocumentCount = 2 },
            [pscustomobject]@{ ProjectName = "Ed-Fi"; ResourceName = "students"; DocumentCount = 3 }
        )
        $countSet | Export-Csv -LiteralPath $script:leftCsv -NoTypeInformation
        $countSet | Export-Csv -LiteralPath $script:twinCsv -NoTypeInformation
    }

    It "throws when -LeftPath and -RightPath are the same path" {
        # The false pass: the file reconciles against itself with zero differences on every axis and
        # hits both expected totals, so nothing downstream could refuse it.
        { & $script:countScript -LeftPath $script:leftCsv -RightPath $script:leftCsv -ExpectedDocumentCount 5 -ExpectedResourceCount 2 } |
            Should -Throw "*both resolve to*"
    }

    It "throws when the two paths are two spellings of one file" {
        Push-Location $TestDrive
        try {
            { & $script:countScript -LeftPath "./pg.csv" -RightPath $script:leftCsv -ExpectedDocumentCount 5 -ExpectedResourceCount 2 } |
                Should -Throw "*both resolve to*"
        }
        finally {
            Pop-Location
        }
    }

    It "throws when one path is a symbolic link to the other" {
        $link = Join-Path $TestDrive "pg-link.csv"
        try {
            New-Item -ItemType SymbolicLink -Path $link -Target $script:leftCsv -ErrorAction Stop | Out-Null
        }
        catch {
            Set-ItResult -Skipped -Because "this account cannot create symbolic links: $($_.Exception.Message)"
        }
        { & $script:countScript -LeftPath $link -RightPath $script:leftCsv -ExpectedDocumentCount 5 -ExpectedResourceCount 2 } |
            Should -Throw "*both resolve to*"
    }

    It "still reconciles two distinct files with identical contents to PASS" {
        # The guard is about identity, not content: agreement between two real count sets is the result
        # a successful reconciliation produces.
        $output = @(& $script:countScript -LeftPath $script:leftCsv -RightPath $script:twinCsv -ExpectedDocumentCount 5 -ExpectedResourceCount 2)
        ($output -join "`n") | Should -Match "PASS: zero differences in both directions"
    }

    It "names a missing file rather than reconciling nothing" {
        { & $script:countScript -LeftPath (Join-Path $TestDrive "absent.csv") -RightPath $script:twinCsv -ExpectedDocumentCount 5 -ExpectedResourceCount 2 } |
            Should -Throw "*does not exist or is not a file*"
    }
}

Describe "Get-DmsResourceCount.ps1 refuses count output it cannot parse" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:countScript -FunctionName "ConvertTo-CountRow")))
        $script:goodRow = @("Ed-Fi|students|3", "TPDM|students|4")
    }

    It "parses ProjectName|ResourceName|Count rows and ignores the tools' trailing blank line" {
        $rows = ConvertTo-CountRow -Line ($script:goodRow + "")
        $rows.Count | Should -Be 2
        $rows[0].ProjectName | Should -Be "Ed-Fi"
        $rows[0].ResourceName | Should -Be "students"
        $rows[0].DocumentCount | Should -Be 3
        $rows[1].ProjectName | Should -Be "TPDM"
        $rows[1].DocumentCount | Should -Be 4
    }

    It "throws on '<Line>' (<Reason>) instead of skipping it" -ForEach @(
        @{ Line = "psql: warning: something"; Reason = "a diagnostic on the row stream" },
        @{ Line = "Ed-Fi|students"; Reason = "a truncated row" },
        @{ Line = "Ed-Fi|students|abc"; Reason = "a non-numeric count" },
        @{ Line = "Ed-Fi|students|-1"; Reason = "a negative count" },
        @{ Line = "a|b|c|4"; Reason = "a row with too many separators" },
        @{ Line = "|students|3"; Reason = "an empty project name" }
    ) {
        # The false pass: the row was skipped, the resource dropped out of the count set, and the
        # reconciliation read its absence on both sides as agreement.
        { ConvertTo-CountRow -Line ($script:goodRow + $Line) } | Should -Throw "*refusing to skip it*"
    }

    It "returns no rows for no output at all, so the caller's empty-set error is what reports it" {
        # The false failure: a tool that printed nothing handed the parser $null, which the parameter
        # refused with a binding error; and an empty list returned bare unrolls to $null, whose .Count
        # throws under strict mode -- either way the run failed on something other than the message
        # that says the count set was empty.
        Set-StrictMode -Version Latest
        (ConvertTo-CountRow -Line $null).Count | Should -Be 0
        (ConvertTo-CountRow -Line @()).Count | Should -Be 0
        (ConvertTo-CountRow -Line @("", "   ")).Count | Should -Be 0
        $text = Get-Content -Raw -LiteralPath $script:countScript
        $text | Should -Match '(?ms)\$rows = ConvertTo-CountRow -Line \$raw\s+if \(\$rows\.Count -eq 0\) \{\s+throw "No resource counts were returned' -Because "the empty set is reported by the count-mode check, after the parser"
    }

    It "is the only parser the script reads counts through, and both engines emit the same three fields" {
        $text = Get-Content -Raw -LiteralPath $script:countScript
        $text | Should -Not -Match 'LastIndexOf\("\|"\)' -Because "no lenient parsing may remain outside the helper that fails closed"
        $text | Should -Match 'GROUP BY rk\."ProjectName", rk\."ResourceName"' -Because "the PostgreSQL count is per project and resource"
        $text | Should -Match 'GROUP BY rk\.ProjectName, rk\.ResourceName' -Because "the SQL Server count is per project and resource"
        $text | Should -Match 'Sort-Object -Property ProjectName, ResourceName \| Export-Csv'
    }
}

Describe "Get-DmsResourceCount.ps1 keys every count by project and resource" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:countScript -FunctionName "Get-CountSetKey")))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:countScript -FunctionName "ConvertTo-CountMap")))
        $script:twoProject = @(
            [pscustomobject]@{ ProjectName = "Ed-Fi"; ResourceName = "students"; DocumentCount = 3 },
            [pscustomobject]@{ ProjectName = "TPDM"; ResourceName = "students"; DocumentCount = 4 }
        )

        # The script throws after it has written its report, so the report is collected as it streams
        # and the terminating message separately.
        function script:Invoke-Reconcile {
            param([string] $Left, [string] $Right, [long] $Documents, [int] $Resources)
            $line = [System.Collections.Generic.List[string]]::new()
            $thrown = $null
            try {
                & $script:countScript -LeftPath $Left -RightPath $Right -ExpectedDocumentCount $Documents -ExpectedResourceCount $Resources |
                    ForEach-Object { $line.Add([string]$_) }
            }
            catch {
                $thrown = $_.Exception.Message
            }
            return [pscustomobject]@{ Report = ($line -join "`n"); Thrown = $thrown }
        }
    }

    It "keeps two projects' resources of one name as two entries" {
        # The false pass: keyed by ResourceName alone, the second Student row was refused as a repeat
        # or overwrote the first, and a per-project count was never compared.
        $map = ConvertTo-CountMap -Row $script:twoProject -Label "left"
        $map.Count | Should -Be 2
        $map.Comparer | Should -Be ([System.StringComparer]::Ordinal)
        $map[(Get-CountSetKey -ProjectName "Ed-Fi" -ResourceName "students")].DocumentCount | Should -Be 3
        $map[(Get-CountSetKey -ProjectName "TPDM" -ResourceName "students")].DocumentCount | Should -Be 4
        (Get-CountSetKey -ProjectName "Ed-Fi" -ResourceName "students") | Should -Not -Be (Get-CountSetKey -ProjectName "TPDM" -ResourceName "students")
    }

    It "reconciles two identical two-project count sets to PASS" {
        $left = Join-Path $TestDrive "two-project-left.csv"
        $right = Join-Path $TestDrive "two-project-right.csv"
        $script:twoProject | Export-Csv -LiteralPath $left -NoTypeInformation
        $script:twoProject | Export-Csv -LiteralPath $right -NoTypeInformation
        $run = Invoke-Reconcile -Left $left -Right $right -Documents 7 -Resources 2
        $run.Thrown | Should -BeNullOrEmpty
        $run.Report | Should -Match "PASS: zero differences in both directions"
        $run.Report | Should -Match "Resources, left           : 2"
    }

    It "reports a resource one project has and the other lacks, by project" {
        $left = Join-Path $TestDrive "two-project.csv"
        $right = Join-Path $TestDrive "other-project.csv"
        $script:twoProject | Export-Csv -LiteralPath $left -NoTypeInformation
        @($script:twoProject[0], [pscustomobject]@{ ProjectName = "TPDM"; ResourceName = "candidates"; DocumentCount = 4 }) |
            Export-Csv -LiteralPath $right -NoTypeInformation
        $run = Invoke-Reconcile -Left $left -Right $right -Documents 7 -Resources 2
        $run.Thrown | Should -Match "1 resource\(s\) present only on the left; 1 resource\(s\) present only on the right"
        $run.Report | Should -Match "left-only : TPDM/students = 4"
        $run.Report | Should -Match "right-only: TPDM/candidates = 4"
        $run.Report | Should -Not -Match "differs"
    }

    It "refuses a count set keyed by ResourceName alone" {
        # An older CSV has no project column; reconciled by name it would collapse the projects again.
        $old = Join-Path $TestDrive "old.csv"
        $new = Join-Path $TestDrive "new.csv"
        @([pscustomobject]@{ ResourceName = "students"; DocumentCount = 7 }) | Export-Csv -LiteralPath $old -NoTypeInformation
        $script:twoProject | Export-Csv -LiteralPath $new -NoTypeInformation
        { & $script:countScript -LeftPath $old -RightPath $new -ExpectedDocumentCount 7 -ExpectedResourceCount 2 } |
            Should -Throw "*has no ProjectName column*"
    }
}

Describe "Add-NorthridgeGapDocument.ps1 compares a date-time field instead of throwing on it" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:gapScript -FunctionName "Test-NumericValue")))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:gapScript -FunctionName "Compare-SentField")))
        # As the script sees them: the manifest body and the GET response both come through
        # ConvertFrom-Json, which materialises an ISO 8601 date-time as [datetime] and leaves a
        # date-only value a string.
        $script:sent = '{"beginDate":"2024-08-01T00:00:00Z","birthDate":"2005-01-15","orderOfAssignment":1.0,"name":"Krystal"}' | ConvertFrom-Json
    }

    It "passes a document whose date-time field round-trips" {
        # The false failure: [double] has no conversion from [datetime], so the comparison threw and no
        # document carrying a date-time could be verified at all.
        $script:sent.beginDate | Should -BeOfType [datetime] -Because "the case exists only because ConvertFrom-Json materialises the value"
        $fetched = '{"id":"x","beginDate":"2024-08-01T00:00:00Z","birthDate":"2005-01-15","orderOfAssignment":1,"name":"Krystal","_etag":"e"}' | ConvertFrom-Json
        $mismatch = Compare-SentField -Sent $script:sent -Fetched $fetched
        $mismatch.Count | Should -Be 0
    }

    It "reports a date-time that came back different, comparing instants" {
        $fetched = '{"beginDate":"2024-08-02T00:00:00Z","birthDate":"2005-01-15","orderOfAssignment":1,"name":"Krystal"}' | ConvertFrom-Json
        $mismatch = Compare-SentField -Sent $script:sent -Fetched $fetched
        $mismatch.Count | Should -Be 1
        $mismatch[0] | Should -Be 'beginDate sent=2024-08-01T00:00:00.0000000Z fetched=2024-08-02T00:00:00.0000000Z'
        # The same instant written with an offset is not a difference.
        $offset = '{"beginDate":"2024-08-01T02:00:00+02:00","birthDate":"2005-01-15","orderOfAssignment":1,"name":"Krystal"}' | ConvertFrom-Json
        (Compare-SentField -Sent $script:sent -Fetched $offset).Count | Should -Be 0
    }

    It "keeps a date-only value a string comparison" {
        $script:sent.birthDate | Should -BeOfType [string]
        $fetched = '{"beginDate":"2024-08-01T00:00:00Z","birthDate":"2005-01-16","orderOfAssignment":1,"name":"Krystal"}' | ConvertFrom-Json
        $mismatch = Compare-SentField -Sent $script:sent -Fetched $fetched
        $mismatch.Count | Should -Be 1
        $mismatch[0] | Should -Be 'birthDate sent="2005-01-15" fetched="2005-01-16"'
    }

    It "still compares numbers by value, and a date-time on one side only is a mismatch" {
        $fetched = '{"beginDate":"2024-08-01","birthDate":"2005-01-15","orderOfAssignment":1,"name":"Krystal"}' | ConvertFrom-Json
        $mismatch = Compare-SentField -Sent $script:sent -Fetched $fetched
        $mismatch.Count | Should -Be 1
        $mismatch[0] | Should -Match '^beginDate sent='
        Test-NumericValue -Value ([datetime]::UtcNow) | Should -BeFalse
        Test-NumericValue -Value ([System.Numerics.BigInteger]::Parse("10000000000000000000")) | Should -BeTrue
        Test-NumericValue -Value 1.5 | Should -BeTrue
        Test-NumericValue -Value $true | Should -BeFalse
        Test-NumericValue -Value $null | Should -BeFalse
    }
}

Describe "Copy-NorthridgeDataForward.ps1 classifies every dms base table exactly once" {
    BeforeAll {
        foreach ($name in @("ProvisioningOwnedTable", "DmsDataTable", "DmsStagedTable", "DmsDerivedTable")) {
            . ([scriptblock]::Create((Get-ScriptAssignmentText -ScriptPath $script:copyScript -VariablePath "script:$name")))
        }
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Get-DmsTableClassificationFailure")))
        $script:knownDmsTable = @(@($script:ProvisioningOwnedTable) + @($script:DmsDataTable) + @($script:DmsStagedTable) + @($script:DmsDerivedTable) | ForEach-Object { "dms.$_" })
        $script:copyText = Get-Content -Raw -LiteralPath $script:copyScript
    }

    It "names exactly the dms base tables the published artifact carries" {
        # The recipe's step 6 asserts the number of dms base tables in the restored artifact; the lists
        # that stand in for discovery have to add up to the same number, or one side is wrong.
        $expected = [regex]::Match($script:recipe, "SELECT 'dms base tables', '(?<n>\d+)'").Groups["n"].Value
        $expected | Should -Not -BeNullOrEmpty -Because "step 6 must assert the dms base table count"
        $script:knownDmsTable.Count | Should -Be ([int]$expected)
        @($script:knownDmsTable | Sort-Object -Unique).Count | Should -Be $script:knownDmsTable.Count -Because "no table may be on two lists"
    }

    It "reports nothing when source and target hold exactly the classified tables" {
        $failure = Get-DmsTableClassificationFailure -SourceTable $script:knownDmsTable -TargetTable $script:knownDmsTable
        $failure.Count | Should -Be 0
    }

    It "fails an unknown eleventh dms table on either side, naming the table and the side" {
        # The false pass: a dms table the lists do not know is neither restored nor reconciled, and the
        # copy reported PASS around it.
        foreach ($case in @(
                @{ Source = $script:knownDmsTable + "dms.Ledger"; Target = $script:knownDmsTable; Side = "source" },
                @{ Source = $script:knownDmsTable; Target = $script:knownDmsTable + "dms.Ledger"; Side = "target" },
                @{ Source = $script:knownDmsTable + "dms.Ledger"; Target = $script:knownDmsTable + "dms.Ledger"; Side = "source and target" }
            )) {
            $failure = Get-DmsTableClassificationFailure -SourceTable $case.Source -TargetTable $case.Target
            $failure.Count | Should -Be 1 -Because "one unknown table is one failure ($($case.Side))"
            $failure[0] | Should -Match "^dms\.Ledger is a base table in the $($case.Side) and is on none of the lists"
        }
    }

    It "fails a copied or derived table missing from either side" {
        $withoutDocument = @($script:knownDmsTable | Where-Object { $_ -ne "dms.Document" })
        $withoutDescriptor = @($script:knownDmsTable | Where-Object { $_ -ne "dms.Descriptor" })
        (Get-DmsTableClassificationFailure -SourceTable $withoutDocument -TargetTable $script:knownDmsTable) -join "`n" |
            Should -Match "dms\.Document is not a base table in the source"
        (Get-DmsTableClassificationFailure -SourceTable $script:knownDmsTable -TargetTable $withoutDescriptor) -join "`n" |
            Should -Match "dms\.Descriptor is not a base table in the target"
        (Get-DmsTableClassificationFailure -SourceTable @() -TargetTable $script:knownDmsTable) -join "`n" |
            Should -Match "dms\.Document is not a base table in the source"
    }

    It "tolerates a provisioning-owned table the older source schema lacks" {
        # The published copy ran against a source without DataStoreIdentity, DocumentCacheState and
        # DocumentProjectionWork: provisioning creates them and nothing is copied from them.
        $olderSource = @($script:knownDmsTable | Where-Object {
                $_ -notin @("dms.DataStoreIdentity", "dms.DocumentCacheState", "dms.DocumentProjectionWork")
            })
        $olderSource.Count | Should -Be ($script:knownDmsTable.Count - 3)
        (Get-DmsTableClassificationFailure -SourceTable $olderSource -TargetTable $script:knownDmsTable).Count | Should -Be 0
    }

    It "treats a name that differs only in case as a different, unclassified table" {
        (Get-DmsTableClassificationFailure -SourceTable ($script:knownDmsTable + "dms.document") -TargetTable $script:knownDmsTable) -join "`n" |
            Should -Match "dms\.document is a base table in the source"
    }

    It "reports a table listed under two classifications" {
        $saved = $script:DmsDataTable
        try {
            $script:DmsDataTable = @($saved) + $script:DmsDerivedTable
            (Get-DmsTableClassificationFailure -SourceTable $script:knownDmsTable -TargetTable $script:knownDmsTable) -join "`n" |
                Should -Match "dms\.$($script:DmsDerivedTable) is classified twice"
        }
        finally {
            $script:DmsDataTable = $saved
        }
    }

    It "keys every map of catalog names ordinally" {
        # A hashtable literal compares keys without regard to case and reads two tables differing only
        # in case as one; a [hashtable] parameter copies the Ordinal dictionary a caller passes into a
        # Hashtable with a comparer of PowerShell's choosing. Every map keyed by a table name is an
        # Ordinal dictionary, and the row-count map crosses function boundaries as an IDictionary.
        $script:copyText | Should -Not -Match '(?m)^\s*\$\w+\s*=\s*@\{\}\s*$' -Because "a hashtable literal compares keys without regard to case"
        $script:copyText | Should -Not -Match '\[hashtable\]\s*\$' -Because "a [hashtable] parameter copies the caller's dictionary under another comparer"
        $script:copyText | Should -Match '\[System\.Collections\.IDictionary\] \$RowCount'
    }

    It "runs the check in copy mode against both databases before the dump is copied in" {
        $script:copyText | Should -Match 'Get-DataTableList -DatabaseName \$SourceDatabase -Schema @\("dms"\)'
        $script:copyText | Should -Match 'Get-DataTableList -DatabaseName \$TargetDatabase -Schema @\("dms"\)'
        $checkAt = $script:copyText.IndexOf('Get-DmsTableClassificationFailure -SourceTable $sourceDmsTable -TargetTable $targetDmsTable')
        $copyAt = $script:copyText.IndexOf('docker cp $DumpPath')
        $checkAt | Should -BeGreaterThan -1
        $copyAt | Should -BeGreaterThan $checkAt -Because "a misclassified table must stop the run before anything is loaded"
    }
}

Describe "Copy-NorthridgeDataForward.ps1 refuses a database as its own source or reference" {
    BeforeAll {
        $script:copyCommon = @{
            OutputDirectory       = $TestDrive
            ExpectedDocumentCount = 1
            WhatIf                = $true
        }
    }

    It "refuses -SourceDatabase equal to -TargetDatabase in copy mode" {
        # The false pass: a target reconciled against itself agrees with itself on every row count and
        # every stamp distribution.
        { & $script:copyScript -Mode Copy -DumpPath (Join-Path $TestDrive "nr.dump") -SourceDatabase "nr" -TargetDatabase "nr" -ReferenceDatabase "nr_reference" @script:copyCommon } |
            Should -Throw "*-TargetDatabase and -SourceDatabase name the same database 'nr'*"
    }

    It "refuses -ReferenceDatabase equal to -TargetDatabase in checkpoint mode" {
        # A target measured against itself as its own reference agrees with itself on the fingerprint.
        { & $script:copyScript -Mode Checkpoint -TargetDatabase "nr" -CheckpointName "C2" -ReferenceDatabase "nr" @script:copyCommon } |
            Should -Throw "*-TargetDatabase and -ReferenceDatabase name the same database 'nr'*"
    }

    It "refuses -ReferenceDatabase equal to -SourceDatabase" {
        { & $script:copyScript -Mode Copy -DumpPath (Join-Path $TestDrive "nr.dump") -SourceDatabase "nr_source" -TargetDatabase "nr" -ReferenceDatabase "nr_source" @script:copyCommon } |
            Should -Throw "*-SourceDatabase and -ReferenceDatabase name the same database 'nr_source'*"
    }

    It "treats names that differ only in case as distinct databases" {
        $output = @(& $script:copyScript -Mode Copy -DumpPath (Join-Path $TestDrive "nr.dump") -SourceDatabase "nr" -TargetDatabase "NR" -ReferenceDatabase "Nr" @script:copyCommon)
        ($output -join "`n") | Should -Match "WhatIf: no database was contacted"
    }
}

Describe "Copy-NorthridgeDataForward.ps1 checkpoint record has an expected value for every measured key" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Get-CheckpointExpectedValue")))
        $script:measuredKey = Get-FunctionHashtableKey -ScriptPath $script:copyScript -FunctionName "Measure-Invariant"
        $script:testInvariantText = Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Test-Invariant"
        $script:expected = [ordered]@{
            Source              = "test"
            EffectiveSchemaHash = "hash"
            ResourceKeyCount    = [long]351
            ResourceKeySeedHash = "seed"
            CacheStateLifecycle = "Disabled"
            CacheAheadRecovery  = "false"
        }
    }

    It "resolves an expected value for every key Measure-Invariant records" {
        $script:measuredKey.Count | Should -BeGreaterThan 5 -Because "the measured keys must have been read: $($script:measuredKey -join ', ')"
        foreach ($key in $script:measuredKey) {
            $value = Get-CheckpointExpectedValue -Key $key -Expected $script:expected -ExpectedDocumentRow 7
            $value | Should -Not -BeNullOrEmpty -Because "'$key' is recorded and must be recorded against something"
        }
    }

    It "throws for a key it does not know rather than recording a blank expectation" {
        # The false pass: the default arm returned "", so a newly measured value was written to the
        # record as 'expected=' and read like a check.
        { Get-CheckpointExpectedValue -Key "NotMeasuredAnywhere" -Expected $script:expected -ExpectedDocumentRow 7 } |
            Should -Throw "*'NotMeasuredAnywhere' has no expected value*"
    }

    It "compares the fingerprint hashes case-sensitively" {
        # -ne compares strings without regard to case; a stored hash that differs from the expected one
        # only in case is not the recorded value, and the Ordinal discipline every other comparison
        # here follows says so.
        $script:testInvariantText | Should -Match '\$Measurement\.EffectiveSchemaHash -cne \$Expected\.EffectiveSchemaHash'
        $script:testInvariantText | Should -Match '\$Measurement\.ResourceKeySeedHash -cne \$Expected\.ResourceKeySeedHash'
        $script:testInvariantText | Should -Not -Match 'Hash -ne '
    }

    It "compares every measured key in Test-Invariant" {
        foreach ($key in $script:measuredKey) {
            $script:testInvariantText | Should -Match ('\$Measurement\.' + [regex]::Escape($key) + '\b') -Because "'$key' is measured and must be asserted, not only recorded"
        }
    }
}

Describe "Add-NorthridgeGapDocument.ps1 records the final read and refuses ambiguous labels" {
    BeforeAll {
        $script:gapCommon = @{
            DmsBaseUrl   = "http://dms.test"
            TokenUrl     = "http://cms.test/connect/token"
            ClientId     = "client"
            ClientSecret = "secret"
        }

        function script:Write-GapManifest {
            param(
                [Parameter(Mandatory)] [string] $Path,
                [Parameter(Mandatory)] [object[]] $Document
            )
            [ordered]@{ documents = $Document } | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding utf8
        }
    }

    It "refuses a manifest whose labels collide, before requesting a token" {
        # The false pass: the final verification found the sent body by label, so two documents sharing
        # one -- or differing only in case -- failed with a field diff that named the wrong cause.
        $manifestPath = Join-Path $TestDrive "collide.json"
        Write-GapManifest -Path $manifestPath -Document @(
            [ordered]@{ order = 1; label = "Staff: Krystal Redd"; endpoint = "/data/ed-fi/staffs"; body = [ordered]@{ staffUniqueId = "A" } },
            [ordered]@{ order = 2; label = "staff: krystal redd"; endpoint = "/data/ed-fi/staffs"; body = [ordered]@{ staffUniqueId = "B" } }
        )
        { & $script:gapScript -ManifestPath $manifestPath @script:gapCommon -WhatIf } |
            Should -Throw "*uses label 'staff: krystal redd' more than once*"
    }

    It "looks the sent body up by label case-sensitively" {
        (Get-Content -Raw -LiteralPath $script:gapScript) | Should -Match '\$_\.label -ceq \$row\.Label'
    }

    Context "a Staff document readable only once its association exists" {
        BeforeAll {
            # Staff is authorized through its education organization association, which the manifest
            # creates after it, so the GET issued right after the Staff POST answers 403 and only the
            # final pass can read it. The HTTP cmdlets are mocked to play exactly that server.
            $script:staffBody = [ordered]@{ staffUniqueId = "NR-GAP-1"; firstName = "Krystal"; lastSurname = "Redd" }
            $script:associationBody = [ordered]@{
                staffReference                 = [ordered]@{ staffUniqueId = "NR-GAP-1" }
                educationOrganizationReference = [ordered]@{ educationOrganizationId = 255901 }
                staffClassificationDescriptor  = "uri://ed-fi.org/StaffClassificationDescriptor#Teacher"
                orderOfAssignment              = 1
            }
            $script:manifestPath = Join-Path $TestDrive "deferred.json"
            Write-GapManifest -Path $script:manifestPath -Document @(
                [ordered]@{ order = 2; label = "StaffEducationOrganizationAssignmentAssociation: Krystal Redd"; endpoint = "/data/ed-fi/staffEducationOrganizationAssignmentAssociations"; body = $script:associationBody },
                [ordered]@{ order = 1; label = "Staff: Krystal Redd"; endpoint = "/data/ed-fi/staffs"; body = $script:staffBody }
            )
            # The mock body runs in the scope chain of the script under test, whose nearest script
            # scope is its own, so this file's $script: variables are not what $script: names there.
            # The probe state is therefore plain BeforeAll variables, reached unqualified through the
            # scope the script is invoked from -- and visible to the It blocks the same way.
            $sentBodyByEndpoint = @{
                "/data/ed-fi/staffs"                                           = $script:staffBody
                "/data/ed-fi/staffEducationOrganizationAssignmentAssociations" = $script:associationBody
            }
            $getCount = @{}

            Mock Invoke-RestMethod { [pscustomobject]@{ access_token = "token" } }
            Mock Invoke-WebRequest {
                $path = ([uri]$Uri).AbsolutePath
                if ("$Method" -eq "Post") {
                    return [pscustomobject]@{ StatusCode = 201; Headers = @{ Location = "$path/id-$($path.Split('/')[-1])" }; Content = "" }
                }
                $getCount[$path] = 1 + $(if ($getCount.ContainsKey($path)) { $getCount[$path] } else { 0 })
                if ($path -like "/data/ed-fi/staffs/*" -and $getCount[$path] -eq 1) {
                    return [pscustomobject]@{ StatusCode = 403; Headers = @{}; Content = '{"detail":"Access to the resource could not be authorized."}' }
                }
                $endpoint = $path.Substring(0, $path.LastIndexOf("/"))
                $fetched = [ordered]@{ id = $path.Split("/")[-1] }
                foreach ($property in $sentBodyByEndpoint[$endpoint].GetEnumerator()) {
                    $fetched[$property.Key] = $property.Value
                }
                $fetched["_etag"] = "etag"
                return [pscustomobject]@{ StatusCode = 200; Headers = @{}; Content = ($fetched | ConvertTo-Json -Depth 16 -Compress) }
            }

            $script:resultPath = Join-Path $TestDrive "gap-result.csv"
            $script:runOutput = @(& $script:gapScript -ManifestPath $script:manifestPath @script:gapCommon -OutputPath $script:resultPath)
            $script:resultRow = @(Import-Csv -LiteralPath $script:resultPath)
        }

        It "defers the mid-manifest 403 and passes on the final read" {
            $text = $script:runOutput -join "`n"
            $text | Should -Match "GET http://dms.test/data/ed-fi/staffs/id-staffs -> 403"
            $text | Should -Match "deferred: re-checked once the whole manifest exists"
            $text | Should -Match "PASS: every document was created and verified by GET-by-id"
            $getCount["/data/ed-fi/staffs/id-staffs"] | Should -Be 2 -Because "the final pass re-reads the deferred document"
        }

        It "records the final GET status for the deferred document, not the 403 it answered mid-manifest" {
            # The false pass: the result record kept the mid-manifest status, so the evidence file said
            # 403 about a document the run had verified.
            $staff = @($script:resultRow | Where-Object { $_.Label -ceq "Staff: Krystal Redd" })
            $staff.Count | Should -Be 1
            $staff[0].PostStatus | Should -Be "201"
            $staff[0].GetStatus | Should -Be "200"
            $staff[0].FieldMatch | Should -Be "True"
        }

        It "records every document with a verified final read" {
            $script:resultRow.Count | Should -Be 2
            foreach ($row in $script:resultRow) {
                $row.GetStatus | Should -Be "200" -Because "$($row.Label) was re-read after the whole manifest existed"
                $row.FieldMatch | Should -Be "True"
            }
        }
    }
}

Describe "Restore recipe resolves the service ports and bounds every wait" {
    BeforeAll {
        $script:waitFunction = [regex]::Match($script:recipe, '(?ms)^WAIT200\(\) \{.*?^\}').Value
        $activeWait = (($script:waitFunction -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $script:activeOutsideWait = if ($activeWait) { $script:activeRecipe.Replace($activeWait, "") } else { $script:activeRecipe }
    }

    It "never hard-codes the CMS or DMS port" {
        # The false pass: a stack on other ports met http://localhost:8081 and http://localhost:8080
        # here and waited on them forever.
        $script:activeRecipe | Should -Not -Match 'localhost:80(80|81)\b'
        $script:activeRecipe | Should -Not -Match '(?m)^(CMS|DMS)=http://localhost:\d'
    }

    It "reads each port from the container the compose file configured, and refuses anything but a number" {
        $script:activeRecipe | Should -Match '(?m)^CMS_PORT=\$\(ENVOF ed-fi-api-config-service \| sed -n ''s/\^ASPNETCORE_HTTP_PORTS=//p''\)$'
        $script:activeRecipe | Should -Match '(?m)^DMS_PORT=\$\(ENVOF ed-fi-api \| sed -n ''s/\^ASPNETCORE_HTTP_PORTS=//p''\)$'
        $script:activeRecipe | Should -Match '(?m)^case "\$CMS_PORT" in ''''\|\*\[!0-9\]\*\) echo .*; exit 1;; esac$'
        $script:activeRecipe | Should -Match '(?m)^case "\$DMS_PORT" in ''''\|\*\[!0-9\]\*\) echo .*; exit 1;; esac$'
        $script:activeRecipe | Should -Match '(?m)^CMS="http://localhost:\$CMS_PORT"$'
        $script:activeRecipe | Should -Match '(?m)^DMS="http://localhost:\$DMS_PORT"$'
        [regex]::Matches($script:activeRecipe, '(?m)^ENVOF\(\) \{').Count | Should -Be 1 -Because "one definition, before its first use"
        $script:activeRecipe.IndexOf('ENVOF() {') | Should -BeLessThan $script:activeRecipe.IndexOf('CMS_PORT=$(ENVOF')
    }

    It "bounds every health wait and stops with the container's logs when the bound is exceeded" {
        # The false pass: `until ...; do sleep 3; done` against a wrong port hung with nothing on screen.
        $script:waitFunction | Should -Not -BeNullOrEmpty -Because "the recipe must define WAIT200"
        $script:waitFunction | Should -Match '-ge "\$2"'
        $script:waitFunction | Should -Match '(?m)^\s+return 1$'
        $script:waitFunction | Should -Match 'docker logs --tail \d+ "\$3"'
        # The counter bounds the loop; only a per-request cap bounds one probe. A port that accepts the
        # connection and never answers would otherwise hold a single curl open and the counter would
        # never be reached.
        $script:waitFunction | Should -Match 'curl -s --connect-timeout [1-9]\d* --max-time [1-9]\d* ' -Because "each probe must time out on its own, or the loop bound is never reached"
        $call = @([regex]::Matches($script:activeRecipe, '(?m)^WAIT200 "(?<url>[^"]+)" (?<seconds>\d+) (?<container>\S+) \|\| exit 1$'))
        $call.Count | Should -Be 2 -Because "CMS and DMS each wait once"
        @($call | ForEach-Object { $_.Groups["url"].Value }) | Should -Be @('$CMS/health', '$DMS/health')
        @($call | ForEach-Object { $_.Groups["container"].Value }) | Should -Be @("ed-fi-api-config-service", "ed-fi-api")
        foreach ($item in $call) {
            [int]$item.Groups["seconds"].Value | Should -BeGreaterThan 0
        }
    }

    It "has no other loop or sleep anywhere in the recipe" {
        $script:activeOutsideWait | Should -Not -Match '(?m)^\s*(until|while)\b'
        $script:activeOutsideWait | Should -Not -Match '\bsleep\b'
    }

    It "resolves both URLs before starting either service, and smokes the dataset through the resolved DMS URL" {
        $script:activeRecipe.IndexOf('DMS="http://localhost:$DMS_PORT"') | Should -BeLessThan $script:activeRecipe.IndexOf('docker start ed-fi-api-config-service')
        $script:activeRecipe | Should -Match '"\$DMS/data/ed-fi/students\?limit=1&totalCount=true"'
        $script:activeRecipe | Should -Not -Match 'http://localhost:\d+/data/'
    }
}

Describe "Copy-NorthridgeDataForward.ps1 stamp distributions cover every sampled table or fail" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "ConvertTo-StampDistributionMap")))
        $script:stampTable = @("dms.Document", "edfi.Student", "edfi.School")
        # A count, then min|max for ContentVersion, ContentLastModifiedAt and CreatedAt on dms.Document;
        # the sampled tables carry the first two pairs only.
        $script:stampRow = @(
            "dms.Document|3|1|9|a|b|a|b",
            "edfi.School|2|1|9|a|b",
            "edfi.Student|5|1|9|a|b"
        )
    }

    It "maps one row per table, keyed ordinally, and tolerates psql's trailing empty element" {
        $map = ConvertTo-StampDistributionMap -Row ($script:stampRow + "") -ExpectedTable $script:stampTable
        $map.Count | Should -Be 3
        $map.Comparer | Should -Be ([System.StringComparer]::Ordinal)
        $map["edfi.Student"] | Should -Be "5|1|9|a|b"
        $map["dms.Document"] | Should -Be "3|1|9|a|b|a|b"
    }

    It "refuses a row without a separator rather than dropping it" {
        # The false pass: a malformed row was skipped, the table dropped out of both maps at once, and
        # absence compared equal to absence.
        { ConvertTo-StampDistributionMap -Row ($script:stampRow + "edfi.Broken") -ExpectedTable ($script:stampTable + "edfi.Broken") } |
            Should -Throw "*'edfi.Broken' has no table name before its first separator*"
        { ConvertTo-StampDistributionMap -Row ($script:stampRow + "|orphan") -ExpectedTable $script:stampTable } |
            Should -Throw "*has no table name before its first separator*"
    }

    It "refuses a parsed set that does not cover exactly the requested tables" {
        # The false pass: the comparison count was read from whatever parsed, so a table that produced no
        # row was reported as compared by not being reported at all.
        { ConvertTo-StampDistributionMap -Row $script:stampRow[0..1] -ExpectedTable $script:stampTable } |
            Should -Throw "*Missing: edfi.Student. Unexpected: none.*"
        { ConvertTo-StampDistributionMap -Row @() -ExpectedTable $script:stampTable } |
            Should -Throw "*covers 0 table(s) for 3 requested. Missing: dms.Document, edfi.School, edfi.Student.*"
        { ConvertTo-StampDistributionMap -Row ($script:stampRow + "edfi.Extra|1|1|1|a|b") -ExpectedTable $script:stampTable } |
            Should -Throw "*Missing: none. Unexpected: edfi.Extra.*"
        { ConvertTo-StampDistributionMap -Row ($script:stampRow + "edfi.School|9|9|9|x|y") -ExpectedTable $script:stampTable } |
            Should -Throw "*reported 'edfi.School' twice*"
    }

    It "is what Get-StampDistribution returns, over dms.Document and every sampled table" {
        $text = Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Get-StampDistribution"
        $text | Should -Match 'return ConvertTo-StampDistributionMap -Row @\(\$rows\) -ExpectedTable \(@\("dms\.Document"\) \+ \$SampleTable\)'
        $text | Should -Not -Match 'IndexOf\("\|"\)' -Because "no parsing may remain outside the helper that fails closed"
    }
}

Describe "Copy-NorthridgeDataForward.ps1 re-points only the Descriptor COPY header, inside the container" {
    BeforeAll {
        $script:copyText = Get-Content -Raw -LiteralPath $script:copyScript
        . ([scriptblock]::Create((Get-ScriptAssignmentText -ScriptPath $script:copyScript -VariablePath "script:CopyHeaderRedirectScript")))
        # The rewrite is a POSIX shell script the copy tool feeds to `sh -s` inside the container. It is
        # run here with the host's sh when there is one (the pull-request lane runs on ubuntu; Git for
        # Windows supplies one too) and skipped otherwise; the structural case below runs everywhere.
        $script:posixShell = Get-Command sh -ErrorAction SilentlyContinue

        function script:Invoke-DescriptorRedirect {
            param([Parameter(Mandatory)] [string] $Content)
            $in = Join-Path $TestDrive ("descriptor-" + [guid]::NewGuid().ToString("N") + ".sql")
            $out = "$in.staging"
            [System.IO.File]::WriteAllText($in, $Content, [System.Text.UTF8Encoding]::new($false))
            $output = $script:CopyHeaderRedirectScript | & $script:posixShell.Source -s ($in -replace '\\', '/') ($out -replace '\\', '/') "northridge_staging" "Descriptor" 2>&1
            return [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Output   = (@($output | ForEach-Object { [string]$_ }) -join "`n")
                Result   = if (Test-Path -LiteralPath $out) { [System.IO.File]::ReadAllText($out) } else { $null }
            }
        }

        $script:copyHeader = 'COPY dms."Descriptor" ("DocumentId", "Namespace", "CodeValue") FROM stdin;'
        $script:emitted = @(
            '--',
            '-- Data for Name: Descriptor; Type: TABLE DATA; Schema: dms; Owner: -',
            '--',
            '',
            $script:copyHeader,
            "1`turi://ed-fi.org/x`tCOPY dms.""Descriptor"" (in a value",
            "2`tsee dms.""Descriptor"" for details`tplain",
            '\.',
            ''
        ) -join "`n"
    }

    It "restores the descriptor entry to a file inside the container and never into a PowerShell string" {
        # The false pass: `pg_restore -f - ... 2>&1` captured SQL and diagnostics into one array, a
        # global Replace rewrote every dms."Descriptor" in it -- data rows included -- and the result
        # was piped back into psql through the host's string and encoding handling.
        $script:copyText | Should -Not -Match 'pg_restore [^\n]*-f - ' -Because "the emitted SQL must land in the container, not in the host"
        $script:copyText | Should -Not -Match '\.Replace\(''dms\."Descriptor"''' -Because "a global replace rewrites data rows"
        $script:copyText | Should -Not -Match '\$redirected = |\$redirected \|' -Because "no PowerShell string may carry the descriptor SQL or be piped into psql"
        $script:copyText | Should -Match '--exit-on-error -L \$containerListPath -f \$containerDescriptorSqlPath \$containerDumpPath 2>&1'
        $script:copyText | Should -Match '\$script:CopyHeaderRedirectScript \| docker exec -i \$Container sh -s `\r?\n\s+\$containerDescriptorSqlPath \$containerStagingSqlPath \$script:StagingSchema \$script:DmsDerivedTable 2>&1'
        $script:copyText | Should -Match 'psql -U \$PostgresUser -d \$TargetDatabase `\r?\n\s+-v ON_ERROR_STOP=1 --quiet -f \$containerStagingSqlPath 2>&1' -Because "psql reads the rewritten file in the container"
        $script:copyText | Should -Match 'rm -f \$containerDumpPath \$containerListPath `\r?\n\s+\$containerDescriptorSqlPath \$containerStagingSqlPath' -Because "both emitted files are removed with the dump"
        $script:copyText.IndexOf('$redirectOutput = $script:CopyHeaderRedirectScript') | Should -BeGreaterThan $script:copyText.IndexOf('-Description "pg_restore of dms.Descriptor to text"') -Because "the diagnostics scan runs before the rewrite"
        $script:CopyHeaderRedirectScript | Should -Match '(?m)^set -eu$'
        $script:CopyHeaderRedirectScript | Should -Match ([regex]::Escape('header="^COPY dms\\.\"$table\" ("')) -Because "the rewrite is anchored to the start of the COPY header line, for the table it is given"
        $script:CopyHeaderRedirectScript | Should -Match 'if \[ "\$count" != 1 \]' -Because "exactly one header may be rewritten"
    }

    It "rewrites the header and leaves data rows carrying the table name untouched" {
        if (-not $script:posixShell) { Set-ItResult -Skipped -Because "no POSIX sh on PATH; the pull-request lane runs this on ubuntu" }
        $run = Invoke-DescriptorRedirect -Content $script:emitted
        $run.ExitCode | Should -Be 0 -Because $run.Output
        $run.Result | Should -Match '(?m)^COPY "northridge_staging"\."Descriptor" \("DocumentId", "Namespace", "CodeValue"\) FROM stdin;$'
        $run.Result | Should -Not -Match '(?m)^COPY dms\."Descriptor" \('
        $run.Result | Should -Match ([regex]::Escape("1`turi://ed-fi.org/x`tCOPY dms.""Descriptor"" (in a value")) -Because "a data row that carries the table name is data"
        $run.Result | Should -Match ([regex]::Escape("2`tsee dms.""Descriptor"" for details`tplain"))
        ($run.Result -split "`n").Count | Should -Be ($script:emitted -split "`n").Count -Because "only the header line changed"
        $run.Output.Trim() | Should -Be $script:copyHeader -Because "the archive's column list is reported from the one line that is not data, and nothing else is"
    }

    It "refuses SQL with no header or with two, writing nothing to load" {
        if (-not $script:posixShell) { Set-ItResult -Skipped -Because "no POSIX sh on PATH; the pull-request lane runs this on ubuntu" }
        $none = Invoke-DescriptorRedirect -Content "SET client_encoding = 'UTF8';`n"
        $none.ExitCode | Should -Be 2
        $none.Output | Should -Match 'found 0'
        $none.Result | Should -BeNullOrEmpty
        $two = Invoke-DescriptorRedirect -Content ($script:emitted + $script:copyHeader + "`n\.`n")
        $two.ExitCode | Should -Be 2
        $two.Output | Should -Match 'found 2'
        $two.Result | Should -BeNullOrEmpty
    }
}

Describe "Copy-NorthridgeDataForward.ps1 inserts dms.Document by the columns the archive and the target share" {
    BeforeAll {
        foreach ($name in @("Get-CopyHeaderColumn", "ConvertTo-TargetColumnList", "Resolve-StagedInsertColumn")) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName $name)))
        }
        foreach ($name in @("DmsDataTable", "DmsStagedTable")) {
            . ([scriptblock]::Create((Get-ScriptAssignmentText -ScriptPath $script:copyScript -VariablePath "script:$name")))
        }
        $script:copyText = Get-Content -Raw -LiteralPath $script:copyScript
        $script:selectEntryText = Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Select-ArchiveEntry"

        # The header a source dump emits for dms."Document" while the schema carries the identity stamp
        # columns, and a target catalog that has dropped them, as the catalog query reports it.
        $script:sourceHeader = 'COPY dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "CreatedByOwnershipTokenId", "ContentVersion", "IdentityVersion", "ContentLastModifiedAt", "IdentityLastModifiedAt", "CreatedAt") FROM stdin;'
        $script:sourceColumn = @("DocumentId", "DocumentUuid", "ResourceKeyId", "CreatedByOwnershipTokenId", "ContentVersion", "IdentityVersion", "ContentLastModifiedAt", "IdentityLastModifiedAt", "CreatedAt")
        $script:targetRow = @(
            'DocumentId|NO|NO|YES|NEVER',
            'DocumentUuid|NO|NO|NO|NEVER',
            'ResourceKeyId|NO|NO|NO|NEVER',
            'CreatedByOwnershipTokenId|YES|NO|NO|NEVER',
            'ContentVersion|NO|YES|NO|NEVER',
            'ContentLastModifiedAt|NO|YES|NO|NEVER',
            'CreatedAt|NO|YES|NO|NEVER'
        )
    }

    It "reads the archive's column list from the COPY header and refuses any other line" {
        Get-CopyHeaderColumn -HeaderLine $script:sourceHeader -QualifiedTable "dms.Document" | Should -Be $script:sourceColumn
        Get-CopyHeaderColumn -HeaderLine 'COPY dms."Document" ("Quo""ted", plain) FROM stdin;' -QualifiedTable "dms.Document" | Should -Be @('Quo"ted', 'plain')
        { Get-CopyHeaderColumn -HeaderLine 'COPY dms."Descriptor" ("DocumentId") FROM stdin;' -QualifiedTable "dms.Document" } | Should -Throw "*not the COPY header for dms.Document*"
        { Get-CopyHeaderColumn -HeaderLine "1`tsee COPY dms.""Document"" (`tplain" -QualifiedTable "dms.Document" } | Should -Throw "*not the COPY header*"
        { Get-CopyHeaderColumn -HeaderLine 'COPY dms."Document" ("A", "A") FROM stdin;' -QualifiedTable "dms.Document" } | Should -Throw "*twice*"
        { Get-CopyHeaderColumn -HeaderLine 'COPY dms."Document" ("A", B C) FROM stdin;' -QualifiedTable "dms.Document" } | Should -Throw "*not an identifier*"
    }

    It "does not send IdentityVersion or IdentityLastModifiedAt to a target that no longer has them" {
        # The false pass: pg_restore --data-only replayed the archive's own COPY column list into the
        # target, which succeeded only because the published source predates those columns being
        # dropped; the next refresh from a newer dump fails the COPY under --exit-on-error.
        $plan = Resolve-StagedInsertColumn -SourceColumn (Get-CopyHeaderColumn -HeaderLine $script:sourceHeader -QualifiedTable "dms.Document") `
            -TargetColumn (ConvertTo-TargetColumnList -Row $script:targetRow -QualifiedTable "dms.Document") -QualifiedTable "dms.Document"
        $plan.Insert | Should -Be @("DocumentId", "DocumentUuid", "ResourceKeyId", "CreatedByOwnershipTokenId", "ContentVersion", "ContentLastModifiedAt", "CreatedAt")
        $plan.Insert | Should -Not -Contain "IdentityVersion"
        $plan.Insert | Should -Not -Contain "IdentityLastModifiedAt"
        $plan.SourceOnly | Should -Be @("IdentityVersion", "IdentityLastModifiedAt") -Because "they are staged as text and go no further"
        $plan.OverridingSystemValue | Should -BeTrue -Because "DocumentId is GENERATED ALWAYS AS IDENTITY and the archive's values must survive the insert"
    }

    It "refuses a target NOT NULL column with no default, identity or generation that the archive does not carry" {
        $target = ConvertTo-TargetColumnList -Row ($script:targetRow + 'TenantId|NO|NO|NO|NEVER') -QualifiedTable "dms.Document"
        { Resolve-StagedInsertColumn -SourceColumn $script:sourceColumn -TargetColumn $target -QualifiedTable "dms.Document" } |
            Should -Throw "*dms.Document in the target has 1 NOT NULL column(s)*TenantId*"
    }

    It "lets the target fill a column the archive lacks when it is nullable, defaulted, identity or generated, and never inserts a generated one" {
        $target = ConvertTo-TargetColumnList -Row ($script:targetRow + @('Note|YES|NO|NO|NEVER', 'Stamp|NO|YES|NO|NEVER', 'Seq|NO|NO|YES|NEVER', 'Derived|NO|NO|NO|ALWAYS')) -QualifiedTable "dms.Document"
        $plan = Resolve-StagedInsertColumn -SourceColumn @("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "Derived") -TargetColumn $target -QualifiedTable "dms.Document"
        $plan.Insert | Should -Be @("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion")
        $plan.Insert | Should -Not -Contain "Derived" -Because "PostgreSQL computes a generated column and rejects a supplied value"
        $plan.SourceOnly.Count | Should -Be 0
    }

    It "parses the target catalog fail-closed and refuses an archive that shares no column with the target" {
        { ConvertTo-TargetColumnList -Row @('DocumentId|NO|NO') -QualifiedTable "dms.Document" } | Should -Throw "*is not '<name>|<is_nullable>|<has_default>|<is_identity>|<is_generated>'*"
        { ConvertTo-TargetColumnList -Row @('DocumentId|NO|NO|YES|NEVER', 'DocumentId|NO|NO|YES|NEVER') -QualifiedTable "dms.Document" } | Should -Throw "*twice*"
        { ConvertTo-TargetColumnList -Row @('', ' ') -QualifiedTable "dms.Document" } | Should -Throw "*no columns*"
        { Resolve-StagedInsertColumn -SourceColumn @("Other") -TargetColumn (ConvertTo-TargetColumnList -Row @('Note|YES|NO|NO|NEVER') -QualifiedTable "dms.Document") -QualifiedTable "dms.Document" } |
            Should -Throw "*shares no column*"
    }

    It "keeps dms.Document out of the bulk pg_restore and inserts it from staging by the resolved columns, before the Descriptor derivation" {
        $script:DmsDataTable | Should -Not -Contain "Document" -Because "the bulk restore replays the archive's column list"
        $script:DmsStagedTable | Should -Be "Document"
        $script:selectEntryText | Should -Match '(?s)if \(-not \$AllowStagedTable\) \{\s+\$forbiddenName \+= "dms\.\$script:DmsStagedTable"' -Because "no restore list may carry dms.Document unless it is the staging load"
        $script:copyText | Should -Match '-QualifiedTable @\(\$stagedQualified\) -AllowStagedTable'
        $script:copyText | Should -Match '--exit-on-error -L \$containerListPath -f \$containerStagedSqlPath \$containerDumpPath 2>&1'
        $script:copyText | Should -Match '\$script:CopyHeaderRedirectScript \| docker exec -i \$Container sh -s `\r?\n\s+\$containerStagedSqlPath \$containerStagedLoadPath \$script:StagingSchema \$script:DmsStagedTable 2>&1'
        $script:copyText | Should -Match 'Get-CopyHeaderColumn -HeaderLine \$stagedHeader\[0\] -QualifiedTable \$stagedQualified'
        $script:copyText | Should -Match 'Get-TargetColumnList -DatabaseName \$TargetDatabase -QualifiedTable \$stagedQualified' -Because "the insert columns come from the target catalog, not from the archive"
        $script:copyText | Should -Match ([regex]::Escape('ADD COLUMN ""$($name.Replace(''"'', ''""''))"" text;')) -Because "a source-only column is staged as text and goes no further"
        $script:copyText | Should -Match '(?m)^INSERT INTO \$stagedQuoted \(\$stagedColumnList\)\r?\n\$\{stagedOverriding\}SELECT \$stagedColumnList\r?\nFROM \$stagingQuoted;'
        $script:copyText | Should -Match 'if \(\$stagedPlan\.OverridingSystemValue\) \{ "OVERRIDING SYSTEM VALUE " \}'
        $stagedAt = $script:copyText.IndexOf('Loading dms.$script:DmsStagedTable through staging schema')
        $stagedAt | Should -BeGreaterThan $script:copyText.IndexOf('-Description "Bulk pg_restore"')
        $stagedAt | Should -BeLessThan $script:copyText.IndexOf('Deriving dms.Descriptor.ResourceKeyId') -Because "the Descriptor derivation joins dms.Document"
        $script:copyText | Should -Match '"dms\.\$script:DmsStagedTable", "dms\.\$script:DmsDerivedTable"\) \+ \$bulkTable' -Because "the row-count reconciliation covers the staged table"
    }
}

Describe "Restore recipe stops on a checkout that is not the artifact's DMS revision and on a failed bootstrap" {
    BeforeAll {
        $script:readme = Get-Content -Raw -LiteralPath $script:readmePath
        $script:step3 = [regex]::Match($script:recipe, '(?ms)^# 3\. REQUIRED.*?(?=^# 4\. )').Value
        $script:activeStep3 = (($script:step3 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $script:step4 = [regex]::Match($script:recipe, '(?ms)^# 4\. Bootstrap.*?(?=^# 5\. )').Value
        $script:activeStep4 = (($script:step4 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $script:recordedRevision = [regex]::Match($script:readme, '(?m)^\| DMS revision built \| `(?<sha>[0-9a-f]{40})`').Groups["sha"].Value
    }

    It "states the revision prerequisite and the copy-forward alternative before the first recipe command" {
        $script:recordedRevision | Should -Match '^[0-9a-f]{40}$' -Because "the Record must name the DMS revision built"
        $sectionAt = $script:readme.IndexOf('## Restore recipe -- PostgreSQL')
        $preamble = $script:readme.Substring($sectionAt, $script:readme.IndexOf('```shell') - $sectionAt)
        $preamble | Should -Match $script:recordedRevision -Because "the reader must be told which revision the recipe is for before running it"
        $preamble | Should -Match 'Copy-NorthridgeDataForward\.ps1' -Because "a newer checkout is served by the copy-forward, not by this recipe"
    }

    It "compares the checkout's src/ against the recorded revision in step 3, before anything is materialized or staged, and stops with the way out" {
        # The false pass: step 3 expects one effective schema hash and step 5c compares against the
        # checkout's own DDL, but nothing checked that the checkout was the revision the artifact
        # records, so on a later main the recipe ran until the hash differed -- or until 5c failed,
        # after the restore.
        $assign = [regex]::Match($script:activeStep3, '(?m)^ARTIFACT_DMS_REV=(?<sha>[0-9a-f]{40})\b')
        $assign.Success | Should -BeTrue -Because "the recorded revision must be a value the guard reads, not prose"
        $assign.Groups["sha"].Value | Should -Be $script:recordedRevision -Because "the guard and the Record must name the same revision"
        $present = [regex]::Match($script:activeStep3, '(?m)^git cat-file -e "\$\{ARTIFACT_DMS_REV\}\^\{commit\}"[^\n]*\|\| \\\n\s+\{ echo "[^"]*"; exit 1; \}$')
        $present.Success | Should -BeTrue -Because "a clone without the commit must stop rather than be compared against nothing"
        $guard = [regex]::Match($script:activeStep3, '(?m)^test -d \.\./\.\./src && git diff --quiet "\$ARTIFACT_DMS_REV" HEAD -- \.\./\.\./src \|\| \\\n\s+\{ echo "(?<message>[^"]*)"; exit 1; \}$')
        $guard.Success | Should -BeTrue -Because "the comparison is by content of src/, commit to commit, so an equivalent commit passes and a missing path cannot pass as unchanged"
        $guard.Index | Should -BeGreaterThan $present.Index
        $guard.Index | Should -BeGreaterThan $script:activeStep3.IndexOf('cd "$DC"') -Because "git runs inside the checkout"
        $guard.Index | Should -BeLessThan $script:activeStep3.IndexOf('dotnet restore') -Because "nothing is materialized or staged from the wrong revision"
        $guard.Groups["message"].Value | Should -Match '\$ARTIFACT_DMS_REV' -Because "the operator is told which revision to check out"
        $guard.Groups["message"].Value | Should -Match 'Copy-NorthridgeDataForward\.ps1' -Because "the operator on a newer checkout is sent to the copy-forward"
        $guard.Groups["message"].Value | Should -Not -Match 'RECOVER_FROM_REF' -Because "nothing has been set aside yet"
    }

    It "guards the step 4 bootstrap so a failed deployment stops the recipe before step 5 sets it aside as the reference" {
        # The false pass: the recipe carries no set -e, so a bootstrap that failed part-way was followed
        # by step 5 renaming the incomplete deployment to the reference and comparing the restore to it.
        $guard = [regex]::Match($script:activeStep4, '(?m)^pwsh -NoProfile -File \./bootstrap-local-dms\.ps1 -DatabaseEngine postgresql -IdentityProvider self-contained \|\| \\\n\s+\{ echo "(?<message>[^"]*)"; exit 1; \}$')
        $guard.Success | Should -BeTrue -Because "the bootstrap must stop the recipe when it fails"
        $guard.Groups["message"].Value | Should -Match 'step 5' -Because "the operator is told not to run step 5 over an incomplete deployment"
        $guard.Groups["message"].Value | Should -Not -Match 'RECOVER_FROM_REF' -Because "no reference exists yet, so there is nothing to recover"
        @([regex]::Matches($script:activeStep4, 'bootstrap-local-dms\.ps1')).Count | Should -Be 1 -Because "step 4 runs the bootstrap once, guarded"
    }
}

Describe "Compare-DmsSchemaSnapshot.ps1 refuses a snapshot that captured nothing" {
    It "throws after each export when the captured row count is zero, naming the database and the schemas" {
        # The false pass: two snapshots of nothing are byte-identical, and the row count was only printed.
        $text = Get-Content -Raw -LiteralPath $script:compareScript
        $export = [regex]::Match($text, '(?m)^\s*\$rowCount = Export-SchemaSnapshot -DatabaseName \$databaseName [^\n]*`\r?\n[^\n]*$')
        $export.Success | Should -BeTrue -Because "the export must still be what produces the count"
        $after = $text.Substring($export.Index + $export.Length)
        $guard = [regex]::Match($after, '(?s)\A\s*(?:#[^\n]*\n\s*)*if \(\$rowCount -le 0\) \{\s*throw "(?<message>[^"]*)"\s*\}')
        $guard.Success | Should -BeTrue -Because "the zero-row check must be the next statement after the export"
        $guard.Groups["message"].Value | Should -Match '\$databaseName'
        $guard.Groups["message"].Value | Should -Match '\$\(\$Schema -join'
        ($guard.Index + $guard.Length) | Should -BeLessThan $after.IndexOf('Write-Output "Captured') -Because "nothing is reported as captured until the count is known to be non-zero"
    }
}

Describe "Restore recipe recovers from a failed restore without touching the reference" {
    BeforeAll {
        $script:helper = [regex]::Match($script:recipe, '(?ms)^RECOVER_FROM_REF\(\) \{.*?^\}').Value
        $script:activeHelper = (($script:helper -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $script:step5to6 = [regex]::Match($script:recipe, '(?ms)^# 5\. Stop the applications.*?(?=^# 7\. )').Value
        $script:activeStep5to6 = (($script:step5to6 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $script:readme = Get-Content -Raw -LiteralPath $script:readmePath
    }

    It "defines RECOVER_FROM_REF once, after DB and REF are known and before anything that can fail past the rename" {
        # The false pass: every failure said "start again from step 4"; the bootstrap does not reset
        # volumes, so the partial $DB survived it, and a second pass through step 5 dropped the intact
        # reference as stale and renamed the partial database onto its name.
        $script:helper | Should -Not -BeNullOrEmpty -Because "the recipe must define RECOVER_FROM_REF"
        @([regex]::Matches($script:recipe, '(?m)^RECOVER_FROM_REF\(\) \{')).Count | Should -Be 1
        $helperAt = $script:recipe.IndexOf('RECOVER_FROM_REF() {')
        $helperAt | Should -BeGreaterThan $script:recipe.IndexOf('REF="${DB}_reference"')
        $helperAt | Should -BeGreaterThan $script:recipe.IndexOf('DBUSER=$(docker exec dms-postgresql printenv POSTGRES_USER)')
        $helperAt | Should -BeLessThan $script:recipe.IndexOf('createdb -U "$DBUSER"')
    }

    It "reads its inputs from the container, so the same text works pasted alone into a fresh shell" {
        # The false pass: the helper read DB, DBUSER and REF from the shell that defined it, and every
        # failure guard ended that shell with exit 1 -- so the recovery, and the variables it needed,
        # were gone exactly when the operator was told to run it.
        $script:activeHelper | Should -Match '(?m)^\s+_db=\$\(docker exec dms-postgresql printenv POSTGRES_DB_NAME\)$'
        $script:activeHelper | Should -Match '(?m)^\s+_dbuser=\$\(docker exec dms-postgresql printenv POSTGRES_USER\)$'
        $script:activeHelper | Should -Match '(?m)^\s+_ref="\$\{_db\}_reference"$'
        $script:activeHelper | Should -Match 'test -n "\$_db" -a -n "\$_dbuser" \|\|' -Because "an unreadable container must stop the helper before it touches anything"
        $script:activeHelper | Should -Not -Match '\$(DB|DBUSER|REF)\b' -Because "nothing in the helper may depend on the recipe's shell variables"
        $script:activeHelper | Should -Not -Match '\bexit\b' -Because "the helper is pasted into the operator's shell and must return, not end it"
    }

    It "proves the reference exists before it drops anything, drops only the partial target, then renames the reference back" {
        $existsAt = $script:activeHelper.IndexOf('SELECT 1 FROM pg_database WHERE datname = :''ref'';')
        $refuseAt = $script:activeHelper.IndexOf('test -n "$_ref_exists" ||')
        $dropAt = $script:activeHelper.IndexOf('dropdb -U "$_dbuser" --maintenance-db=postgres --if-exists -- "$_db"')
        $renameAt = $script:activeHelper.IndexOf("SELECT format('ALTER DATABASE %I RENAME TO %I', :'ref', :'db') \gexec")
        $existsAt | Should -BeGreaterThan -1
        $refuseAt | Should -BeGreaterThan $existsAt
        $dropAt | Should -BeGreaterThan $refuseAt -Because "nothing may be dropped until the reference is known to exist"
        $renameAt | Should -BeGreaterThan $dropAt -Because "the reference takes the target's name only once the partial target is gone"
        $script:activeHelper | Should -Not -Match 'dropdb [^\n]*_ref"' -Because "the helper must never drop the reference"
        @([regex]::Matches($script:activeHelper, 'return 1')).Count | Should -Be 5 -Because "the container read, the existence query, the refusal, the drop and the rename each stop the helper"
        $script:activeHelper | Should -Match '-v ref="\$_ref" -v db="\$_db" -f - <<''SQL'''
    }

    It "refuses to proceed past an existing reference instead of dropping it" {
        # The false pass: a stale $REF was dropped as a leftover, and it was the intact deployment an
        # earlier attempt had set aside. The guard is held to its shape, not its wording: it tests the
        # existence flag, prints a message and stops with exit 1; the message points at the paste-alone
        # Recovery block, because exit 1 ends the shell that defined the in-shell helper; and it
        # neither drops the reference nor recovers over it -- the target may be a finished restore the
        # operator wants.
        $guard = [regex]::Match($script:step5to6, '(?m)^test -z "\$_ref_exists" \|\| \\\r?\n\s+\{ echo "[^\n]*"; exit 1; \}$')
        $guard.Success | Should -BeTrue -Because "an existing reference must stop the recipe with exit 1"
        $guard.Index | Should -BeGreaterThan $script:step5to6.IndexOf('RECOVER_FROM_REF() {') -Because "the guard belongs to the step 5 preflight that follows the helper definition"
        $guard.Index | Should -BeLessThan $script:step5to6.IndexOf("SELECT format('ALTER DATABASE %I RENAME TO %I', :'db', :'ref') \gexec") -Because "the guard runs before the rename that would collide"
        $guard.Value | Should -Match 'Recovery after a failed restore' -Because "the message must point at the paste-alone Recovery block"
        $guard.Value | Should -Not -Match 'RECOVER_FROM_REF' -Because "the helper is neither run here nor named as something to run from a shell exit 1 is about to end"
        $guard.Value | Should -Not -Match 'dropdb'
        $script:step5to6 | Should -Not -Match 'dropdb [^\n]*--if-exists -- "\$REF"' -Because "dropping a stale reference is the destructive step this closes"
    }

    It "runs the helper inside every failure guard from createdb through step 6, before the shell stops" {
        # The false pass: the guards said "run RECOVER_FROM_REF" and then ran exit 1, which ended the
        # shell the helper lived in. Each guard now recovers itself. The docker cp guard is one of them:
        # it fails after the rename and createdb, with an empty $DB and the reference set aside. So is
        # the repair.sql write: it fails with $DB restored but unrepaired and the reference set aside,
        # and "re-run from step 5b" was no way out -- the next step 5 met its own stale-reference guard.
        $script:recipe | Should -Not -Match 'start again from step 4'
        $script:recipe | Should -Not -Match 'start over from step 4'
        $script:recipe | Should -Not -Match 'RESUME POINT' -Because "after the shell has stopped there is no point inside step 5 to resume at; the next attempt starts step 5 over"
        foreach ($failure in @(
                'could not create database \$DB',
                'could not copy the dump into the container',
                'holds a PARTIAL restore',
                'entries were skipped',
                'could not write \$ART/repair\.sql',
                'step 5c failed',
                'step 6 failed'
            )) {
            $at = [regex]::Match($script:activeStep5to6, '(?m)^[^\n]*' + $failure + '[^\n]*$')
            $at.Success | Should -BeTrue -Because "the failure '$failure' must still be reported"
            $tail = $script:activeStep5to6.Substring($at.Index)
            $stop = $tail.IndexOf('exit 1')
            $stop | Should -BeGreaterThan -1 -Because "the failure '$failure' must stop the recipe"
            $tail.Substring(0, $stop + 6) | Should -Match 'RECOVER_FROM_REF; exit 1$' -Because "the guard for '$failure' must put the deployment back before the shell stops"
        }
        @([regex]::Matches($script:activeStep5to6, 'RECOVER_FROM_REF; exit 1')).Count | Should -Be 7 -Because "createdb, docker cp, the two pg_restore checks, the repair.sql write, 5c and 6 recover; nothing else does"
        # A failed rename changed nothing, so it must not recover: there is no reference yet.
        $rename = [regex]::Match($script:activeStep5to6, '(?m)^[^\n]*could not rename \$DB to \$REF[^\n]*$').Value
        $rename | Should -Not -BeNullOrEmpty
        $rename | Should -Not -Match 'RECOVER_FROM_REF'
        # A rolled-back repair is re-run in place first; the recovery is the fallback, in either shell.
        $step5b = [regex]::Match($script:activeStep5to6, '(?m)^[^\n]*step 5b failed[^\n]*$').Value
        $step5b | Should -Match '\bRECOVER_FROM_REF\b' -Because "the recovery is named as the fallback for a cause that cannot be fixed in place"
        $step5b | Should -Not -Match 'RECOVER_FROM_REF; exit' -Because "a rolled-back repair is re-run in place, not recovered over"
        $repairSql = [regex]::Match($script:recipe, "(?ms)<<'REPAIR_SQL'[^\r\n]*\r?\n(?<sql>.*?)^REPAIR_SQL\s*$").Groups["sql"].Value
        $repairSql | Should -Not -BeNullOrEmpty
        $repairSql | Should -Not -Match 'start (again|over) from step 4' -Because "a cluster without the role was never deployed to; the way back is a wipe, not step 4"
        $repairSql | Should -Match 'bootstrap-local-dms\.ps1 -d -v' -Because "the way back is the wipe, named as the command"
    }

    It "refuses a reference name longer than PostgreSQL's 63-byte identifier limit, before the rename" {
        # The false pass: PostgreSQL truncates a long identifier with a NOTICE rather than an error, so
        # the deployment was renamed to a name no later lookup of $REF would find, and the recovery
        # would then report that it had never been set aside.
        $guard = [regex]::Match($script:activeStep5to6, '(?m)^test "\$\(printf ''%s'' "\$REF" \| wc -c[^\n]*\)" -le 63 \|\| \\\n\s+\{ [^\n]*; exit 1; \}$')
        $guard.Success | Should -BeTrue -Because "the byte length must be measured with printf and wc -c and refused above 63"
        $guard.Index | Should -BeGreaterThan $script:activeStep5to6.IndexOf('REF="${DB}_reference"')
        $guard.Index | Should -BeLessThan $script:activeStep5to6.IndexOf("SELECT format('ALTER DATABASE %I RENAME TO %I', :'db', :'ref') \gexec") -Because "nothing may be renamed to a name the server would truncate"
        $guard.Value | Should -Not -Match 'RECOVER_FROM_REF' -Because "nothing has been renamed yet, so there is nothing to recover"
        $guard.Value | Should -Not -Match '\$\{#REF\}' -Because "the shell's length operator counts characters, not bytes"
    }

    It "publishes the same helper as a paste-alone Recovery block after the recipe" {
        # The block a fresh shell needs is the second fenced shell block of the README. It must be the
        # helper byte for byte -- two copies that drift are two recoveries -- followed by one call.
        $block = @([regex]::Matches($script:readme, '(?ms)^```shell\r?\n(?<body>.*?)^```'))
        $block.Count | Should -Be 2 -Because "the recipe and the Recovery block, and nothing else"
        $recovery = $block[1].Groups["body"].Value
        $recoveryHelper = [regex]::Match($recovery, '(?ms)^RECOVER_FROM_REF\(\) \{.*?^\}').Value
        $recoveryHelper | Should -Be $script:helper -Because "the Recovery block must be the step 5 helper, unchanged"
        ($recovery -split "\r?\n" | Where-Object { $_ -notmatch '^\s*#' -and $_.Trim() -ne '' })[-1] | Should -Be 'RECOVER_FROM_REF' -Because "the block ends by running the helper"
        $recovery | Should -Not -Match '\bexit\b' -Because "a pasted block must not end the operator's shell"
        $heading = $script:readme.IndexOf('### Recovery after a failed restore')
        $heading | Should -BeGreaterThan $script:readme.IndexOf($block[0].Value) -Because "the section follows the recipe"
        $block[1].Index | Should -BeGreaterThan $heading
    }

    It "drops the reference exactly once, only after step 6 has passed" {
        $drop = @([regex]::Matches($script:recipe, '(?m)^docker exec dms-postgresql dropdb -U "\$DBUSER" --maintenance-db=postgres -- "\$REF" \|\| \\$'))
        $drop.Count | Should -Be 1
        $drop[0].Index | Should -BeGreaterThan $script:recipe.IndexOf('#    Expect: NOTICE: restore verified') -Because "step 6 is the last gate that recovers through the reference"
        $drop[0].Index | Should -BeLessThan $script:recipe.IndexOf('# 7. REQUIRED')
        $step5c = [regex]::Match($script:recipe, '(?ms)^# 5c\..*?(?=^# 6\. )').Value
        $step5c | Should -Not -BeNullOrEmpty
        $step5c | Should -Not -Match 'dropdb'
    }

    It "sends the reader to the helper and the Recovery block, not to step 4, in the note after the recipe" {
        $note = [regex]::Match($script:readme, '(?ms)^> \*\*Do not drop and recreate the database.*?(?=\r?\n\r?\n)').Value
        $note | Should -Not -BeNullOrEmpty
        $note | Should -Match 'RECOVER_FROM_REF'
        $note | Should -Match 'Recovery after a failed restore'
        $note | Should -Not -Match 'start over from step 4'
        $note | Should -Match 'bootstrap-local-dms\.ps1 -d -v'
    }
}

Describe "Copy-NorthridgeDataForward.ps1 row counts cover every table or fail" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "ConvertTo-RowCountMap")))
        $script:countTable = @("dms.Document", "edfi.Student", "edfi.School")
        $script:countRow = @("dms.Document|3", "edfi.School|2", "edfi.Student|5")
    }

    It "maps one count per table, keyed ordinally, and tolerates psql's trailing empty element" {
        $map = ConvertTo-RowCountMap -Row ($script:countRow + "") -ExpectedTable $script:countTable
        $map.Count | Should -Be 3
        $map.Comparer | Should -Be ([System.StringComparer]::Ordinal)
        $map["edfi.Student"] | Should -Be 5
        $map["dms.Document"] | Should -Be 3
        $map.ContainsKey("edfi.student") | Should -BeFalse -Because "a quoted identifier is case-sensitive, so this is another table"
    }

    It "refuses a row it cannot parse rather than dropping it" {
        # The false pass: a row that did not match was skipped, the table dropped out of both maps at
        # once, and absence compared equal to absence.
        foreach ($bad in @("edfi.Broken", "edfi.Broken|", "edfi.Broken|abc", "edfi.Broken|-1", "|7", "psql: warning: something")) {
            { ConvertTo-RowCountMap -Row ($script:countRow + $bad) -ExpectedTable ($script:countTable + "edfi.Broken") } |
                Should -Throw "*refusing to drop it*" -Because "'$bad' must stop the run, not be skipped"
        }
    }

    It "refuses a table reported twice" {
        { ConvertTo-RowCountMap -Row ($script:countRow + "edfi.School|9") -ExpectedTable $script:countTable } |
            Should -Throw "*reported 'edfi.School' twice*"
    }

    It "refuses a parsed set that does not cover exactly the requested tables" {
        # The false pass: a table with no count on either side was never compared, because the
        # reconciliation walked the union of what parsed rather than the list it asked for.
        { ConvertTo-RowCountMap -Row $script:countRow[0..1] -ExpectedTable $script:countTable } |
            Should -Throw "*Missing: edfi.Student. Unexpected: none.*"
        { ConvertTo-RowCountMap -Row @() -ExpectedTable $script:countTable } |
            Should -Throw "*cover 0 table(s) for 3 requested. Missing: dms.Document, edfi.School, edfi.Student.*"
        { ConvertTo-RowCountMap -Row ($script:countRow + "edfi.Extra|1") -ExpectedTable $script:countTable } |
            Should -Throw "*Missing: none. Unexpected: edfi.Extra.*"
    }

    It "is what Get-RowCountMap returns, and the reconciliation walks the table list rather than the parsed keys" {
        $text = Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Get-RowCountMap"
        $text | Should -Match 'return ConvertTo-RowCountMap -Row @\(\$rows\) -ExpectedTable \$QualifiedTable'
        $text | Should -Not -Match '-match ' -Because "no parsing may remain outside the helper that fails closed"
        $copyText = Get-Content -Raw -LiteralPath $script:copyScript
        $copyText | Should -Match '(?m)^foreach \(\$table in \(Get-OrdinalSortedUnique -Value \$allTable\)\) \{$' -Because "every table on the list is asked about on both sides"
        $copyText | Should -Not -Match 'Get-OrdinalSortedUnique -Value \(@\(\$sourceCount\.Keys\) \+ @\(\$targetCount\.Keys\)\)' -Because "the union of what parsed cannot see a table that parsed on neither side"
    }
}

Describe "Copy-NorthridgeDataForward.ps1 requires every bulk schema to contribute tables on both sides" {
    BeforeAll {
        . ([scriptblock]::Create((Get-ScriptAssignmentText -ScriptPath $script:copyScript -VariablePath "script:BulkSchema")))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Get-BulkSchemaCoverageFailure")))
        $script:fullBulk = @("auth.EducationOrganizationIdToEducationOrganizationId", "edfi.School", "edfi.Student", "tracked_changes_edfi.Student")
        $script:copyText = Get-Content -Raw -LiteralPath $script:copyScript
    }

    It "names the three schemas the published artifact carries" {
        $script:BulkSchema | Should -Be @("edfi", "tracked_changes_edfi", "auth")
    }

    It "reports nothing when every schema contributes on both sides" {
        (Get-BulkSchemaCoverageFailure -SourceTable $script:fullBulk -TargetTable $script:fullBulk -Schema $script:BulkSchema).Count | Should -Be 0
    }

    It "fails schema '<Schema>' contributing no table on either side, naming the schema and the side" -ForEach @(
        @{ Schema = "auth" }, @{ Schema = "tracked_changes_edfi" }, @{ Schema = "edfi" }
    ) {
        # The false pass: discovery is by schema, so a schema absent from a database -- or holding no
        # base table -- contributed nothing to its list, both lists agreed, and the copy loaded the
        # remaining schemas and reported PASS around the hole.
        $without = @($script:fullBulk | Where-Object { -not $_.StartsWith("$Schema.", [System.StringComparison]::Ordinal) })
        $without.Count | Should -BeLessThan $script:fullBulk.Count
        $source = Get-BulkSchemaCoverageFailure -SourceTable $without -TargetTable $script:fullBulk -Schema $script:BulkSchema
        $source.Count | Should -Be 1
        $source[0] | Should -Match "^schema '$Schema' contributes no base table in the source"
        $target = Get-BulkSchemaCoverageFailure -SourceTable $script:fullBulk -TargetTable $without -Schema $script:BulkSchema
        $target.Count | Should -Be 1
        $target[0] | Should -Match "^schema '$Schema' contributes no base table in the target"
        $both = Get-BulkSchemaCoverageFailure -SourceTable $without -TargetTable $without -Schema $script:BulkSchema
        $both.Count | Should -Be 2 -Because "two lists that both lack the schema agree, which is exactly the case this closes"
        (Get-BulkSchemaCoverageFailure -SourceTable @() -TargetTable @() -Schema $script:BulkSchema).Count | Should -Be (2 * $script:BulkSchema.Count)
    }

    It "treats a schema whose name differs only in case as absent" {
        $wrongCase = @($script:fullBulk | ForEach-Object { $_ -replace '^auth\.', 'Auth.' })
        (Get-BulkSchemaCoverageFailure -SourceTable $wrongCase -TargetTable $script:fullBulk -Schema $script:BulkSchema) -join "`n" |
            Should -Match "schema 'auth' contributes no base table in the source"
    }

    It "runs in copy mode before the two lists are compared or trusted" {
        $coverageAt = $script:copyText.IndexOf('Get-BulkSchemaCoverageFailure -SourceTable $sourceBulkTable -TargetTable $targetBulkTable -Schema $script:BulkSchema')
        $coverageAt | Should -BeGreaterThan -1
        $coverageAt | Should -BeGreaterThan $script:copyText.IndexOf('$targetBulkTable = Get-DataTableList -DatabaseName $TargetDatabase')
        $coverageAt | Should -BeLessThan $script:copyText.IndexOf('$sourceTableSet = ') -Because "coverage is proven before the sets are compared to each other"
        $coverageAt | Should -BeLessThan $script:copyText.IndexOf('$bulkTable = $sourceBulkTable') -Because "the list is trusted only once every schema is known to be in it"
    }
}

Describe "Add-NorthridgeGapDocument.ps1 takes its client secret from exactly one source" {
    BeforeAll {
        $script:secretCommon = @{
            DmsBaseUrl = "http://dms.test"
            TokenUrl   = "http://cms.test/connect/token"
            ClientId   = "client"
        }
        $script:secretManifest = Join-Path $TestDrive "secret-source.json"
        [ordered]@{ documents = @([ordered]@{ order = 1; label = "Staff: One"; endpoint = "/data/ed-fi/staffs"; body = [ordered]@{ staffUniqueId = "S1" } }) } |
            ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $script:secretManifest -Encoding utf8
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:gapScript -FunctionName "Get-DmsAccessToken")))
    }

    It "refuses -ClientSecret together with -ClientSecretEnvironmentVariable, -WhatIf included" {
        { & $script:gapScript -ManifestPath $script:secretManifest @script:secretCommon -ClientSecret "literal" -ClientSecretEnvironmentVariable "NR_TEST_SECRET" -WhatIf } |
            Should -Throw "*either -ClientSecret or -ClientSecretEnvironmentVariable, not both*"
    }

    It "still prints the plan under -WhatIf, with a literal secret or with none, requesting no token" {
        Mock Invoke-RestMethod { throw "no token request may be made under -WhatIf" }
        $withLiteral = @(& $script:gapScript -ManifestPath $script:secretManifest @script:secretCommon -ClientSecret "literal" -WhatIf)
        ($withLiteral -join "`n") | Should -Match "No token was requested and no write was issued"
        $withNone = @(& $script:gapScript -ManifestPath $script:secretManifest @script:secretCommon -WhatIf)
        ($withNone -join "`n") | Should -Match "No token was requested and no write was issued"
        Should -Invoke Invoke-RestMethod -Times 0 -Exactly
    }

    It "requires one secret source before it issues a write" {
        Mock Invoke-RestMethod { throw "no token request may be made without a secret" }
        { & $script:gapScript -ManifestPath $script:secretManifest @script:secretCommon } |
            Should -Throw "*-ClientSecret or -ClientSecretEnvironmentVariable, exactly one*"
        Should -Invoke Invoke-RestMethod -Times 0 -Exactly
    }

    It "refuses a named environment variable that is not set" {
        Remove-Item Env:\NR_TEST_SECRET_ABSENT -ErrorAction SilentlyContinue
        Mock Invoke-RestMethod { throw "no token request may be made without a secret" }
        { & $script:gapScript -ManifestPath $script:secretManifest @script:secretCommon -ClientSecretEnvironmentVariable "NR_TEST_SECRET_ABSENT" } |
            Should -Throw "*'NR_TEST_SECRET_ABSENT'*not set*"
        Should -Invoke Invoke-RestMethod -Times 0 -Exactly
    }

    It "sends the value of the named environment variable as the client secret, and never logs it" {
        $env:NR_TEST_SECRET = "from-environment"
        try {
            Mock Invoke-RestMethod { [pscustomobject]@{ access_token = "token" } }
            Mock Invoke-WebRequest {
                if ("$Method" -eq "Post") {
                    return [pscustomobject]@{ StatusCode = 201; Headers = @{ Location = "/data/ed-fi/staffs/id-1" }; Content = "" }
                }
                return [pscustomobject]@{ StatusCode = 200; Headers = @{}; Content = '{"id":"id-1","staffUniqueId":"S1","_etag":"e"}' }
            }
            $output = @(& $script:gapScript -ManifestPath $script:secretManifest @script:secretCommon -ClientSecretEnvironmentVariable "NR_TEST_SECRET")
            ($output -join "`n") | Should -Match "PASS: every document was created and verified by GET-by-id"
            Should -Invoke Invoke-RestMethod -Times 1 -Exactly -ParameterFilter { $Body.client_secret -ceq "from-environment" }
            ($output -join "`n") | Should -Not -Match "from-environment" -Because "the secret is never logged"
        }
        finally {
            Remove-Item Env:\NR_TEST_SECRET -ErrorAction SilentlyContinue
        }
    }

    It "reports a token response with no access_token as such under strict mode" {
        # The false failure: under Set-StrictMode -Version Latest, reading $response.access_token on a
        # body without that member threw a property-not-found error, so the message that says the
        # endpoint returned no token was never reached.
        Set-StrictMode -Version Latest
        Mock Invoke-RestMethod { [pscustomobject]@{ error = "invalid_client" } }
        { Get-DmsAccessToken -Url "http://cms.test/connect/token" -Id "client" -Secret "s" -RequestedScope "scope" } |
            Should -Throw "*returned no access_token*"
        Mock Invoke-RestMethod { "not json" }
        { Get-DmsAccessToken -Url "http://cms.test/connect/token" -Id "client" -Secret "s" -RequestedScope "scope" } |
            Should -Throw "*returned no access_token*"
        Mock Invoke-RestMethod { [pscustomobject]@{ access_token = "  " } }
        { Get-DmsAccessToken -Url "http://cms.test/connect/token" -Id "client" -Secret "s" -RequestedScope "scope" } |
            Should -Throw "*returned no access_token*"
        Mock Invoke-RestMethod { [pscustomobject]@{ access_token = "abc" } }
        Get-DmsAccessToken -Url "http://cms.test/connect/token" -Id "client" -Secret "s" -RequestedScope "scope" | Should -Be "abc"
    }
}

Describe "Northridge PostgreSQL restore recipe identity handoff" {
    # Moved here from eng/docker-compose/tests/OpenIddictCrypto.Tests.ps1: these cases read the
    # Northridge recipe, so they belong to the suite that owns it, where a compose refactor cannot
    # fail them and a recipe change cannot fail a compose suite.
    BeforeAll {
        $script:SetupOpenIddictInvocation = {
            $lines = $script:recipe -split "`r?`n"
            $start = [array]::FindIndex($lines, [Predicate[string]] {
                    param($line)
                    $line -match '^\s*(CSEC="\$CSEC" )?pwsh -NoProfile -File \./setup-openiddict\.ps1 -InsertData'
                })
            $start | Should -BeGreaterOrEqual 0

            $invocationLines = [System.Collections.Generic.List[string]]::new()
            for ($i = $start; $i -lt $lines.Count; $i++) {
                $invocationLines.Add($lines[$i])
                if ($lines[$i] -notmatch '\\\s*$') { break }
            }

            return ($invocationLines -join "`n")
        }
    }

    It "registers restore-admin with the live CMS identity secret and validation bounds" {
        $script:activeRecipe | Should -Match 'CMSENV=\$\(docker inspect .*ed-fi-api-config-service\)'
        $script:activeRecipe | Should -Match 'ADMIN_SECRET=\$\(printf ''%s\\n'' "\$CMSENV" \| sed -n ''s/\^IdentitySettings__ClientSecret=//p''\)'
        $script:activeRecipe | Should -Match 'CLIENT_SECRET_MIN=\$\(printf ''%s\\n'' "\$CMSENV" \| sed -n ''s/\^IdentitySettings__ClientSecretValidation__MinimumLength=//p''\)'
        $script:activeRecipe | Should -Match 'CLIENT_SECRET_MAX=\$\(printf ''%s\\n'' "\$CMSENV" \| sed -n ''s/\^IdentitySettings__ClientSecretValidation__MaximumLength=//p''\)'
        # The live secret reaches CMS through the pwsh helpers, as the environment of that one process:
        # CMS_SECRET is set from the value read above, and the helpers send $env:CMS_SECRET.
        $script:activeRecipe | Should -Match '(?m)^CMS_SECRET="\$ADMIN_SECRET"$'
        $script:activeRecipe | Should -Match '(?m)^CMS_REGISTER restore-admin "Restore Admin" \|\| \{ [^}]*; exit 1; \}$'
        $script:activeRecipe | Should -Match '(?m)^T=\$\(CMS_TOKEN restore-admin edfi_admin_api/full_access\) \|\| \\$'
        $script:activeRecipe | Should -Match 'ClientSecret = \$env:CMS_SECRET'
        $script:activeRecipe | Should -Match 'client_secret = \$env:CMS_SECRET'
        $script:activeRecipe | Should -Not -Match 'ValidClientSecret1234567890!Abcd'
    }

    It "keeps every client secret out of curl's argument list and reports a failed registration as one" {
        # A curl argument list is readable by every process on the host while curl runs. Every call that
        # carries a client secret -- the registration and the three token requests -- goes through the
        # pwsh helpers, which read the secret from their own environment; and the registration asserts
        # its status and prints the body, so a 4xx/5xx there stops the recipe instead of surfacing at the
        # token check as a misleading signing-key failure.
        $script:activeRecipe | Should -Not -Match '(?im)^[^\n]*\bcurl\b[^\n]*secret=' -Because "no curl invocation may carry a secret in its arguments"
        # Nor may any pwsh invocation: a secret-bearing shell variable may appear before `pwsh` only, as
        # the environment prefix of that one process, never among its arguments -- continuation lines
        # are joined so a multi-line invocation is read whole.
        $joined = $script:activeRecipe -replace '\\\n\s*', ' '
        $joined | Should -Not -Match '(?m)\bpwsh\b[^\n]*"\$(CSEC|SEC|ADMIN_SECRET|CMS_SECRET|CMSCS|IDK|KEY_SQL|PW|T|DT|TOKEN|BODY|DS_BODY)"' -Because "no pwsh argument may carry a secret; the environment prefix before pwsh is the only place one may appear"
        $script:activeRecipe | Should -Not -Match '(?i)--data-urlencode "[^"]*secret='
        @([regex]::Matches($script:activeRecipe, '(?m)^CMS_SECRET="\$(ADMIN_SECRET|CSEC|SEC)"$')).Count | Should -Be 3 -Because "each secret is scoped to CMS_SECRET from a variable the recipe already holds"
        @([regex]::Matches($script:activeRecipe, '(?m)^\w+=\$\(CMS_TOKEN ')).Count | Should -Be 3 -Because "restore-admin, the DMS-to-CMS client and the consumer's client each mint one token"
        @([regex]::Matches($script:activeRecipe, '(?m)^CMS_REGISTER ')).Count | Should -Be 1

        $register = [regex]::Match($script:recipe, '(?ms)^CMS_REGISTER\(\) \{.*?^\}').Value
        $register | Should -Not -BeNullOrEmpty -Because "the recipe must define CMS_REGISTER"
        $register | Should -Match 'Invoke-WebRequest -Method Post -Uri "\$env:CMS/connect/register" -SkipHttpErrorCheck'
        $register | Should -Match '\[int\]\$response\.StatusCode -ne 200'
        $register | Should -Match '\$\(\$response\.Content\)' -Because "the failure message must carry the body CMS answered with"
        $register | Should -Match '(?m)^\s+exit 1$'
        $register | Should -Match '-TimeoutSec [1-9]\d*' -Because "the request replaced a curl call and must not hang on a service that accepts and never answers"
        $register | Should -Not -Match 'curl'

        $token = [regex]::Match($script:recipe, '(?ms)^CMS_TOKEN\(\) \{.*?^\}').Value
        $token | Should -Not -BeNullOrEmpty -Because "the recipe must define CMS_TOKEN"
        $token | Should -Match 'Invoke-WebRequest -Method Post -Uri "\$env:CMS/connect/token" -SkipHttpErrorCheck'
        $token | Should -Match '\[int\]\$response\.StatusCode -eq 200'
        $token | Should -Match '\$\(\$response\.Content\)'
        $token | Should -Match '-TimeoutSec [1-9]\d*' -Because "the request replaced a curl call and must not hang on a service that accepts and never answers"
        $token | Should -Not -Match 'curl'
    }

    It "keeps every bearer token out of curl's argument list and routes token-bearing calls through AUTH_HTTP" {
        # The same class as the client secrets: a token in a curl argument is readable by every process
        # on the host while curl runs. Every request that carries one goes through AUTH_HTTP, which
        # reads the token from TOKEN and the JSON body from BODY in its own environment, prints the
        # status on its first line and the body after it, and writes the headers to a file -- so each
        # caller keeps the status check and the body diagnostics it had.
        $script:activeRecipe | Should -Not -Match 'Authorization: Bearer \$' -Because "no command line may carry a token"
        $script:activeRecipe | Should -Not -Match '(?m)^[^\n]*\bcurl\b[^\n]*(Bearer|-H "Authorization|/v3/|/data/)' -Because "no token-bearing or API call may be a curl call"

        $helper = [regex]::Match($script:recipe, '(?ms)^AUTH_HTTP\(\) \{.*?^\}').Value
        $helper | Should -Not -BeNullOrEmpty -Because "the recipe must define AUTH_HTTP"
        $helper | Should -Match 'TOKEN="\$TOKEN" BODY="\$\{BODY:-\}" pwsh -NoProfile -Command'
        $helper | Should -Match 'Authorization = "Bearer \$env:TOKEN"'
        $helper | Should -Match '\$request\.Body = \$env:BODY'
        $helper | Should -Match 'SkipHttpErrorCheck = \$true' -Because "a 4xx/5xx must come back as a status the caller asserts, not as an exception"
        $helper | Should -Match 'TimeoutSec = [1-9]\d*' -Because "the request replaced a curl call and must not hang on a service that accepts and never answers"
        $helper | Should -Match '\[Console\]::Out\.WriteLine\(\[int\]\$response\.StatusCode\)'
        $helper | Should -Match 'Set-Content -Path \$env:HEADERS_FILE'
        $helper | Should -Not -Match 'curl'

        $call = @([regex]::Matches($script:activeRecipe, '(?m)^\w+=\$\(AUTH_HTTP (?<method>[A-Z]+) "(?<url>[^"]+)" "\$ART/[^"]+"\) \|\| \\$'))
        @($call | ForEach-Object { $_.Groups["method"].Value + " " + $_.Groups["url"].Value }) |
            Should -Be @('PUT $CMS/v3/dataStores/1', 'POST $CMS/v3/vendors', 'POST $CMS/v3/applications', 'GET $DMS/data/ed-fi/students?limit=1&totalCount=true')
        # Each call is preceded by the token it needs; the GET is preceded by an emptied BODY.
        @([regex]::Matches($script:activeRecipe, '(?m)^TOKEN="\$(T|DT)"$')).Count | Should -Be 3
        $script:activeRecipe | Should -Match '(?m)^TOKEN="\$DT"\nBODY=\nSMOKE_RESPONSE=\$\(AUTH_HTTP GET '
        # The statuses are still asserted against exact values and the bodies still shown on failure.
        $script:activeRecipe | Should -Match '(?m)^DS=\$\(printf ''%s\\n'' "\$DS_RESPONSE" \| sed -n 1p\)\nif \[ "\$DS" != "204" \]; then'
        $script:activeRecipe | Should -Match 'printf ''%s\\n'' "\$DS_RESPONSE" \| sed 1d'
        $script:activeRecipe | Should -Match '(?m)^SC=\$\(printf ''%s\\n'' "\$SMOKE_RESPONSE" \| sed -n 1p\)$'
        $script:activeRecipe | Should -Match 'if \[ "\$SC" != "200" \] \|\| \[ "\$TC" != "21628" \]; then'
        $script:activeRecipe | Should -Match 'sed -n ''s\|\^\[Ll\]ocation:\.\*/v3/vendors/' -Because "the vendor id is still read from the Location header, now from the headers file"
        # The data store body holds the database password and travels as BODY, never as a file.
        $script:activeRecipe | Should -Not -Match 'datastore\.json'
        $script:activeRecipe | Should -Match '(?m)^DS_BODY=\$\(PW="\$PW" DB="\$DB" DBUSER="\$DBUSER" pwsh -NoProfile -Command ''$'
        $script:activeRecipe | Should -Match '(?m)^BODY="\$DS_BODY"$'
        $script:activeRecipe | Should -Match '(?m)^unset BODY DS_BODY$'
    }

    It "asserts the exact success status of every AUTH_HTTP response before trusting its headers or body" {
        # AUTH_HTTP prints the status on its first line and never throws on a 4xx/5xx, so a caller that
        # reads Location or the body without reading the status first turns a 401, 403 or 500 into "no
        # Location" or "no credentials" with the cause gone. CMS answers the data store PUT with 204, a
        # new vendor with 201 (200, Location set, for a company it already holds: VendorModule creates by
        # company name), a new application with 201 and its credentials, and DMS the smoke read with 200.
        $active = $script:activeRecipe

        # Data store: the status is compared before anything else is done with the response.
        $active | Should -Match '(?m)^DS=\$\(printf ''%s\\n'' "\$DS_RESPONSE" \| sed -n 1p\)\nif \[ "\$DS" != "204" \]; then\n[^\n]*\n  printf ''%s\\n'' "\$DS_RESPONSE" \| sed 1d\n  exit 1\nfi$'

        # Vendor: the status is matched, and only 201 or 200 continue, before the Location header is read.
        $vendor = [regex]::Match($active, '(?ms)^VENDOR_RESPONSE=\$\(AUTH_HTTP POST "\$CMS/v3/vendors".*?^VID=').Value
        $vendor | Should -Not -BeNullOrEmpty
        $vendor | Should -Match '(?m)^VS=\$\(printf ''%s\\n'' "\$VENDOR_RESPONSE" \| sed -n 1p\)\ncase "\$VS" in\n  201\) [^\n]*;;\n  200\) [^\n]*;;\n  \*\) [^\n]*expected 201[^\n]*printf ''%s\\n'' "\$VENDOR_RESPONSE" \| sed 1d[^\n]*; exit 1 ;;\nesac$'
        $vendor.IndexOf('case "$VS" in') | Should -BeLessThan $vendor.IndexOf('VID=') -Because "the status is asserted before the Location header is parsed"
        $active | Should -Match '(?m)^test -n "\$VID" \|\| \\\n  \{ [^\n]*; exit 1; \}$' -Because "a success status with no Location still stops the recipe"

        # Application: exactly 201 before the body is parsed for the credentials.
        $app = [regex]::Match($active, '(?ms)^APP_RESPONSE=\$\(AUTH_HTTP POST "\$CMS/v3/applications".*?^KEY=').Value
        $app | Should -Not -BeNullOrEmpty
        $app | Should -Match '(?m)^AS=\$\(printf ''%s\\n'' "\$APP_RESPONSE" \| sed -n 1p\)\nif \[ "\$AS" != "201" \]; then\n[^\n]*expected 201[^\n]*\n  printf ''%s\\n'' "\$APP_RESPONSE" \| sed 1d\n  exit 1\nfi$'
        $app.IndexOf('if [ "$AS" != "201" ]') | Should -BeLessThan $app.IndexOf('APP=$(') -Because "the status is asserted before the body is parsed for credentials"

        # Smoke: exactly 200, together with the count.
        $active | Should -Match '(?m)^SC=\$\(printf ''%s\\n'' "\$SMOKE_RESPONSE" \| sed -n 1p\)$'
        $active | Should -Match 'if \[ "\$SC" != "200" \] \|\| \[ "\$TC" != "21628" \]; then'

        # Each response has its status line read into a variable exactly once, so no caller reads the
        # status only inside a failure message after it has already trusted the response.
        foreach ($response in @('DS_RESPONSE', 'VENDOR_RESPONSE', 'APP_RESPONSE', 'SMOKE_RESPONSE')) {
            @([regex]::Matches($active, [regex]::Escape("printf '%s\n' `"`$$response`" | sed -n 1p"))).Count | Should -Be 1 -Because "$response must have its status line read once, into a variable that is compared"
        }
    }

    It "passes the live PostgreSQL user, roles and client-secret bounds into setup-openiddict, with the secret in the environment" {
        $invocation = & $script:SetupOpenIddictInvocation

        # setup-openiddict.ps1 is a new process, so its argument list is readable by every process on
        # the host: the secret travels as CSEC in that process's environment and
        # -NewClientSecretEnvironmentVariable names the variable. -NewClientSecret is a literal for every
        # caller -- a secret may itself begin with "ENV:" -- so the recipe must not use it at all.
        $invocation | Should -Match '^CSEC="\$CSEC" pwsh -NoProfile -File \./setup-openiddict\.ps1 -InsertData'
        $invocation | Should -Match '-NewClientSecretEnvironmentVariable CSEC'
        $invocation | Should -Not -Match '-NewClientSecret\b' -Because "the secret must not be an argument, and the literal parameter never reads a variable"
        $invocation | Should -Not -Match 'ENV:CSEC' -Because "spelled as an indirection, the eight characters ENV:CSEC would be the secret validated and hashed"
        $invocation | Should -Match '-ConfigServiceRole "\$CMSROLE"'
        $invocation | Should -Match '-DmsClientRole "\$DMSROLE"'
        $invocation | Should -Match '-DbUser "\$DBUSER"'
        $invocation | Should -Match '-DbName "\$CMSDB"' -Because "the insert must target the database the DELETE cleared, as the same literal"
        $invocation | Should -Not -Match 'ENV:DMS_CONFIG_DATABASE_NAME' -Because "an indirection the script resolves on its own could name a database other than the one the DELETE ran against"
        $invocation | Should -Match '-ClientSecretMinimumLength "\$CLIENT_SECRET_MIN"'
        $invocation | Should -Match '-ClientSecretMaximumLength "\$CLIENT_SECRET_MAX"'
    }

    It "replaces the OpenIddict signing key in one guarded transaction and proves exactly one key is active" {
        # The recipe carries no set -e. Deactivating the producer's key and inserting the consumer's
        # must be one operation: run separately, a failed insert leaves no active key and a failed
        # deactivate leaves the producer's key trusted beside the new one, and neither shows until
        # the token check. So the generator's INSERT is assembled into one file between the
        # deactivate and an assertion, the file runs under -1 and ON_ERROR_STOP, and every command
        # that can fail is guarded with an exit.
        $step7 = [regex]::Match($script:recipe, '(?ms)^# 7\. REQUIRED: install your own OpenIddict signing key\..*?(?=^# 8\. )').Value
        $step7 | Should -Not -BeNullOrEmpty

        # The identity encryption key reaches the generator through the environment of that one pwsh
        # process, never as an argument, and the generated SQL -- private key and encryption key in
        # clear -- is held in the shell as KEY_SQL and piped, never written under $ART.
        $step7 | Should -Match '(?m)^KEY_SQL=\$\(IDK="\$IDK" pwsh -NoProfile -Command ''& \./Generate-OpenIddictKey-Insert\.ps1 -EncryptionKey \$env:IDK''\) \|\| \\\r?\n\s+\{ unset KEY_SQL IDK; .*; exit 1; \}$'
        $step7 | Should -Not -Match '-EncryptionKey "\$IDK"' -Because "the key must not be an argument of any process"
        $step7 | Should -Not -Match 'newkey' -Because "no key SQL file may be written"
        $step7 | Should -Not -Match '> "\$ART/' -Because "nothing in step 7 may be written under the scratch directory"
        $step7 | Should -Match '(?m)^printf ''%s\\n'' "\$KEY_SQL" \| grep -q ''\^INSERT INTO "dmscs"\."OpenIddictKey" '' \|\| \\\r?\n\s+\{ unset KEY_SQL IDK; .*; exit 1; \}$'
        $step7 | Should -Match '(?m)^\} \| docker exec -i dms-postgresql psql -U "\$DBUSER" -d "\$CMSDB" -v ON_ERROR_STOP=1 -q -1 -f - \|\| \\\r?\n\s+\{ unset KEY_SQL IDK; .*; exit 1; \}$'

        # One psql run over one stream: no separate deactivate, no copy into the container.
        $activeStep7 = (($step7 -split "`r?`n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        @([regex]::Matches($activeStep7, 'docker exec .*psql')).Count | Should -Be 1
        $step7 | Should -Not -Match 'psql .*-c ''UPDATE dmscs\."OpenIddictKey"'
        $step7 | Should -Not -Match 'docker cp'

        $assembled = [regex]::Match($step7, '(?ms)^\{\r?\n(?<body>.*?)^\} \| docker exec').Groups["body"].Value
        $assembled | Should -Not -BeNullOrEmpty
        $deactivateAt = $assembled.IndexOf('echo ''UPDATE dmscs."OpenIddictKey" SET "IsActive" = FALSE;''')
        $insertAt = $assembled.IndexOf('printf ''%s\n'' "$KEY_SQL"')
        $assertAt = $assembled.IndexOf("<<'KEY_ASSERT_SQL'")
        $deactivateAt | Should -BeGreaterThan -1
        $insertAt | Should -BeGreaterThan $deactivateAt -Because "the producer's key is deactivated before the new one is inserted"
        $assertAt | Should -BeGreaterThan $insertAt -Because "the assertion runs after the insert, inside the same transaction"

        $assertion = [regex]::Match($assembled, "(?ms)<<'KEY_ASSERT_SQL'\r?\n(?<sql>.*?)^KEY_ASSERT_SQL").Groups["sql"].Value
        $assertion | Should -Match 'SELECT COUNT\(\*\) INTO active FROM dmscs\."OpenIddictKey" WHERE "IsActive"'
        $assertion | Should -Match '(?s)IF active <> 1 THEN\s+RAISE EXCEPTION' -Because "an insert that succeeded beside a still-active producer key must roll back"

        # The key material does not outlive the step: KEY_SQL and IDK are unset on the success path and
        # inside every failure guard that follows the generator call.
        $afterGenerate = $step7.Substring($step7.IndexOf('KEY_SQL=$('))
        $guard = @([regex]::Matches($afterGenerate, '\{ [^\n]*; exit 1; \}'))
        $guard.Count | Should -Be 3 -Because "the generator, the INSERT check and the psql run are each guarded"
        foreach ($item in $guard) {
            $item.Value | Should -Match '^\{ unset KEY_SQL IDK; ' -Because "a failure path must not leave the key material in the shell: $($item.Value)"
        }
        $afterGenerate | Should -Match '(?m)^unset KEY_SQL IDK$' -Because "the success path must clear the key material too"

        # The token check stays, after the replacement, as the second proof rather than the only one,
        # and its failure text still points at this step.
        $tokenCheckAt = $script:recipe.IndexOf('T=$(CMS_TOKEN restore-admin edfi_admin_api/full_access) || \')
        $tokenCheckAt | Should -BeGreaterThan $script:recipe.IndexOf("# 8. REQUIRED")
        $script:recipe.Substring($tokenCheckAt, 300) | Should -Match 'step 7' -Because "the failure text must point at the key replacement"
    }

    It "guards the DMS-to-CMS client replacement so a failed delete or insert stops the recipe" {
        # The same class as step 7: setup-openiddict.ps1 inserts ON CONFLICT DO NOTHING, so a delete
        # that fails silently leaves the producer's secret hash in place, and only the token check
        # would notice. Both commands stop the recipe on failure.
        $step10 = [regex]::Match($script:recipe, '(?ms)^# 10\. REQUIRED: recreate the client DMS uses.*?(?=^# 11\. )').Value
        $step10 | Should -Not -BeNullOrEmpty
        $step10 | Should -Match '(?m)^docker exec -i dms-postgresql psql .* -v cid="\$CID" -f - <<''SQL'' \|\| \\\r?\n\s+\{ .*; exit 1; \}\r?\nDELETE FROM dmscs\."OpenIddictApplication" WHERE "ClientId" = :''cid'';\r?\nSQL$'
        $invocation = & $script:SetupOpenIddictInvocation
        $invocation | Should -Match '\|\| \\\n\s+\{ .*; exit 1; \}$' -Because "a failed setup-openiddict.ps1 must stop the recipe before the token check"
    }
    It "deletes and recreates the DMS-to-CMS client in the one database the running Configuration Service reads" {
        # The false pass: the DELETE ran against $DB while the insert was pointed at
        # ENV:DMS_CONFIG_DATABASE_NAME, which setup-openiddict.ps1 resolves on its own. With that
        # override set, the delete cleared one database and the insert skipped the producer's row in
        # the other, and only step 11 would have noticed.
        $step10 = [regex]::Match($script:recipe, '(?ms)^# 10\. REQUIRED: recreate the client DMS uses.*?(?=^# 11\. )').Value
        $active10 = (($step10 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $active10 | Should -Not -Match '(?m)^CMSDB=' -Because "step 10 reuses the database step 7 resolved rather than resolving one of its own"
        $delete = [regex]::Match($active10, '(?m)^docker exec -i dms-postgresql psql [^\n]* -v cid="\$CID" -f - <<''SQL'' \|\| \\$')
        $delete.Success | Should -BeTrue
        $delete.Value | Should -Match ' -d "\$CMSDB" '
        $delete.Value | Should -Not -Match ' -d "\$DB" ' -Because "the DELETE must not assume the CMS rows live in the restored database"
        $script:activeRecipe.IndexOf('CMSDB=$(') | Should -BeLessThan $script:activeRecipe.IndexOf($delete.Value) -Because "the name is resolved, in step 7, before it is used here"
        $invocation = & $script:SetupOpenIddictInvocation
        $invocation | Should -Match '-DbName "\$CMSDB"' -Because "the insert targets the same database, as the same literal"
        $invocation | Should -Not -Match 'ENV:DMS_CONFIG_DATABASE_NAME'
        $invocation | Should -Not -Match '-DbName "\$DB"'
    }

    It "resolves the CMS database once, in step 7, before the signing key is read or replaced" {
        # The false pass: step 10 resolved the database the running Configuration Service reads and
        # recreated the DMS-to-CMS client there, while step 7 had replaced the signing key in $DB. With
        # DMS_CONFIG_DATABASE_NAME set, the key went into the restored data store and CMS kept minting
        # tokens from the producer's key in its own database, and only the step 9 token check noticed.
        $step7 = [regex]::Match($script:recipe, '(?ms)^# 7\. REQUIRED: install your own OpenIddict signing key\..*?(?=^# 8\. )').Value
        $active7 = (($step7 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $active7 | Should -Match '(?m)^CMSCS=\$\(ENVOF ed-fi-api-config-service \| sed -n ''s/\^DatabaseSettings__DatabaseConnection=//p''\)$' -Because "the database is the one the running CMS was configured with"
        $active7 | Should -Match '(?m)^CMSDB=\$\(CMSCS="\$CMSCS" pwsh -NoProfile -Command ''$' -Because "the connection string carries the password and travels as that one process's environment"
        $active7 | Should -Match 'DbConnectionStringBuilder' -Because "the database keyword is read back by the rules Npgsql parses, not by a pattern over the text"
        $active7 | Should -Match '(?m)^unset CMSCS$'
        $guard = [regex]::Match($active7, '(?m)^test -n "\$CMSDB" \|\| \\\n\s+\{ [^\n]*; exit 1; \}$')
        $guard.Success | Should -BeTrue -Because "an empty database name must stop the step"
        $keyRun = [regex]::Match($active7, '(?m)^\} \| docker exec -i dms-postgresql psql [^\n]*$')
        $keyRun.Success | Should -BeTrue
        $keyRun.Value | Should -Match ' -d "\$CMSDB" ' -Because "CMS mints tokens from the dmscs.OpenIddictKey row of its own database"
        $guard.Index | Should -BeLessThan $keyRun.Index -Because "the name is proven non-empty before it is used"
        $guard.Index | Should -BeLessThan $active7.IndexOf('IDK=$(') -Because "the database is resolved before the key material is read, so its guard has no key to clear"
        $active7.IndexOf('ENVOF() {') | Should -BeGreaterThan -1 -Because "the helper the resolution reads the container with is defined here, at its first use"
        $active7.IndexOf('ENVOF() {') | Should -BeLessThan $active7.IndexOf('CMSCS=$(ENVOF')
        @([regex]::Matches($script:activeRecipe, '(?m)^CMSDB=')).Count | Should -Be 1 -Because "one resolution, which step 10 reuses"
    }

    It "targets the CMS database for every dmscs operation and the restored data store for every dms operation" {
        # The rule the two steps above follow, held as one assertion so a new dmscs or dms call site
        # cannot land on the other database: dmscs.* (OpenIddict) lives where CMS reads, $CMSDB; dms.*
        # lives in the restored data store, $DB. Steps 7 and 10 are the dmscs steps and name no other
        # database; step 8 rotates a dms row and names no other database.
        $step7 = [regex]::Match($script:recipe, '(?ms)^# 7\. REQUIRED: install your own OpenIddict signing key\..*?(?=^# 8\. )').Value
        $step8 = [regex]::Match($script:recipe, '(?ms)^# 8\. REQUIRED: rotate dms\.DataStoreIdentity\.SourceIdentity\..*?(?=^# 9\. )').Value
        $step10 = [regex]::Match($script:recipe, '(?ms)^# 10\. REQUIRED: recreate the client DMS uses.*?(?=^# 11\. )').Value
        $active7 = (($step7 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $active8 = (($step8 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $active10 = (($step10 -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
        $active8 | Should -Not -BeNullOrEmpty

        $dmscsLines = @(($script:activeRecipe -split "`n") | Where-Object { $_ -match 'dmscs' })
        $dmscsLines.Count | Should -BeGreaterThan 0
        foreach ($line in $dmscsLines) {
            ($active7.Contains($line) -or $active10.Contains($line)) | Should -BeTrue -Because "a dmscs operation outside steps 7 and 10 would target a database this rule does not cover: $line"
        }
        $active7 | Should -Not -Match '"\$DB"' -Because "the signing key belongs to the CMS database"
        $active10 | Should -Not -Match '"\$DB"' -Because "the DMS-to-CMS client belongs to the CMS database"
        foreach ($psql in @([regex]::Matches("$active7`n$active10", '(?m)^.*\bpsql\b.*$'))) {
            $psql.Value | Should -Match ' -d "\$CMSDB" ' -Because "every psql run in the dmscs steps targets the CMS database: $($psql.Value)"
        }

        $active8 | Should -Match '(?m)^NEW_SOURCE_ID=\$\(docker exec dms-postgresql psql -U "\$DBUSER" -d "\$DB" -v ON_ERROR_STOP=1 -tAc \\$' -Because "the source identity is a dms row of the restored data store"
        $active8 | Should -Match 'UPDATE dms\."DataStoreIdentity" SET "SourceIdentity" = gen_random_uuid\(\)'
        $active8 | Should -Not -Match 'CMSDB' -Because "moving the signing key to the CMS database must not move the data-store mutation with it"

        $inDmscsSteps = @([regex]::Matches($active7, '\$CMSDB\b')).Count + @([regex]::Matches($active10, '\$CMSDB\b')).Count
        @([regex]::Matches($script:activeRecipe, '\$CMSDB\b')).Count | Should -Be $inDmscsSteps -Because "no other step names the CMS database"
    }
}
