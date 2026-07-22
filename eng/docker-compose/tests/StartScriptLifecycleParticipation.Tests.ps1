# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Finding 6: lifecycle participation. Full startup (default / -InfraOnly / -DmsOnly) materializes the CMS
# topology; the database-only diagnostic slice and teardown do NOT participate in CMS topology and must not
# require its materialization (which throws on a minimal environment that omits POSTGRES_DB_NAME) or resolve
# the schema tool. These paths must reach their Compose action honoring the Compose database default.
#
# The teardown path is proven behaviorally against a minimal environment with docker stubbed (a function
# named 'docker' shadows the native command inside the invoked script). The -DbOnly gate is proven
# structurally through the AST, because -DbOnly's real readiness probe uses System.Diagnostics.Process and
# cannot be shadowed by a function stub.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    # A minimal PostgreSQL environment based on the tracked example but WITHOUT POSTGRES_DB_NAME (and the
    # DMS_CONFIG_DATABASE_NAME that references it). postgresql.yml supplies the datastore default, so full
    # startup would still resolve, but the host-side topology resolver has no name to materialize and throws
    # - which is exactly why -DbOnly and teardown must not call it.
    $script:minimalEnvDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-f6-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:minimalEnvDir -Force | Out-Null
    $script:minimalEnv = Join-Path $script:minimalEnvDir "minimal.env"
    (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example")) |
        Where-Object { $_ -notmatch "^POSTGRES_DB_NAME=" -and $_ -notmatch "^DMS_CONFIG_DATABASE_NAME=" } |
        Set-Content -LiteralPath $script:minimalEnv

    # Invoke a start script with the native 'docker' command shadowed by a stub, capturing the docker
    # invocations and returning any terminating error. LASTEXITCODE is forced to 0 so the scripts' own
    # exit-code checks pass without a real Docker daemon.
    function Invoke-StartScriptWithDockerStub {
        param([string]$ScriptName, [hashtable]$ScriptParams)

        # Capture docker invocations through a temp file whose path travels in an environment variable:
        # the stub 'docker' function is invoked from deep inside the start script's scope, where a $script:
        # variable would not resolve back to this test module, and a global variable trips PSScriptAnalyzer.
        $captureFile = Join-Path $script:minimalEnvDir ("docker-calls-" + [guid]::NewGuid().ToString("N") + ".log")
        $env:DMS_F6_DOCKER_CAPTURE = $captureFile
        function docker {
            Add-Content -LiteralPath $env:DMS_F6_DOCKER_CAPTURE -Value ($args -join " ")
            $global:LASTEXITCODE = 0
        }

        $caught = $null
        try {
            & (Join-Path $script:composeRoot $ScriptName) @ScriptParams *> $null
        }
        catch {
            $caught = $_
        }
        $calls = if (Test-Path -LiteralPath $captureFile) { @(Get-Content -LiteralPath $captureFile) } else { @() }
        $env:DMS_F6_DOCKER_CAPTURE = $null
        return [pscustomobject]@{
            Error       = $caught
            DockerCalls = $calls
        }
    }
}

AfterAll {
    if (Test-Path -LiteralPath $script:minimalEnvDir) {
        Remove-Item -Recurse -Force $script:minimalEnvDir -ErrorAction SilentlyContinue
    }
}

Describe "The topology resolver throws on a minimal environment (the condition -DbOnly/teardown must avoid)" {
    It "rejects a minimal PostgreSQL environment that omits POSTGRES_DB_NAME" {
        { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:minimalEnv -DockerComposeRoot $script:minimalEnvDir -DatabaseEngine postgresql } |
            Should -Throw "*could not determine the effective configuration database name*"
    }
}

Describe "Teardown does not participate in CMS topology (finding 6, behavioral)" {
    It "<Script> teardown reaches 'compose down' on a minimal environment without materializing topology" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        $result = Invoke-StartScriptWithDockerStub -ScriptName $Script -ScriptParams @{ d = $true; EnvironmentFile = $script:minimalEnv; DatabaseEngine = 'postgresql' }

        $result.Error | Should -BeNullOrEmpty -Because "teardown must not require CMS-topology materialization on a minimal environment"
        ($result.DockerCalls -join " `n") | Should -Match 'compose .*down' -Because "$Script teardown must reach 'docker compose down'"
    }
}

Describe "Topology materialization is gated to full startup (finding 6, AST participation)" {
    It "<Script> calls Resolve-ConfigDatabaseTopologyEnvironmentFile only inside an if guarded by both -d and -DbOnly" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        $path = Join-Path $script:composeRoot $Script
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)

        $topologyCalls = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Resolve-ConfigDatabaseTopologyEnvironmentFile'
            }, $true)
        @($topologyCalls).Count | Should -Be 1 -Because "$Script materializes topology in exactly one place"

        # Walk up to the enclosing if-statement and confirm its condition gates on both lifecycle flags.
        $node = $topologyCalls[0]
        $enclosingIf = $null
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                $enclosingIf = $node
                break
            }
            $node = $node.Parent
        }
        $enclosingIf | Should -Not -BeNullOrEmpty -Because "the topology call must be inside an if guard"
        $conditionText = $enclosingIf.Clauses[0].Item1.Extent.Text
        $conditionText | Should -Match '\$d' -Because "teardown must not materialize CMS topology"
        $conditionText | Should -Match '\$DbOnly' -Because "the database-only diagnostic slice must not materialize CMS topology"
    }

    It "<Script> resolves the connection validator ('<Resolver>') only on the non-DbOnly startup path (never for -DbOnly or teardown)" -ForEach @(
        # The local lane resolves a host executable; the published lane resolves a host-exe-or-container
        # validator (finding 4). Either way the resolution must be gated away from -DbOnly and teardown.
        @{ Script = 'start-local-dms.ps1';     Resolver = 'Resolve-DmsSchemaTool' }
        @{ Script = 'start-published-dms.ps1'; Resolver = 'Resolve-DmsConnectionValidator' }
    ) {
        $path = Join-Path $script:composeRoot $Script
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)

        $resolveCalls = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq $Resolver
            }, $true)
        @($resolveCalls).Count | Should -Be 1 -Because "$Script resolves the connection validator in one place"

        $node = $resolveCalls[0]
        $guardConditions = [System.Collections.Generic.List[string]]::new()
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                $guardConditions.Add($node.Clauses[0].Item1.Extent.Text)
            }
            $node = $node.Parent
        }
        ($guardConditions -join " `n") | Should -Match '\$DbOnly' -Because "$Script must not resolve the connection validator for -DbOnly or teardown"
    }
}
