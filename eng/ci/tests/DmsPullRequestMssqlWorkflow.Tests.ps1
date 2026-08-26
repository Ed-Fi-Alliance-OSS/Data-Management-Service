# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Secret-safety guardrails for the SQL Server E2E lanes in .github/workflows/on-dms-pullrequest.yml.
# Scope is deliberately narrow: only the invariants that keep an unredacted credential out of a
# published artifact or off the immutable Actions console. Those live purely in declarative CI wiring -
# step order and if: conditions - so no invoked-script test can reach them, and a regression publishes a
# secret rather than failing a build. Everything else about these lanes (engine flags, filters, teardown
# arguments, artifact naming, action pinning, needs wiring) is either behavior covered by the invoked
# E2EEngineForwarding / E2ETeardownSafety / InstanceE2EForwarding / Sanitize-E2EArtifacts specs or a
# convention a failing lane surfaces directly, and is intentionally not asserted from source here.
#
# No YAML parser is available in this lane, so (following
# eng/DatabaseTemplates/tests/Template-WorkflowInputs.Tests.ps1) each named job block is extracted by its
# two-space job key and invariants are asserted inside that block.

Describe "on-dms-pullrequest.yml SQL Server E2E lane secret-safety guardrails" {
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
    }

    Context "the SQL Server lanes exist" {
        It "extracts each new lane's job block" {
            $script:standard | Should -Not -BeNullOrEmpty
            $script:ds61 | Should -Not -BeNullOrEmpty
            $script:instance | Should -Not -BeNullOrEmpty
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

    Context "secret-safe file-only capture" {
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

        It "restricts diagnostic container capture to the dms-local Compose project, not every container" -ForEach @(
            @{ Name = 'run-e2e-tests-mssql' }
            @{ Name = 'run-e2e-tests-mssql-ds61' }
            @{ Name = 'run-instance-management-e2e-tests-mssql' }
        ) {
            $block = switch ($Name) {
                'run-e2e-tests-mssql' { $script:standard }
                'run-e2e-tests-mssql-ds61' { $script:ds61 }
                'run-instance-management-e2e-tests-mssql' { $script:instance }
            }

            $captureStep = Get-StepChunk -Block $block | Where-Object { $_ -match "docker logs" }
            $captureStep | Should -Not -BeNullOrEmpty
            $captureStep | Should -Match ([regex]::Escape('docker ps -a --filter "label=com.docker.compose.project=dms-local"')) `
                -Because "diagnostics must not collect unrelated (buildx / other) containers"
            $captureStep | Should -Not -Match ([regex]::Escape('docker ps -a --format')) `
                -Because "an unfiltered enumeration of every container is rejected"
        }
    }
}

Describe "on-dms-pullrequest.yml bootstrap Pester registry" {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/on-dms-pullrequest.yml")
        )
        $script:content = Get-Content -LiteralPath $script:workflowPath -Raw

        $pathListMatch = [regex]::Match($script:content, '(?ms)^\s+\$paths\s*=\s*@\(\s*(?<body>.*?)^\s+\)\s*$')
        if (-not $pathListMatch.Success) {
            throw "The run-bootstrap-pester-tests `$paths array was not found in $script:workflowPath."
        }

        $script:pesterPaths = @(
            [regex]::Matches($pathListMatch.Groups["body"].Value, '"(?<path>[^"]+\.Tests\.ps1)"') |
                ForEach-Object { $_.Groups["path"].Value }
        )
    }

    It "runs the Docker Compose logging guard in the pull request Pester lane" {
        $script:pesterPaths | Should -Contain "eng/docker-compose/tests/DockerComposeLogging.Tests.ps1" `
            -Because "the DMS-1407 compose logging guard must run on every DMS-relevant pull request"
    }

    It "runs the DocumentCacheAdmin package-target guard in the pull request Pester lane" {
        $script:pesterPaths | Should -Contain "eng/ci/tests/DocumentCacheAdminPackageTarget.Tests.ps1" `
            -Because "DocumentCacheAdmin package-target wiring must run on every DMS-relevant pull request"
    }
}
