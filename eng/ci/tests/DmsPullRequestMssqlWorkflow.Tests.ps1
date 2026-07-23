# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Structural guardrails for the SQL Server E2E lanes added to .github/workflows/on-dms-pullrequest.yml.
# A workflow is declarative wiring that no invoked-script test can cover: these assertions fail fast if a
# future edit drops the engine flag, the exact scenario filter, the always-run engine-correct teardown, the
# sanitize-before-publish ordering, or the gate/summary/needs wiring for the MSSQL lanes. No YAML parser is
# available in this lane, so (following eng/DatabaseTemplates/tests/Template-WorkflowInputs.Tests.ps1) each
# named job block is extracted by its two-space job key and invariants are asserted inside that block. This
# targeted regex fallback is scoped to this one workflow file; PowerShell behavior stays covered by the
# invoked E2EEngineForwarding / E2ETeardownSafety / InstanceE2EForwarding / Sanitize-E2EArtifacts specs.

Describe "on-dms-pullrequest.yml SQL Server E2E lane guardrails" {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/on-dms-pullrequest.yml")
        )
        $script:content = Get-Content -LiteralPath $script:workflowPath -Raw
        $script:lines = $script:content -split "\r?\n"

        function Get-JobBlock {
            # Returns the text of a top-level job (two-space key) up to the next top-level job key or EOF.
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

        function Get-StepChunk {
            # Splits a job block into per-step chunks (each begins with a six-space "- name:" line). The
            # leading chunk (job header before the first step) is dropped.
            param([Parameter(Mandatory)] [string] $Block)

            $chunks = [regex]::Split($Block, "(?m)(?=^      - name:)")
            return @($chunks | Where-Object { $_ -match "(?m)^      - name:" })
        }

        function Get-RunCommandLine {
            # Returns the single line inside a block that invokes the given public build-dms.ps1 command.
            param(
                [Parameter(Mandatory)] [string] $Block,
                [Parameter(Mandatory)] [string] $Command
            )

            return (
                ($Block -split "\r?\n") |
                    Where-Object { $_ -match "build-dms\.ps1 $([regex]::Escape($Command))\b" } |
                    Select-Object -First 1
            )
        }

        $script:standard = Get-JobBlock 'run-e2e-tests-mssql'
        $script:ds61 = Get-JobBlock 'run-e2e-tests-mssql-ds61'
        $script:instance = Get-JobBlock 'run-instance-management-e2e-tests-mssql'
        $script:e2eSummary = Get-JobBlock 'e2e-summary'
        $script:timingSummary = Get-JobBlock 'test-timing-summary'
        $script:eventFile = Get-JobBlock 'event_file'
        $script:gate = Get-JobBlock 'dms-ci-gate'
    }

    Context "run-e2e-tests-mssql (standard representative lane)" {
        It "extracts the job block" {
            $script:standard | Should -Not -BeNullOrEmpty
        }

        It "runs exactly the self-contained and keycloak identity providers" {
            $script:standard | Should -Match 'identityprovider:\s*\[\s*self-contained\s*,\s*keycloak\s*\]'
        }

        It "invokes the public E2ETest entry point against the MSSQL engine with the base E2E env file and the exact representative filter" {
            $runLine = Get-RunCommandLine -Block $script:standard -Command 'E2ETest'
            $runLine | Should -Not -BeNullOrEmpty
            $runLine | Should -Match ([regex]::Escape("-DatabaseEngine mssql"))
            $runLine | Should -Match ([regex]::Escape("-EnvironmentFile './.env.e2e'"))
            $runLine | Should -Match ([regex]::Escape("-TestFilter 'Category=@MssqlRepresentative'"))
            $runLine | Should -Not -Match ([regex]::Escape("-DataStandardVersion"))
        }
    }

    Context "run-e2e-tests-mssql-ds61 (version-coupled lane)" {
        It "extracts the job block" {
            $script:ds61 | Should -Not -BeNullOrEmpty
        }

        It "is a single self-contained job with no matrix" {
            $script:ds61 | Should -Not -Match '(?m)^    strategy:'
            $script:ds61 | Should -Not -Match '(?m)^\s*matrix:'
        }

        It "invokes E2ETest against MSSQL with DS 6.1 and the exact version-coupled filter" {
            $runLine = Get-RunCommandLine -Block $script:ds61 -Command 'E2ETest'
            $runLine | Should -Not -BeNullOrEmpty
            $runLine | Should -Match ([regex]::Escape("-DatabaseEngine mssql"))
            $runLine | Should -Match ([regex]::Escape("-IdentityProvider self-contained"))
            $runLine | Should -Match ([regex]::Escape("-EnvironmentFile './.env.e2e'"))
            $runLine | Should -Match ([regex]::Escape("-DataStandardVersion 6.1"))
            $runLine | Should -Match ([regex]::Escape("-TestFilter 'Category=@StandardVersion-6_1'"))
        }

        It "does not start Kafka or set a Kafka host entry (the MSSQL stack is relational-only)" {
            $script:ds61 | Should -Not -Match ([regex]::Escape("dms-kafka1"))
            $script:ds61 | Should -Not -Match ([regex]::Escape("KAFKA_BOOTSTRAP_SERVERS"))
            $script:ds61 | Should -Not -Match ([regex]::Escape("/etc/hosts"))
        }
    }

    Context "run-instance-management-e2e-tests-mssql (instance lane)" {
        It "extracts the job block" {
            $script:instance | Should -Not -BeNullOrEmpty
        }

        It "runs exactly shards 1 and 2" {
            $script:instance | Should -Match 'shard:\s*\[\s*1\s*,\s*2\s*\]'
        }

        It "invokes the public InstanceE2ETest entry point against MSSQL with the exact shard filter" {
            $runLine = Get-RunCommandLine -Block $script:instance -Command 'InstanceE2ETest'
            $runLine | Should -Not -BeNullOrEmpty
            $runLine | Should -Match ([regex]::Escape("-DatabaseEngine mssql"))
            $runLine | Should -Match ([regex]::Escape("-TestFilter 'Category=@instance-management-ci-shard-`${{ matrix.shard }}'"))
        }

        It "omits -EnvironmentFile on the run command so the DMS1284-U6-P1 default is exercised" {
            $runLine = Get-RunCommandLine -Block $script:instance -Command 'InstanceE2ETest'
            $runLine | Should -Not -Match ([regex]::Escape("-EnvironmentFile"))
        }
    }

    Context "always-run engine-correct teardown per lane" {
        It "run-e2e-tests-mssql tears down once with the standard base env and MSSQL engine, always()" {
            @([regex]::Matches($script:standard, [regex]::Escape("teardown-local-dms.ps1"))).Count | Should -Be 1
            $teardownStep = Get-StepChunk -Block $script:standard |
                Where-Object { $_ -match "teardown-local-dms\.ps1" }
            $teardownStep | Should -Not -BeNullOrEmpty
            $teardownStep | Should -Match ([regex]::Escape("if: always()"))
            $teardownStep | Should -Match ([regex]::Escape("EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '.env.e2e'"))
        }

        It "run-e2e-tests-mssql-ds61 tears down once with the standard base env and MSSQL engine, always()" {
            @([regex]::Matches($script:ds61, [regex]::Escape("teardown-local-dms.ps1"))).Count | Should -Be 1
            $teardownStep = Get-StepChunk -Block $script:ds61 |
                Where-Object { $_ -match "teardown-local-dms\.ps1" }
            $teardownStep | Should -Match ([regex]::Escape("if: always()"))
            $teardownStep | Should -Match ([regex]::Escape("EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '.env.e2e'"))
        }

        It "run-instance-management-e2e-tests-mssql tears down once with the route-context base env and MSSQL engine, always()" {
            @([regex]::Matches($script:instance, [regex]::Escape("teardown-local-dms.ps1"))).Count | Should -Be 1
            $teardownStep = Get-StepChunk -Block $script:instance |
                Where-Object { $_ -match "teardown-local-dms\.ps1" }
            $teardownStep | Should -Match ([regex]::Escape("if: always()"))
            $teardownStep | Should -Match ([regex]::Escape("EdFi.InstanceManagement.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '.env.routeContext.e2e'"))
        }
    }

    Context "sanitize-before-publish ordering and gating" {
        It "orders log capture before teardown before the sanitizer, and the sanitizer before every reporter/upload" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $captureIndex = $block.IndexOf("docker logs")
            $teardownIndex = $block.IndexOf("teardown-local-dms.ps1")
            $sanitizeIndex = $block.IndexOf("sanitize-e2e-artifacts.ps1")
            $firstUploadIndex = $block.IndexOf("uses: actions/upload-artifact")
            $reporterIndex = $block.IndexOf("uses: dorny/test-reporter")

            $captureIndex | Should -BeGreaterThan 0
            $teardownIndex | Should -BeGreaterThan 0
            $sanitizeIndex | Should -BeGreaterThan 0
            $firstUploadIndex | Should -BeGreaterThan 0
            $reporterIndex | Should -BeGreaterThan 0

            $captureIndex | Should -BeLessThan $teardownIndex -Because "container logs must be snapshotted before teardown removes the containers"
            $teardownIndex | Should -BeLessThan $sanitizeIndex -Because "the sanitizer runs after teardown"
            $sanitizeIndex | Should -BeLessThan $firstUploadIndex -Because "diagnostics must be sanitized before any artifact upload"
            $sanitizeIndex | Should -BeLessThan $reporterIndex -Because "the TRX must be sanitized before the dorny reporter consumes it"
        }

        It "gates every reporter/upload that consumes diagnostic, TRX, or timing content on sanitizer success" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $consumingSteps = Get-StepChunk -Block $block | Where-Object {
                $_ -match "uses: actions/upload-artifact" -or
                $_ -match "uses: dorny/test-reporter" -or
                $_ -match "summarize-test-timings\.ps1"
            }

            $consumingSteps.Count | Should -BeGreaterThan 0
            foreach ($step in $consumingSteps) {
                $step | Should -Match ([regex]::Escape("steps.sanitize.outcome == 'success'")) `
                    -Because "content-publishing steps must be skipped when sanitization fails"
            }
        }

        It "declares the sanitizer step id used by the gates" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }
            $block | Should -Match ([regex]::Escape("id: sanitize"))
        }
    }

    Context "gate, summary, and needs wiring (MSSQL added, PostgreSQL preserved)" {
        It "adds both standard MSSQL lanes to e2e-summary needs and result checks while keeping the PG lanes" {
            $script:e2eSummary | Should -Match ([regex]::Escape("run-e2e-tests-mssql,"))
            $script:e2eSummary | Should -Match ([regex]::Escape("run-e2e-tests-mssql-ds61,"))
            $script:e2eSummary | Should -Match ([regex]::Escape("needs.run-e2e-tests-mssql.result"))
            $script:e2eSummary | Should -Match ([regex]::Escape("needs.run-e2e-tests-mssql-ds61.result"))
            # PG wiring preserved
            $script:e2eSummary | Should -Match ([regex]::Escape("needs.run-e2e-tests.result"))
            $script:e2eSummary | Should -Match ([regex]::Escape("needs.run-e2e-tests-ds61.result"))
        }

        It "adds all three MSSQL lanes to test-timing-summary needs" {
            $script:timingSummary | Should -Match ([regex]::Escape("run-e2e-tests-mssql,"))
            $script:timingSummary | Should -Match ([regex]::Escape("run-e2e-tests-mssql-ds61,"))
            $script:timingSummary | Should -Match ([regex]::Escape("run-instance-management-e2e-tests-mssql,"))
        }

        It "adds the representative and instance MSSQL lanes to event_file needs, mirroring the PG analogs (no ds61)" {
            $script:eventFile | Should -Match ([regex]::Escape("run-e2e-tests-mssql,"))
            $script:eventFile | Should -Match ([regex]::Escape("run-instance-management-e2e-tests-mssql,"))
            $script:eventFile | Should -Not -Match ([regex]::Escape("run-e2e-tests-mssql-ds61"))
        }

        It "covers all three MSSQL lanes directly in the dms-ci-gate needs, keeping the PG lanes" {
            $script:gate | Should -Match ([regex]::Escape("- run-e2e-tests-mssql`n"))
            $script:gate | Should -Match ([regex]::Escape("- run-e2e-tests-mssql-ds61"))
            $script:gate | Should -Match ([regex]::Escape("- run-instance-management-e2e-tests-mssql"))
            # PG wiring preserved
            $script:gate | Should -Match ([regex]::Escape("- run-e2e-tests`n"))
            $script:gate | Should -Match ([regex]::Escape("- run-e2e-tests-ds61"))
            $script:gate | Should -Match ([regex]::Escape("- run-instance-management-e2e-tests`n"))
        }

        It "wires the new ticket Pester specs into run-bootstrap-pester-tests" {
            $bootstrap = Get-JobBlock 'run-bootstrap-pester-tests'
            $bootstrap | Should -Match ([regex]::Escape("eng/ci/tests/DmsPullRequestMssqlWorkflow.Tests.ps1"))
            $bootstrap | Should -Match ([regex]::Escape("eng/ci/tests/Sanitize-E2EArtifacts.Tests.ps1"))
            $bootstrap | Should -Match ([regex]::Escape("eng/docker-compose/tests/E2EEngineForwarding.Tests.ps1"))
            $bootstrap | Should -Match ([regex]::Escape("eng/docker-compose/tests/E2ETeardownSafety.Tests.ps1"))
            $bootstrap | Should -Match ([regex]::Escape("eng/docker-compose/tests/InstanceE2EForwarding.Tests.ps1"))
            $bootstrap | Should -Match ([regex]::Escape("eng/docker-compose/tests/E2ETestProcessContext.Tests.ps1"))
        }
    }

    Context "pinned action, retention, and artifact-naming conventions" {
        It "pins upload-artifact and dorny/test-reporter to the neighboring SHAs in every new lane" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $uploadRefs = [regex]::Matches($block, "uses: actions/upload-artifact@(?<sha>\S+)")
            $uploadRefs.Count | Should -BeGreaterThan 0
            foreach ($ref in $uploadRefs) {
                $ref.Groups["sha"].Value | Should -Be "6f51ac03b9356f520e9adb1b1b7802705f340c2b"
            }

            $reporterRefs = [regex]::Matches($block, "uses: dorny/test-reporter@(?<sha>\S+)")
            $reporterRefs.Count | Should -Be 1
            $reporterRefs[0].Groups["sha"].Value | Should -Be "a43b3a5f7366b97d083190328d2c652e1a8b6aa2"
        }

        It "names MSSQL artifacts distinctly from the PostgreSQL lanes" {
            $script:standard | Should -Match ([regex]::Escape("test-timings-e2e-mssql-`${{ matrix.identityprovider }}-representative"))
            $script:standard | Should -Match ([regex]::Escape("mssql-`${{ matrix.identityprovider }}-e2e-representative-test-logs"))
            $script:ds61 | Should -Match ([regex]::Escape("test-timings-e2e-mssql-self-contained-ds61"))
            $script:instance | Should -Match ([regex]::Escape("test-timings-instance-mssql-shard-`${{ matrix.shard }}"))
        }

        It "sets retention on every new-lane artifact upload" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }
            $uploadSteps = Get-StepChunk -Block $block | Where-Object { $_ -match "uses: actions/upload-artifact" }
            foreach ($step in $uploadSteps) {
                $step | Should -Match "retention-days:\s*\d+"
            }
        }
    }

    Context "secret-safe file-only capture and build-before-filter (DMS1284-U6-C1)" {
        It "captures build output via a child pwsh to a file only - never Tee-Object, raw *>&1, or a direct in-process invocation" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql'; Command = 'E2ETest' }
            @{ Name = 'run-e2e-tests-mssql-ds61'; Command = 'E2ETest' }
            @{ Name = 'run-instance-management-e2e-tests-mssql'; Command = 'InstanceE2ETest' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $block | Should -Not -Match ([regex]::Escape("Tee-Object")) `
                -Because "raw output must never be mirrored to the immutable Actions console before sanitization"
            $block | Should -Not -Match ([regex]::Escape("*>&1")) `
                -Because "a raw all-stream pass-through pipe would expose credentials on the console"

            $runLine = Get-RunCommandLine -Block $block -Command $Command
            $runLine | Should -Match ([regex]::Escape("& pwsh -NoProfile -File ./build-dms.ps1 $Command")) `
                -Because "build-dms.ps1 must run in a child pwsh so its Invoke-Main exit does not terminate the run block and the LASTEXITCODE guard stays reachable"
            $runLine | Should -Not -Match '^\s*\./build-dms\.ps1' `
                -Because "a direct in-process ./build-dms.ps1 invocation makes the LASTEXITCODE guard unreachable"
            $runLine | Should -Match ([regex]::Escape("*> `$diagFile")) `
                -Because "child build output must be redirected to a file only"
            $block | Should -Match ([regex]::Escape('$buildExitCode = $LASTEXITCODE')) `
                -Because "the child exit code must be captured immediately after the redirected child invocation"
            $block | Should -Match ([regex]::Escape('if ($buildExitCode -ne 0)')) `
                -Because "the captured child exit code must be re-raised as a secret-free failure"
        }

        It "confirms a child pwsh exit code returns to the parent so the LASTEXITCODE guard is reachable (placeholder probe)" {
            # The lanes rely on this control flow: build-dms.ps1's Invoke-Main `exit` must terminate only a
            # CHILD pwsh so control returns to the run block and the guard runs. Proven with a bare `exit 7`
            # in a child pwsh - no Docker, no build-dms.ps1, no arguments or captured output.
            & pwsh -NoProfile -Command 'exit 7' 2>$null
            $buildExitCode = $LASTEXITCODE
            $guardReached = $true

            $guardReached | Should -BeTrue -Because "the child exit did not terminate this runspace"
            $buildExitCode | Should -Be 7 -Because "the parent receives the child's exit code, unlike a direct in-process exit which would terminate the runspace before the guard"
        }

        It "shows only the already-sanitized setup log on the console, gated on sanitizer success" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $displayStep = Get-StepChunk -Block $block | Where-Object {
                $_ -match "Get-Content" -and $_ -match "build-dms-setup\.log"
            }
            $displayStep | Should -Not -BeNullOrEmpty
            $displayStep | Should -Match ([regex]::Escape("if: failure() && steps.sanitize.outcome == 'success'"))
        }

        It "runs build-dms.ps1 Build before the filtered run command" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql'; Command = 'E2ETest' }
            @{ Name = 'run-e2e-tests-mssql-ds61'; Command = 'E2ETest' }
            @{ Name = 'run-instance-management-e2e-tests-mssql'; Command = 'InstanceE2ETest' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $buildIndex = $block.IndexOf("build-dms.ps1 Build")
            $runIndex = $block.IndexOf("build-dms.ps1 $Command")
            $buildIndex | Should -BeGreaterThan 0
            $runIndex | Should -BeGreaterThan 0
            $buildIndex | Should -BeLessThan $runIndex `
                -Because "the Build step must compile the tags before the category filter selects scenarios"
        }
    }
}
