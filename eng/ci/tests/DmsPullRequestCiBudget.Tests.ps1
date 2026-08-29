# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Guardrails for the CI-budget wiring in .github/workflows/on-dms-pullrequest.yml.
#
# The classification behind these decisions is exercised directly by DmsChangeCategories.Tests.ps1.
# What is left here is the wiring itself - which trigger types fire, which jobs consume which
# output, and whether the aggregate gate still reports - and that wiring has no runtime seam: it is
# declarative YAML evaluated by GitHub, so a mistake shows up as a lane that silently never runs
# behind a green required check. Scope is deliberately narrow: only the decisions DMS-1474 makes.
#
# No YAML parser is available in this lane, so (following DmsPullRequestMssqlWorkflow.Tests.ps1)
# named blocks are extracted by their two-space job key and invariants are asserted inside them.

# The nine jobs that each ran their own solution build before the shared build artifact landed.
# Declared at file scope rather than in BeforeAll because Pester binds -ForEach during discovery,
# which happens before any BeforeAll body has run.
$buildOutputConsumer = @(
    @{ JobName = 'run-unit-tests' }
    @{ JobName = 'run-e2e-tests' }
    @{ JobName = 'run-e2e-tests-ds61' }
    @{ JobName = 'run-e2e-tests-partition-sizing' }
    @{ JobName = 'run-e2e-tests-mssql' }
    @{ JobName = 'run-e2e-tests-mssql-ds61' }
    @{ JobName = 'run-instance-management-e2e-tests' }
    @{ JobName = 'run-instance-management-e2e-tests-mssql' }
    @{ JobName = 'build-and-start-dms' }
)

Describe "on-dms-pullrequest.yml CI budget wiring" {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/on-dms-pullrequest.yml")
        )
        $script:lines = (Get-Content -LiteralPath $script:workflowPath -Raw) -split "\r?\n"

        $script:buildScriptPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../build-dms.ps1")
        )

        function Get-JobBlock {
            # Text of a top-level job (two-space key) up to the next top-level job key or EOF.
            param([Parameter(Mandatory)] [string] $JobName)

            $startIndex = -1
            for ($i = 0; $i -lt $script:lines.Count; $i++) {
                if ($script:lines[$i] -match "^  $([regex]::Escape($JobName)):\s*$") {
                    $startIndex = $i
                    break
                }
            }
            if ($startIndex -lt 0) {
                return $null
            }

            $endIndex = $script:lines.Count
            for ($j = $startIndex + 1; $j -lt $script:lines.Count; $j++) {
                if ($script:lines[$j] -match '^  [A-Za-z0-9_-]+:\s*$') {
                    $endIndex = $j
                    break
                }
            }

            return ($script:lines[$startIndex..($endIndex - 1)] -join "`n")
        }

        function Get-JobIfCondition {
            # The job-level `if:` flattened to one line, so a folded scalar and an inline condition
            # are compared the same way. Job keys sit at two spaces, so the job's own `if:` is the
            # first four-space one in the block; a step's `if:` is indented further and never wins.
            param([Parameter(Mandatory)] [string] $JobName)

            $block = Get-JobBlock -JobName $JobName
            if ($null -eq $block) {
                throw "Job '$JobName' was not found in $script:workflowPath."
            }

            $blockLines = $block -split "`n"
            for ($i = 0; $i -lt $blockLines.Count; $i++) {
                if ($blockLines[$i] -notmatch '^    if:\s*(.*)$') {
                    continue
                }

                $inline = $Matches[1].Trim()
                if ($inline -notin @('>-', '>', '|', '|-')) {
                    return $inline
                }

                $continuation = @()
                for ($j = $i + 1; $j -lt $blockLines.Count; $j++) {
                    if ([string]::IsNullOrWhiteSpace($blockLines[$j])) {
                        continue
                    }
                    if ($blockLines[$j] -notmatch '^      \S') {
                        break
                    }
                    $continuation += $blockLines[$j].Trim()
                }

                return ($continuation -join ' ')
            }

            # A job with no condition at all. Distinct from a job whose condition is empty text,
            # and the correct answer for "is this job draft-gated?" either way.
            return ''
        }

        function Get-TriggerBlock {
            # Everything from the `on:` key to the `jobs:` key.
            $startIndex = [array]::FindIndex($script:lines, [Predicate[string]] { $args[0] -match '^on:\s*$' })
            $endIndex = [array]::FindIndex($script:lines, [Predicate[string]] { $args[0] -match '^jobs:\s*$' })

            if ($startIndex -lt 0 -or $endIndex -lt 0) {
                throw "Could not locate the on:/jobs: boundary in $script:workflowPath."
            }

            return ($script:lines[$startIndex..($endIndex - 1)] -join "`n")
        }

        function Get-PullRequestTriggerType {
            # The list items under `pull_request:` -> `types:`.
            $triggerLines = (Get-TriggerBlock) -split "`n"
            $types = @()
            $inTypes = $false

            for ($i = 0; $i -lt $triggerLines.Count; $i++) {
                if ($triggerLines[$i] -match '^    types:\s*$') {
                    $inTypes = $true
                    continue
                }

                if (-not $inTypes) {
                    continue
                }

                if ($triggerLines[$i] -match '^      - (\S+)\s*$') {
                    $types += $Matches[1]
                    continue
                }

                break
            }

            return $types
        }

        $script:draftGatedJob = @(
            'build-ci-docker-images'
            'build-integration-test-assemblies'
            'verify-dms-packages'
            'run-cli-integration-tests'
            'test-timing-summary'
        )

        function Get-JobNeed {
            # The job-level `needs:`, whether written inline (`needs: a`, `needs: [a, b]`) or as a
            # block sequence.
            param([Parameter(Mandatory)] [string] $JobName)

            $block = Get-JobBlock -JobName $JobName
            if ($null -eq $block) {
                throw "Job '$JobName' was not found in $script:workflowPath."
            }

            $blockLines = $block -split "`n"
            for ($i = 0; $i -lt $blockLines.Count; $i++) {
                if ($blockLines[$i] -notmatch '^    needs:\s*(.*)$') {
                    continue
                }

                $inline = $Matches[1].Trim()
                if (-not [string]::IsNullOrWhiteSpace($inline)) {
                    return @(
                        ($inline.Trim('[', ']') -split ',') |
                            ForEach-Object { $_.Trim() } |
                            Where-Object { $_ }
                    )
                }

                $names = @()
                for ($j = $i + 1; $j -lt $blockLines.Count; $j++) {
                    if ([string]::IsNullOrWhiteSpace($blockLines[$j])) {
                        continue
                    }
                    if ($blockLines[$j] -match '^      \[?\s*$') {
                        continue
                    }
                    if ($blockLines[$j] -match '^      -?\s*([A-Za-z0-9_-]+),?\s*$') {
                        $names += $Matches[1]
                        continue
                    }
                    break
                }

                return $names
            }

            return @()
        }

        $script:promotedLane = @(
            @{ JobName = 'run-backend-mssql-integration-tests'; Category = 'backend_mssql_relevant' }
            @{ JobName = 'run-dms-api-mssql-integration-tests'; Category = 'dms_api_relevant' }
            @{ JobName = 'run-dms-api-postgresql-integration-tests'; Category = 'dms_api_relevant' }
            @{ JobName = 'run-backend-cdc-integration-tests'; Category = 'cdc_relevant' }
            @{ JobName = 'run-schematools-postgresql-integration-tests'; Category = 'schematools_relevant' }
            @{ JobName = 'run-schematools-mssql-integration-tests'; Category = 'schematools_relevant' }
        )

        $script:notDraftGatedJob = @(
            'detect-fresh-build-changes'
            'scan-actions-bidi'
            'run-bootstrap-pester-tests'
            'verify-lock-files'
            'run-unit-tests'
            'dms-ci-gate'
        )

        function Get-StepChunk {
            # The job's steps as ordered text chunks, split on the six-space `- ` step markers. Order
            # matters here as much as presence: a download step that lands after the step consuming
            # the build output is present and still useless.
            param([Parameter(Mandatory)] [string] $JobName)

            $block = Get-JobBlock -JobName $JobName
            if ($null -eq $block) {
                throw "Job '$JobName' was not found in $script:workflowPath."
            }

            $blockLines = $block -split "`n"
            $stepsIndex = -1
            for ($i = 0; $i -lt $blockLines.Count; $i++) {
                if ($blockLines[$i] -match '^    steps:\s*$') {
                    $stepsIndex = $i
                    break
                }
            }

            if ($stepsIndex -lt 0) {
                throw "Job '$JobName' has no steps: block in $script:workflowPath."
            }

            $chunks = [System.Collections.Generic.List[string]]::new()
            $current = $null

            for ($i = $stepsIndex + 1; $i -lt $blockLines.Count; $i++) {
                if ($blockLines[$i] -match '^      - ') {
                    if ($null -ne $current) {
                        $chunks.Add($current -join "`n")
                    }
                    $current = @($blockLines[$i])
                    continue
                }

                if ($null -ne $current) {
                    $current += $blockLines[$i]
                }
            }

            if ($null -ne $current) {
                $chunks.Add($current -join "`n")
            }

            return $chunks.ToArray()
        }
    }

    Context "Pull request trigger types" {
        It "declares exactly the three default types plus ready_for_review" {
            # Declaring types replaces the default set outright. Dropping synchronize would stop CI
            # running on new pushes; omitting ready_for_review would leave a draft-gated pull request
            # permanently unvalidated once marked ready.
            @(Get-PullRequestTriggerType) | Sort-Object |
                Should -Be @('opened', 'ready_for_review', 'reopened', 'synchronize')
        }

        It "still restricts the trigger to pull requests targeting main" {
            (Get-TriggerBlock) | Should -Match '(?m)^      - main\s*$'
        }
    }

    Context "The detector publishes the draft flag" {
        It "declares draft as a job output" {
            Get-JobBlock 'detect-fresh-build-changes' |
                Should -Match '(?m)^      draft:\s*\$\{\{\s*steps\.detect\.outputs\.draft\s*\}\}\s*$'
        }

        It "still declares the two pre-existing outputs" {
            $block = Get-JobBlock 'detect-fresh-build-changes'

            $block | Should -Match '(?m)^      fresh_build_required:\s*\$\{\{\s*steps\.detect\.outputs\.fresh_build_required\s*\}\}\s*$'
            $block | Should -Match '(?m)^      dms_relevant:\s*\$\{\{\s*steps\.detect\.outputs\.dms_relevant\s*\}\}\s*$'
        }
    }

    Context "Path-promoted integration lanes" {
        It "<JobName> gates on <Category>" -ForEach @(
            @{ JobName = 'run-backend-mssql-integration-tests'; Category = 'backend_mssql_relevant' }
            @{ JobName = 'run-dms-api-mssql-integration-tests'; Category = 'dms_api_relevant' }
            @{ JobName = 'run-dms-api-postgresql-integration-tests'; Category = 'dms_api_relevant' }
            @{ JobName = 'run-backend-cdc-integration-tests'; Category = 'cdc_relevant' }
            @{ JobName = 'run-schematools-postgresql-integration-tests'; Category = 'schematools_relevant' }
            @{ JobName = 'run-schematools-mssql-integration-tests'; Category = 'schematools_relevant' }
        ) {
            Get-JobIfCondition -JobName $JobName |
                Should -Match "needs\.detect-fresh-build-changes\.outputs\.$Category == 'true'"
        }

        It "<JobName> still runs the full suite for non-pull_request events" -ForEach @(
            @{ JobName = 'run-backend-mssql-integration-tests' }
            @{ JobName = 'run-dms-api-mssql-integration-tests' }
            @{ JobName = 'run-dms-api-postgresql-integration-tests' }
            @{ JobName = 'run-backend-cdc-integration-tests' }
            @{ JobName = 'run-schematools-postgresql-integration-tests' }
            @{ JobName = 'run-schematools-mssql-integration-tests' }
        ) {
            Get-JobIfCondition -JobName $JobName | Should -Match "github\.event_name != 'pull_request'"
        }

        It "<JobName> needs both the detector and the assembly build" -ForEach @(
            @{ JobName = 'run-backend-mssql-integration-tests' }
            @{ JobName = 'run-dms-api-mssql-integration-tests' }
            @{ JobName = 'run-dms-api-postgresql-integration-tests' }
            @{ JobName = 'run-backend-cdc-integration-tests' }
            @{ JobName = 'run-schematools-postgresql-integration-tests' }
            @{ JobName = 'run-schematools-mssql-integration-tests' }
        ) {
            # The detector supplies the category output; without it in needs the expression reads
            # empty and the lane never runs. build-integration-test-assemblies supplies the test
            # assemblies and the inherited draft skip.
            $needs = Get-JobNeed -JobName $JobName

            $needs | Should -Contain 'detect-fresh-build-changes'
            $needs | Should -Contain 'build-integration-test-assemblies'
        }

        It "leaves Backend PostgreSQL Integration unpromoted" {
            # Deliberately out of scope: PostgreSQL is the default engine, so this is the lane most
            # likely to catch a shared-backend regression at pull request time.
            Get-JobIfCondition -JobName 'run-backend-postgresql-integration-tests' |
                Should -Not -Match 'outputs\.(backend_mssql|dms_api|schematools|cdc)_relevant'
        }

        It "leaves the other integration lanes unpromoted" -ForEach @(
            @{ JobName = 'run-schematools-cli-integration-tests' }
            @{ JobName = 'run-cli-integration-tests' }
        ) {
            Get-JobIfCondition -JobName $JobName |
                Should -Not -Match 'outputs\.(backend_mssql|dms_api|schematools|cdc)_relevant'
        }

        It "declares every category output the workflow consumes" {
            # An output referenced but not declared evaluates to the empty string, never equals
            # 'true', and silently skips its lane on every pull request forever. This is the one
            # mistake in this design that no test of the classifier could catch.
            $consumed = @(
                [regex]::Matches(
                    ($script:lines -join "`n"),
                    'needs\.detect-fresh-build-changes\.outputs\.([A-Za-z0-9_]+)'
                ) | ForEach-Object { $_.Groups[1].Value }
            ) | Sort-Object -Unique

            $declared = @(
                ((Get-JobBlock 'detect-fresh-build-changes') -split "`n") |
                    Where-Object { $_ -match '^      ([A-Za-z0-9_]+):\s*\$\{\{' } |
                    ForEach-Object { [regex]::Match($_, '^      ([A-Za-z0-9_]+):').Groups[1].Value }
            ) | Sort-Object -Unique

            $consumed.Count | Should -BeGreaterThan 0

            foreach ($name in $consumed) {
                $declared | Should -Contain $name
            }
        }
    }

    Context "Draft pull requests skip the expensive jobs" {
        It "<JobName> is draft-gated" -ForEach @(
            @{ JobName = 'build-ci-docker-images' }
            @{ JobName = 'build-integration-test-assemblies' }
            @{ JobName = 'verify-dms-packages' }
            @{ JobName = 'run-cli-integration-tests' }
            @{ JobName = 'test-timing-summary' }
        ) {
            Get-JobIfCondition -JobName $JobName |
                Should -Match "needs\.detect-fresh-build-changes\.outputs\.draft != 'true'"
        }

        It "<JobName> keeps its DMS-relevance gate as well" -ForEach @(
            @{ JobName = 'build-ci-docker-images' }
            @{ JobName = 'build-integration-test-assemblies' }
            @{ JobName = 'verify-dms-packages' }
            @{ JobName = 'run-cli-integration-tests' }
            @{ JobName = 'test-timing-summary' }
        ) {
            # Draft gating narrows; it must not replace the docs-only skip that already existed.
            Get-JobIfCondition -JobName $JobName |
                Should -Match "needs\.detect-fresh-build-changes\.outputs\.dms_relevant == 'true'"
        }

        It "<JobName> still runs the full suite for non-pull_request events" -ForEach @(
            @{ JobName = 'build-ci-docker-images' }
            @{ JobName = 'build-integration-test-assemblies' }
            @{ JobName = 'verify-dms-packages' }
            @{ JobName = 'run-cli-integration-tests' }
            @{ JobName = 'test-timing-summary' }
        ) {
            # The merge queue is the recovery path for everything this ticket skips at PR time, so
            # no gate may ever apply to merge_group or workflow_dispatch.
            Get-JobIfCondition -JobName $JobName |
                Should -Match "github\.event_name != 'pull_request'"
        }
    }

    Context "Draft pull requests keep fast feedback and a reporting gate" {
        It "<JobName> is not draft-gated" -ForEach @(
            @{ JobName = 'detect-fresh-build-changes' }
            @{ JobName = 'scan-actions-bidi' }
            @{ JobName = 'run-bootstrap-pester-tests' }
            @{ JobName = 'verify-lock-files' }
            @{ JobName = 'run-unit-tests' }
            @{ JobName = 'dms-ci-gate' }
        ) {
            Get-JobIfCondition -JobName $JobName | Should -Not -Match 'outputs\.draft'
        }
    }

    Context "The aggregate gate reports on every pull request" {
        It "runs unconditionally" {
            # dms-ci-gate is the merge queue's required check. It has to run and report even when
            # every dependency skipped, which is exactly the draft case.
            Get-JobIfCondition -JobName 'dms-ci-gate' | Should -Be 'always()'
        }

        It "still counts an intentional skip as a pass" {
            # This is what makes draft gating safe: skipped dependencies must not fail the gate.
            Get-JobBlock 'dms-ci-gate' | Should -Match "\`$result -ne 'success' -and \`$result -ne 'skipped'"
        }

        It "depends only on jobs that exist" {
            # A dependency naming a job that is not defined makes the whole workflow invalid, and a
            # renamed job silently dropping out of the gate would make the gate green while its
            # lane never ran.
            $gateLines = (Get-JobBlock 'dms-ci-gate') -split "`n"
            $needs = @()
            $inNeeds = $false

            foreach ($line in $gateLines) {
                if ($line -match '^    needs:\s*$') {
                    $inNeeds = $true
                    continue
                }
                if (-not $inNeeds) {
                    continue
                }
                if ($line -match '^      - (\S+)\s*$') {
                    $needs += $Matches[1]
                    continue
                }
                break
            }

            $needs.Count | Should -BeGreaterThan 0

            $definedJob = @(
                $script:lines |
                    Where-Object { $_ -match '^  ([A-Za-z0-9_-]+):\s*$' } |
                    ForEach-Object { ($_ -replace '^\s+', '') -replace ':\s*$', '' }
            )

            foreach ($dependency in $needs) {
                $definedJob | Should -Contain $dependency
            }
        }
    }

    Context "One shared solution build feeds every build consumer" {
        It "build-dms-solution exists and uploads the dms-build-output artifact" {
            $block = Get-JobBlock -JobName 'build-dms-solution'

            $block | Should -Not -BeNullOrEmpty
            $block | Should -Match 'actions/upload-artifact'
            $block | Should -Match 'name: dms-build-output'
            # An empty artifact has to fail the producer rather than fail nine consumers later with
            # a confusing missing-assembly error.
            $block | Should -Match 'if-no-files-found: error'
        }

        It "the producer ships a tar archive rather than a directory tree" {
            # The artifact carries files that have to arrive executable - Playwright's
            # .playwright/node/*/node, which the driver spawns, and the apphost beside each
            # executable project. upload-artifact zips a directory path, and the zip handoff drops
            # Unix mode bits: everything lands 755/644. Nothing in a --no-build consumer re-applies
            # them, so a directory upload produces an artifact that downloads cleanly and then fails
            # at run time with permission denied. tar records the modes and carries them through.
            $block = Get-JobBlock -JobName 'build-dms-solution'

            # -C <staged root> . archives the directory's contents, dotfiles included, so the
            # .playwright tree needs no separate opt-in the way include-hidden-files was.
            $block | Should -Match 'tar -czf [^\r\n]*dms-build-output\.tar\.gz -C [^\r\n]*dms-build-output \.'
            $block | Should -Match 'path: [^\r\n]*dms-build-output\.tar\.gz'
            # Re-compressing an already gzipped archive costs minutes on a multi-gigabyte tree and
            # buys nothing.
            $block | Should -Match 'compression-level: 0'
        }

        It "build-dms-solution runs on DMS relevance and is not draft-gated" {
            # A draft-gated producer would skip run-unit-tests through the dependency, which is the
            # opposite of keeping fast feedback on draft pull requests.
            $condition = Get-JobIfCondition -JobName 'build-dms-solution'

            $condition | Should -Match 'dms_relevant'
            $condition | Should -Not -Match 'draft'
        }

        It "<JobName> depends on build-dms-solution" -ForEach $buildOutputConsumer {
            Get-JobNeed -JobName $JobName | Should -Contain 'build-dms-solution'
        }

        It "<JobName> downloads dms-build-output" -ForEach $buildOutputConsumer {
            $block = Get-JobBlock -JobName $JobName

            $block | Should -Match 'actions/download-artifact'
            $block | Should -Match 'name: dms-build-output'
        }

        It "<JobName> no longer runs its own solution build" -ForEach $buildOutputConsumer {
            # Anchored on the run line rather than the step name, so renaming a step cannot hide a
            # build that is still happening.
            $block = Get-JobBlock -JobName $JobName

            $block | Should -Not -Match 'build-dms\.ps1 Build'
        }

        It "<JobName> compiles nothing of its own" -ForEach $buildOutputConsumer {
            # Wider than the solution-build check above, because a consumer can recompile without
            # ever naming build-dms.ps1: a bare `dotnet test <project>` builds that project and its
            # whole ProjectReference graph. The producer stages bin only, so such a step has no
            # intermediate output to reuse and pays close to a full compile - exactly the redundancy
            # the shared artifact exists to remove. Scoped to the consumer set: the artifact
            # producers and the jobs that build only their own matrix project are supposed to build.
            $chunks = [string[]] (Get-StepChunk -JobName $JobName)

            $compiling = @(
                $chunks | Where-Object {
                    $_ -match 'dotnet (test|build|run|publish)\b' -and $_ -notmatch '--no-build'
                }
            )

            $compiling | Should -BeNullOrEmpty
        }

        It "<JobName> extracts the archive with permissions preserved" -ForEach $buildOutputConsumer {
            # -p rather than relying on the runner's umask: umask 022 happens to leave the execute
            # bit alone, but that is a property of the runner image, not of the handoff, and this
            # artifact's correctness rests on the execute bit surviving.
            $block = Get-JobBlock -JobName $JobName

            $block | Should -Match 'tar -xzpf [^\r\n]*dms-build-output\.tar\.gz'
        }

        It "<JobName> extracts the artifact before the first step needing compiled output" -ForEach $buildOutputConsumer {
            # Ordering is asserted against the extract, not the download: an artifact that has been
            # downloaded but not unpacked is exactly as useless as one that never arrived.
            $chunks = [string[]] (Get-StepChunk -JobName $JobName)

            $downloadIndex = [array]::FindIndex(
                $chunks, [Predicate[string]] { $args[0] -match 'name: dms-build-output' }
            )
            $extractIndex = [array]::FindIndex(
                $chunks, [Predicate[string]] { $args[0] -match 'tar -xzpf' }
            )
            $firstUseIndex = [array]::FindIndex(
                $chunks,
                [Predicate[string]] {
                    $args[0] -match 'build-dms\.ps1 (UnitTest|E2ETest|InstanceE2ETest)' -or
                    $args[0] -match 'preflight-dms-schema-compile\.ps1'
                }
            )

            $downloadIndex | Should -BeGreaterThan -1
            $extractIndex | Should -BeGreaterThan -1
            $firstUseIndex | Should -BeGreaterThan -1
            $downloadIndex | Should -BeLessThan $extractIndex
            $extractIndex | Should -BeLessThan $firstUseIndex
        }

        It "only the producer runs a solution build, with no inert -IdentityProvider" {
            # -IdentityProvider never affected Build; it was carried along by copy-paste and is
            # removed with the deleted steps. The producer is the one remaining Build call site.
            $buildLines = @($script:lines | Where-Object { $_ -match 'build-dms\.ps1 Build\b' })

            $buildLines.Count | Should -Be 1
            $buildLines[0] | Should -Not -Match '-IdentityProvider'
            (Get-JobBlock -JobName 'build-dms-solution') | Should -Match 'build-dms\.ps1 Build'
        }

        It "the aggregate gate waits on the producer" {
            Get-JobNeed -JobName 'dms-ci-gate' | Should -Contain 'build-dms-solution'
        }

        It "build-integration-test-assemblies keeps its own artifact contract" {
            # Deliberately not re-staged from dms-build-output: that would couple the six fast
            # integration lanes to a much larger download.
            $block = Get-JobBlock -JobName 'build-integration-test-assemblies'

            $block | Should -Match 'name: dms-integration-test-assemblies'
            $block | Should -Not -Match 'dms-build-output'
        }

        It "no build-dms.ps1 test path rebuilds what the artifact already provides" {
            # The workflow guard above sees only commands written in the workflow. Consumers reach
            # their tests through build-dms.ps1, so a test path that omits --no-build there
            # recompiles just as surely and is invisible to a YAML scan. Asserted per function so a
            # fourth test path added later cannot inherit a pass from its siblings.
            $buildScript = Get-Content -LiteralPath $script:buildScriptPath -Raw
            $functionBlock = [regex]::Split($buildScript, '(?m)^function ') |
                Select-Object -Skip 1 |
                # Anchored at the start of a line so a comment or a Write-Output that merely names
                # the command is not mistaken for one, which the parameter documentation above the
                # functions and the two explanatory comments inside them would otherwise trip.
                Where-Object { $_ -match '(?m)^\s*dotnet test\b' }

            $functionBlock | Should -Not -BeNullOrEmpty

            foreach ($block in $functionBlock) {
                $name = ($block -split '\s', 2)[0]

                # The invocation and the flags can be separated by an argument array, so the
                # function body as a whole is the unit, not the command line.
                $block | Should -Match '--no-build' -Because "$name invokes dotnet test"
                $block | Should -Match '--no-restore' -Because "$name invokes dotnet test"
            }
        }
    }

    Context "Package verification is deliberately not a shared-build consumer" {
        # It is the one job DMS-1474 counts among the redundant solution builds that does not become
        # a consumer, so the decision is pinned here rather than left as an omission from the
        # consumer list. The last two assertions carry the reason, so that if the ground it rests on
        # moves, this fails and forces the exclusion to be decided again instead of inherited.

        It "runs BuildAndPublish rather than the shared Build target" {
            $block = Get-JobBlock -JobName 'verify-dms-packages'

            $block | Should -Match 'build-dms\.ps1 BuildAndPublish'
            $block | Should -Not -Match 'build-dms\.ps1 Build\b'
        }

        It "neither needs nor downloads the shared build output" {
            Get-JobNeed -JobName 'verify-dms-packages' | Should -Not -Contain 'build-dms-solution'
            (Get-JobBlock -JobName 'verify-dms-packages') | Should -Not -Match 'name: dms-build-output'
        }

        It "states in the workflow why it is excluded" {
            $block = Get-JobBlock -JobName 'verify-dms-packages'

            $block | Should -Match 'dms-build-output'
            $block | Should -Match 'version-stamp'
        }

        It "BuildAndPublish still stamps the version before it compiles" {
            # The shared producer runs the plain Build target, so its assemblies carry the version in
            # the tracked props file. This job's packages must carry the release version instead, and
            # only the stamping step ahead of the compile puts it there.
            $buildScript = Get-Content -LiteralPath $script:buildScriptPath -Raw

            $buildScript | Should -Match 'BuildAndPublish \{[^}]*Invoke-SetAssemblyInfo[^}]*Invoke-Build'
        }

        It "version stamping rewrites the props file that governs the shared build" {
            $buildScript = Get-Content -LiteralPath $script:buildScriptPath -Raw

            $buildScript | Should -Match 'function SetDMSAssemblyInfo'
            $buildScript | Should -Match 'Directory\.Build\.props'
            $buildScript | Should -Match '<VersionPrefix>'
        }
    }
}
