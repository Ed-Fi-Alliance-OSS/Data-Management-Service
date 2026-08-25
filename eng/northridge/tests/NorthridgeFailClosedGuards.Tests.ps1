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
#   Get-DmsResourceCount.ps1        one CSV passed as both sides of the reconciliation
#   Copy-NorthridgeDataForward.ps1  a dms base table on none of the classification lists; a target
#                                   used as its own source or reference; a measured checkpoint value
#                                   with no expected value
#   Add-NorthridgeGapDocument.ps1   a deferred read recorded with its mid-manifest status; two
#                                   documents sharing a label
#   README restore recipe           hard-coded 8080/8081 and unbounded health waits

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
            [pscustomobject]@{ ResourceName = "schools"; DocumentCount = 2 },
            [pscustomobject]@{ ResourceName = "students"; DocumentCount = 3 }
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
