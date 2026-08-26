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
#                                   resources of one name collapsed to one key
#   Copy-NorthridgeDataForward.ps1  a dms base table on none of the classification lists; a target
#                                   used as its own source or reference; a measured checkpoint value
#                                   with no expected value; the descriptor load rewriting data rows
#                                   through a host string that also carried pg_restore's diagnostics
#   Add-NorthridgeGapDocument.ps1   a deferred read recorded with its mid-manifest status; two
#                                   documents sharing a label; a date-time field thrown on instead
#                                   of compared
#   README restore recipe           hard-coded 8080/8081 and unbounded health waits; "start again
#                                   from step 4" after a failed restore, which set the partial
#                                   restore aside as the reference over the intact deployment

BeforeAll {
    $script:northridgeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $script:northridgeRoot "../.."))
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
        foreach ($name in @("ProvisioningOwnedTable", "DmsDataTable", "DmsDerivedTable")) {
            . ([scriptblock]::Create((Get-ScriptAssignmentText -ScriptPath $script:copyScript -VariablePath "script:$name")))
        }
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:copyScript -FunctionName "Get-DmsTableClassificationFailure")))
        $script:knownDmsTable = @(@($script:ProvisioningOwnedTable) + @($script:DmsDataTable) + @($script:DmsDerivedTable) | ForEach-Object { "dms.$_" })
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

    It "reads the port the compose files publish on the host, from the override they publish it from" {
        # The recipe reads ASPNETCORE_HTTP_PORTS inside each container. That is the host port only
        # because the compose files set it from the same variable they publish on 127.0.0.1; if either
        # file stops doing that, the recipe's read is no longer the host port and this fails first.
        foreach ($case in @(
                @{ File = "local-config.yml"; Variable = "DMS_CONFIG_ASPNETCORE_HTTP_PORTS" },
                @{ File = "local-dms.yml"; Variable = "DMS_HTTP_PORTS" }
            )) {
            $compose = Get-Content -Raw -LiteralPath (Join-Path $script:repoRoot "eng/docker-compose/$($case.File)")
            $environment = [regex]::Match($compose, '(?m)^\s*ASPNETCORE_HTTP_PORTS:\s*\$\{(?<var>[A-Z_]+)')
            $environment.Success | Should -BeTrue -Because "$($case.File) must set ASPNETCORE_HTTP_PORTS from an override"
            $environment.Groups["var"].Value | Should -Be $case.Variable
            $publish = [regex]::Match($compose, '(?m)^\s*-\s*"127\.0\.0\.1:\$\{(?<host>[A-Z_]+)[^}]*\}:\$\{(?<container>[A-Z_]+)[^}]*\}"')
            $publish.Success | Should -BeTrue -Because "$($case.File) must publish the port on 127.0.0.1 from an override"
            $publish.Groups["host"].Value | Should -Be $case.Variable
            $publish.Groups["container"].Value | Should -Be $case.Variable
        }
        $script:recipe | Should -Match 'DMS_CONFIG_ASPNETCORE_HTTP_PORTS and DMS_HTTP_PORTS' -Because "the recipe must say which overrides it honours"
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
        $script:stampRow = @(
            "dms.Document|3|1|9|1|9|a|b|a|b|a|b",
            "edfi.School|2|1|9|a|b",
            "edfi.Student|5|1|9|a|b"
        )
    }

    It "maps one row per table, keyed ordinally, and tolerates psql's trailing empty element" {
        $map = ConvertTo-StampDistributionMap -Row ($script:stampRow + "") -ExpectedTable $script:stampTable
        $map.Count | Should -Be 3
        $map.Comparer | Should -Be ([System.StringComparer]::Ordinal)
        $map["edfi.Student"] | Should -Be "5|1|9|a|b"
        $map["dms.Document"] | Should -Be "3|1|9|1|9|a|b|a|b|a|b"
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
        . ([scriptblock]::Create((Get-ScriptAssignmentText -ScriptPath $script:copyScript -VariablePath "script:DescriptorRedirectScript")))
        # The rewrite is a POSIX shell script the copy tool feeds to `sh -s` inside the container. It is
        # run here with the host's sh when there is one (the pull-request lane runs on ubuntu; Git for
        # Windows supplies one too) and skipped otherwise; the structural case below runs everywhere.
        $script:posixShell = Get-Command sh -ErrorAction SilentlyContinue

        function script:Invoke-DescriptorRedirect {
            param([Parameter(Mandatory)] [string] $Content)
            $in = Join-Path $TestDrive ("descriptor-" + [guid]::NewGuid().ToString("N") + ".sql")
            $out = "$in.staging"
            [System.IO.File]::WriteAllText($in, $Content, [System.Text.UTF8Encoding]::new($false))
            $output = $script:DescriptorRedirectScript | & $script:posixShell.Source -s ($in -replace '\\', '/') ($out -replace '\\', '/') "northridge_staging" 2>&1
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
        $script:copyText | Should -Match '\$script:DescriptorRedirectScript \| docker exec -i \$Container sh -s `\r?\n\s+\$containerDescriptorSqlPath \$containerStagingSqlPath \$script:StagingSchema 2>&1'
        $script:copyText | Should -Match 'psql -U \$PostgresUser -d \$TargetDatabase `\r?\n\s+-v ON_ERROR_STOP=1 --quiet -f \$containerStagingSqlPath 2>&1' -Because "psql reads the rewritten file in the container"
        $script:copyText | Should -Match 'rm -f \$containerDumpPath \$containerListPath `\r?\n\s+\$containerDescriptorSqlPath \$containerStagingSqlPath' -Because "both emitted files are removed with the dump"
        $script:copyText.IndexOf('$redirectOutput = $script:DescriptorRedirectScript') | Should -BeGreaterThan $script:copyText.IndexOf('-Description "pg_restore of dms.Descriptor to text"') -Because "the diagnostics scan runs before the rewrite"
        $script:DescriptorRedirectScript | Should -Match '(?m)^set -eu$'
        $script:DescriptorRedirectScript | Should -Match 'header=''\^COPY dms\\\."Descriptor" \(''' -Because "the rewrite is anchored to the start of the COPY header line"
        $script:DescriptorRedirectScript | Should -Match 'if \[ "\$count" != 1 \]' -Because "exactly one header may be rewritten"
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
        $script:activeHelper | Should -Match 'resume at step 5' -Because "the helper must say where to resume"
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
        $guard = [regex]::Match($script:step5to6, '(?m)^test -z "\$_ref_exists" \|\| \\\r?\n\s+\{ echo "\$REF already exists[^\n]*Use the \\"Recovery after a failed restore\\" block below if you mean to redo the restore, then resume at step 5\.[^\n]*Do NOT drop \$REF[^\n]*; exit 1; \}$')
        $guard.Success | Should -BeTrue -Because "an existing reference is the deployment an earlier attempt set aside, and exit 1 ends the shell that defined the helper, so the guard must send the operator to the paste-alone Recovery block"
        $guard.Index | Should -BeGreaterThan $script:step5to6.IndexOf('RECOVER_FROM_REF() {') -Because "the guard belongs to the step 5 preflight that follows the helper definition"
        $guard.Index | Should -BeLessThan $script:step5to6.IndexOf("SELECT format('ALTER DATABASE %I RENAME TO %I', :'db', :'ref') \gexec") -Because "the guard runs before the rename that would collide"
        $guard.Value | Should -Not -Match 'RECOVER_FROM_REF; exit' -Because "a reference left behind is refused, not recovered over: the target may be a finished restore the operator wants"
        $guard.Value | Should -Not -Match 'Run RECOVER_FROM_REF' -Because "the in-shell helper is gone once exit 1 ends the shell, so telling the operator to run it there is not actionable"
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
        # It is the one guard in the range that does not recover, so wherever the prose describes the
        # range it must name 5b as the exception rather than claim that every guard recovers.
        $step5b = [regex]::Match($script:activeStep5to6, '(?m)^[^\n]*step 5b failed[^\n]*$').Value
        $step5b | Should -Match 're-run step 5b'
        $step5b | Should -Match 'RECOVER_FROM_REF \(here, or the Recovery block after the recipe from a fresh shell\)'
        $step5b | Should -Not -Match 'RECOVER_FROM_REF; exit'
        $recoveryProse = [regex]::Match($script:step5to6, '(?ms)^#\s+Recovery\. .*?(?=^RECOVER_FROM_REF\(\) \{)').Value
        $recoveryProse | Should -Match 'RECOVER_FROM_REF itself before it stops -- all but one: the 5b apply' -Because "the step 5 prose describes the recovered range and must name its one exception"
        $recoverySection = [regex]::Match($script:readme, '(?ms)^### Recovery after a failed restore\r?\n(?<prose>.*?)^```shell').Groups["prose"].Value
        $recoverySection | Should -Match 'all but the 5b apply' -Because "the Recovery section describes the recovered range and must name its one exception"
        $repairSql = [regex]::Match($script:recipe, "(?ms)<<'REPAIR_SQL'[^\r\n]*\r?\n(?<sql>.*?)^REPAIR_SQL\s*$").Groups["sql"].Value
        $repairSql | Should -Not -BeNullOrEmpty
        $repairSql | Should -Not -Match 'start (again|over) from step 4' -Because "a cluster without the role was never deployed to; the way back is a wipe, not step 4"
        $repairSql | Should -Match 'bootstrap-local-dms\.ps1 -d -v[^\n]*start over from step 3'
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
        $script:readme.Substring($block[1].Index + $block[1].Length, 400) | Should -Match 'resume at step 5' -Because "the section must say where the next attempt starts"
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
