# Wiring, gating, and ordering coverage for the server-backed MSSQL topology-consistency check
# in start-local-dms.ps1 and start-published-dms.ps1.
#
# The harness runs the REAL scripts and intercepts three seams, portably and with no PATH files:
#
# - `docker` is a global FUNCTION stand-in (the established pattern: functions beat external
#   commands, and lookup walks into `&`-invoked scripts). Its policy lets exactly the
#   `compose ... up ... db` call SUCCEED - the boundary under test lies past it - and fails
#   every other compose subcommand, so each run ends at a deterministic, assertable point.
# - Test-NativeCommandWithTimeout and Assert-MssqlTopologyPhysicalConsistency are intercepted
#   with global ALIASES over recording shims. An alias is required, not a function: the scripts
#   re-run Import-Module -Force, which re-registers same-named FUNCTIONS in the global table,
#   but aliases outrank functions in command precedence and imports do not touch them
#   (spike-verified). The readiness shim makes Wait-MssqlReady/-PostgresqlReady succeed
#   instantly; the authority shim records its bound arguments and THROWS a sentinel, so every
#   positive run stops exactly at the boundary and nothing downstream (OpenIddict, CMS, DMS,
#   registration) can execute.
#
# Every event (docker argv, readiness probe, authority call) lands in one ordered list, which is
# what the ordering assertions read. The authority's own behavior (mode selection from the raw
# marker, transport, verdicts, relation enforcement) is unit-tested in
# MssqlPhysicalDistinctnessAuthority.Tests.ps1; here the shim records what the SCRIPTS hand it.

# The authority shim must mirror the real parameter surface, including -SaPassword, to bind the
# scripts' named arguments; the real parameter's plaintext trade-off is documented on the
# authority itself, and the stand-in only records the value for assertions. Scriptblock-level
# suppression attributes are not honored by the analyzer, so the suppression lives here.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Test stand-in mirrors the real authority parameter surface to bind named arguments; see the suppression on Assert-MssqlTopologyPhysicalConsistency.')]
param()

BeforeAll {
    $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

    $script:authoritySentinel = "WIRING-AUTHORITY-SENTINEL"

    # Scalar snapshot of a Function:/Alias: item - presence, the definition needed to recreate
    # it, its OPTIONS, and its DESCRIPTION (review-measured: a restoration that stops at the
    # definition silently strips ReadOnly and empties the description). Never a live
    # FunctionInfo/AliasInfo, which mutates with the table.
    function script:Get-CommandStateSnapshot {
        param([Parameter(Mandatory)] [string]$ItemPath)
        $item = Get-Item $ItemPath -ErrorAction SilentlyContinue
        if ($null -eq $item) { return @{ Present = $false } }
        if ($item -is [System.Management.Automation.AliasInfo]) {
            return @{ Present = $true; Definition = [string]$item.Definition; Options = $item.Options; Description = [string]$item.Description }
        }
        return @{ Present = $true; ScriptBlock = $item.ScriptBlock; Options = $item.Options; Description = [string]$item.Description }
    }

    # The ONE alias/function restoration implementation - every cleanup path goes through it,
    # so fidelity cannot fork between the harness and the safety tests. Order matters: the
    # definition is recreated first, the description applied second, and the options LAST,
    # because a ReadOnly option would refuse the description write.
    function script:Restore-CommandFromSnapshot {
        param(
            [Parameter(Mandatory)] [ValidateSet("Function", "Alias")] [string]$Kind,
            [Parameter(Mandatory)] [string]$Name,
            [Parameter(Mandatory)] [hashtable]$Snapshot
        )
        Remove-Item "$($Kind):\$Name" -Force -ErrorAction SilentlyContinue
        if (-not $Snapshot.Present) { return }
        if ($Kind -eq "Alias") {
            Set-Alias -Name $Name -Value $Snapshot.Definition -Scope Global -Force -Description ([string]$Snapshot.Description)
        }
        else {
            Set-Item -Path "Function:\global:$Name" -Force -Value $Snapshot.ScriptBlock
            if (-not [string]::IsNullOrEmpty([string]$Snapshot.Description)) {
                (Get-Item "Function:\$Name").Description = [string]$Snapshot.Description
            }
        }
        if ($Snapshot.Options -ne [System.Management.Automation.ScopedItemOptions]::None) {
            (Get-Item "$($Kind):\$Name").Options = $Snapshot.Options
        }
    }

    # Runs every restoration step even when one fails: the session is repaired as far as
    # possible FIRST, and a single aggregate error reports the failures afterwards. Factored
    # out so the fault tolerance itself is unit-testable.
    function script:Invoke-WiringStateRestoration {
        param([Parameter(Mandatory)] [object[]]$Step)
        $failures = [System.Collections.Generic.List[string]]::new()
        foreach ($restoreStep in $Step) {
            try { & $restoreStep.Action }
            catch { $failures.Add("$($restoreStep.Description): $($_.Exception.Message)") }
        }
        if ($failures.Count -gt 0) {
            throw "Session-state restoration reported failures (all other state was still restored): $($failures -join ' | ')"
        }
    }

    function script:Invoke-WiringRun {
        param(
            [Parameter(Mandatory)] [scriptblock]$ScriptBlock,
            # Lets `compose ... up ... keycloak` succeed too, for the staged participating
            # keycloak rows; the default policy succeeds ONLY the db-up call.
            [switch]$AllowKeycloakUp
        )

        # ---- Transactional interception: reject what cannot be shadowed AND restored, take
        # ---- scalar snapshots of everything this run will touch, stage GUID-named shims, and
        # ---- in finally remove ONLY what this invocation created before restoring every
        # ---- snapshot - with each restoration step fault-isolated from the others.
        #
        # Option support is MEASURED, not assumed: None and ReadOnly are supported (-Force
        # replaces a ReadOnly command and the saved Options reapply on restore); AllScope is
        # rejected before any mutation (Set-Item -Force cannot replace an AllScope function -
        # 'The AllScope option cannot be removed' - while Remove-Item CAN delete it, so any
        # partial handling would destroy the caller's command); Constant can be neither
        # replaced nor removed.
        $unsupportedOptions = [System.Management.Automation.ScopedItemOptions]::AllScope -bor
            [System.Management.Automation.ScopedItemOptions]::Constant

        # An effective `docker` ALIAS outranks any function stand-in: with one in place the
        # scripts' docker calls would resolve through it - to real Docker, if that is its
        # target. Refuse before staging anything, leaving the alias untouched.
        if (Get-Command docker -CommandType Alias -ErrorAction SilentlyContinue) {
            throw "Refusing to run: an effective 'docker' alias is defined in this session and would outrank the function stand-in, so the scripts could reach real Docker."
        }
        $interceptedAliasNames = @('Test-NativeCommandWithTimeout', 'Assert-MssqlTopologyPhysicalConsistency', 'Start-Sleep')
        $savedAliases = @{}
        foreach ($aliasName in $interceptedAliasNames) {
            $aliasSnapshot = Get-CommandStateSnapshot -ItemPath "Alias:\$aliasName"
            if ($aliasSnapshot.Present -and ($aliasSnapshot.Options -band $unsupportedOptions)) {
                throw "Refusing to run: the pre-existing '$aliasName' alias carries option '$($aliasSnapshot.Options)' and cannot be shadowed and exactly restored."
            }
            $savedAliases[$aliasName] = $aliasSnapshot
        }
        $savedDockerFunction = Get-CommandStateSnapshot -ItemPath "Function:\docker"
        if ($savedDockerFunction.Present -and ($savedDockerFunction.Options -band $unsupportedOptions)) {
            throw "Refusing to run: the pre-existing 'docker' function carries option '$($savedDockerFunction.Options)' and cannot be shadowed and exactly restored."
        }

        # Presence and value are separate state: a fresh session has NO LASTEXITCODE at all,
        # and restoring absence means removal, never a created null-valued variable.
        $lastExitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        $savedLastExitCode = @{
            Present = ($null -ne $lastExitCodeVariable)
            Value   = if ($null -ne $lastExitCodeVariable) { $lastExitCodeVariable.Value } else { $null }
        }

        $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
        $before = @{}
        if (Test-Path $derivedDir) {
            foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) { $before[$name] = $true }
        }

        # The invoked start scripts write these identity variables and nothing restores them.
        $identityEnvironmentName = @(
            'DMS_CONFIG_IDENTITY_PROVIDER', 'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY',
            'DMS_JWT_METADATA_ADDRESS', 'DMS_CONFIG_IDENTITY_AUTHORITY'
        )
        $identityEnvironmentState = @{}
        foreach ($name in $identityEnvironmentName) {
            $identityEnvironmentState[$name] = @{
                Present = (Test-Path -LiteralPath "Env:\$name")
                Value   = [System.Environment]::GetEnvironmentVariable($name)
            }
        }

        $events = [System.Collections.Generic.List[string]]::new()
        $authorityCalls = [System.Collections.Generic.List[object]]::new()
        $allowKeycloak = [bool]$AllowKeycloakUp
        $shimSuffix = [Guid]::NewGuid().ToString('N')
        $readinessShimName = "__WiringReadinessShim_$shimSuffix"
        $authorityShimName = "__WiringAuthorityShim_$shimSuffix"
        $sleepShimName = "__WiringSleepShim_$shimSuffix"
        $caught = $null
        try {
            Set-Item -Path Function:\global:docker -Force -Value {
                $flattened = @($args | ForEach-Object { $_ })
                $joined = $flattened -join " "
                $events.Add("docker: $joined")
                if ($flattened.Count -gt 0 -and $flattened[0] -eq "compose") {
                    $isDbUp = ($joined -match " up ") -and ($flattened[-1] -eq "db")
                    $isKeycloakUp = ($joined -match " up ") -and ($flattened[-1] -eq "keycloak")
                    $global:LASTEXITCODE = if ($isDbUp -or ($isKeycloakUp -and $allowKeycloak)) { 0 } else { 1 }
                }
                else {
                    $global:LASTEXITCODE = 0
                }
            }.GetNewClosure()

            Set-Item -Path "Function:\global:$readinessShimName" -Value {
                # The parameters exist only to bind the readiness probe's named arguments; the
                # shim answers "ready" unconditionally. The $null assignment consumes them,
                # which is what the analyzer's unused-parameter rule wants stated explicitly.
                param($FilePath, $ArgumentList, $TimeoutSeconds)
                $null = $FilePath, $ArgumentList, $TimeoutSeconds
                $events.Add("readiness")
                return $true
            }.GetNewClosure()
            Set-Alias -Name Test-NativeCommandWithTimeout -Value $readinessShimName -Scope Global -Force

            Set-Item -Path "Function:\global:$authorityShimName" -Value {
                param(
                    [string]$EnvironmentFile,
                    [string]$ContainerName,
                    [string]$SaPassword,
                    [string]$RegisteredDatastoreDatabaseName = "",
                    [int]$TimeoutSeconds = 60
                )
                $null = $TimeoutSeconds
                $events.Add("authority")
                $authorityCalls.Add([pscustomobject]@{
                        EnvironmentFile                 = $EnvironmentFile
                        ContainerName                   = $ContainerName
                        SaPassword                      = $SaPassword
                        RegisteredDatastoreDatabaseName = $RegisteredDatastoreDatabaseName
                    })
                throw "WIRING-AUTHORITY-SENTINEL"
            }.GetNewClosure()
            Set-Alias -Name Assert-MssqlTopologyPhysicalConsistency -Value $authorityShimName -Scope Global -Force

            # The first Start-Sleep past the readiness/authority boundary is the scripts' next
            # statement on every gated-off path, so throwing there gives negative runs a
            # deterministic terminus that PROVES they passed through the boundary region -
            # instead of letting the real setup-openiddict sibling execute. Positive runs never
            # reach it (the authority sentinel fires first), and the readiness waits never
            # sleep because the readiness shim succeeds on the first probe.
            Set-Item -Path "Function:\global:$sleepShimName" -Value {
                param($Seconds, $Milliseconds)
                $null = $Seconds, $Milliseconds
                $events.Add("sleep")
                throw "WIRING-POSTBOUNDARY-SENTINEL"
            }.GetNewClosure()
            Set-Alias -Name Start-Sleep -Value $sleepShimName -Scope Global -Force

            & $ScriptBlock
        }
        catch {
            $caught = $_
        }
        finally {
            # Remove ONLY what this invocation created, then restore every snapshot exactly.
            # Each step is fault-isolated: one failed restoration never prevents the others
            # (the driver repairs the session first, then reports).
            # A .GetNewClosure() scriptblock cannot resolve script:-scoped FUNCTIONS (the same
            # closure-scope rule the module mocks hit), so the central restorer travels into
            # the steps as a captured scriptblock VARIABLE invoked with '&'.
            $restoreCommand = ${function:Restore-CommandFromSnapshot}
            $restorationSteps = [System.Collections.Generic.List[object]]::new()
            foreach ($aliasName in $interceptedAliasNames) {
                $savedAlias = $savedAliases[$aliasName]
                $restorationSteps.Add(@{
                        Description = "intercept alias '$aliasName'"
                        Action      = {
                            & $restoreCommand -Kind Alias -Name $aliasName -Snapshot $savedAlias
                        }.GetNewClosure()
                    })
            }
            foreach ($shimName in @($readinessShimName, $authorityShimName, $sleepShimName)) {
                $restorationSteps.Add(@{
                        Description = "shim function '$shimName'"
                        Action      = { Remove-Item "Function:\$shimName" -Force -ErrorAction SilentlyContinue }.GetNewClosure()
                    })
            }
            $restorationSteps.Add(@{
                    Description = "docker function"
                    Action      = {
                        & $restoreCommand -Kind Function -Name docker -Snapshot $savedDockerFunction
                    }.GetNewClosure()
                })
            $restorationSteps.Add(@{
                    Description = "staged module instances"
                    Action      = {
                        # A STAGED run imports the module siblings from the stage path, loading
                        # SECOND same-name module instances - which break Pester's -ModuleName
                        # mocking for suites later in the same process. Unload exactly the
                        # instances this run staged.
                        foreach ($stagedModule in @(Get-Module | Where-Object { $_.Path -like "$($script:work)*" })) {
                            Remove-Module -ModuleInfo $stagedModule -Force -ErrorAction SilentlyContinue
                        }
                    }
                })
            $restorationSteps.Add(@{
                    Description = "LASTEXITCODE"
                    Action      = {
                        if ($savedLastExitCode.Present) {
                            $global:LASTEXITCODE = $savedLastExitCode.Value
                        }
                        else {
                            Remove-Variable -Name LASTEXITCODE -Scope Global -Force -ErrorAction SilentlyContinue
                        }
                    }.GetNewClosure()
                })
            foreach ($name in $identityEnvironmentName) {
                $savedEnvironment = $identityEnvironmentState[$name]
                $restorationSteps.Add(@{
                        Description = "environment variable '$name'"
                        Action      = {
                            if ($savedEnvironment.Present) {
                                [System.Environment]::SetEnvironmentVariable($name, $savedEnvironment.Value)
                            }
                            else {
                                Remove-Item -LiteralPath "Env:\$name" -Force -ErrorAction SilentlyContinue
                            }
                        }.GetNewClosure()
                    })
            }
            Invoke-WiringStateRestoration -Step $restorationSteps.ToArray()
        }

        # The stand-ins must be gone; a pre-existing docker function is back; nothing of this
        # run may leak into the session.
        foreach ($shimName in @($readinessShimName, $authorityShimName, $sleepShimName)) {
            if (Get-Command $shimName -ErrorAction SilentlyContinue) {
                throw "The $shimName stand-in outlived the run."
            }
        }
        if (-not $savedDockerFunction.Present -and (Get-Command docker -CommandType Function -ErrorAction SilentlyContinue)) {
            throw "The docker stand-in outlived the run; refusing to continue with a live docker on PATH."
        }

        $after = if (Test-Path $derivedDir) { @(Get-ChildItem $derivedDir -Name -Force) } else { @() }
        $newDerived = @($after | Where-Object { -not $before.ContainsKey($_) })
        $eventList = @($events)

        return [PSCustomObject]@{
            Events          = $eventList
            AuthorityCalls  = @($authorityCalls)
            Error           = $caught
            ErrorMessage    = if ($null -ne $caught) { $caught.Exception.Message } else { $null }
            NewDerivedFiles = $newDerived
            TopologyFile    = ($newDerived | Where-Object { $_ -like "*.topology" } | Select-Object -First 1)
        }
    }

    # Stages a disposable copy of the compose root (top-level files only, dotfiles included)
    # with setup-keycloak.ps1 replaced by an inert stub, so a participating keycloak run can
    # proceed PAST its identity-provider phase to the database boundary without executing the
    # real Keycloak admin calls. The staged scripts resolve everything - modules, overlays,
    # compose files, .derived - against their own directory, so nothing touches the real root.
    function script:New-StagedComposeRoot {
        $stageRoot = Join-Path $script:work "stage-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
        foreach ($file in (Get-ChildItem -Path $script:dockerComposeRoot -File -Force)) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stageRoot $file.Name)
        }
        $stubLines = @(
            '# Inert stand-in staged by MssqlDistinctnessWiring.Tests.ps1: accepts anything,',
            '# does nothing, so the identity-provider phase completes without real Keycloak.',
            'param([Parameter(ValueFromRemainingArguments = $true)] $IgnoredArgument)',
            '$null = $IgnoredArgument',
            'return'
        )
        Set-Content -LiteralPath (Join-Path $stageRoot "setup-keycloak.ps1") -Value ($stubLines -join "`n")
        return $stageRoot
    }

    function script:New-WiringEnvFile {
        param([string[]]$AdditionalLines = @())

        $path = Join-Path $script:work ".env.distinctness-$([Guid]::NewGuid().ToString('N'))"
        $lines = @(
            'POSTGRES_PASSWORD=abcdefgh1!',
            'POSTGRES_DB_NAME=edfi_datamanagementservice',
            'DMS_CONFIG_IDENTITY_PROVIDER=self-contained'
        ) + $AdditionalLines
        Set-Content -LiteralPath $path -NoNewline -Value ($lines -join "`n")
        return $path
    }

    function script:Get-EventIndex {
        param([Parameter(Mandatory)] $Run, [Parameter(Mandatory)] [string]$Pattern)
        for ($i = 0; $i -lt $Run.Events.Count; $i++) {
            if ($Run.Events[$i] -like $Pattern) { return $i }
        }
        return -1
    }
}

# Deliberately NO file-level AfterAll over docker/alias/shim names: an unconditional deletion
# by NAME destroys caller-owned resources that merely share a name the harness uses
# (review-measured: a seeded docker function and intercept alias were gone after a green run).
# Every run's own finally removes exactly the GUID-named resources it created and restores each
# snapshot; the whole-file postcondition tests below prove the complete Pester lifecycle -
# including every AfterEach - hands the caller's session back intact.

Describe "MSSQL physical-distinctness wiring" {

    BeforeEach {
    # Ambient hermeticity over every name the fixtures declare (the resolvers give ambient
    # values Compose precedence). Presence captured separately from value; restore uses
    # Remove-Item for the absent case (SetEnvironmentVariable(name, $null) leaves a
    # present-but-blank variable in this environment).
    $script:wiringAmbientKeys = @(
        "POSTGRES_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_DB_NAME", "MSSQL_SA_PASSWORD",
        "DMS_DATASTORE", "DMS_CONFIG_DATASTORE", "DMS_CONFIG_IDENTITY_PROVIDER",
        "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING",
        "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
    )
    $script:wiringAmbientSnapshot = @{}
    foreach ($key in $script:wiringAmbientKeys) {
        $script:wiringAmbientSnapshot[$key] = @{
            Present = (Test-Path -LiteralPath "Env:\$key")
            Value   = [System.Environment]::GetEnvironmentVariable($key)
        }
        Remove-Item -LiteralPath "Env:\$key" -Force -ErrorAction SilentlyContinue
    }

    $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-distinctness-wiring-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
    $script:derivedBefore = @{}
    if (Test-Path $derivedDir) {
        foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) { $script:derivedBefore[$name] = $true }
    }
    }

    AfterEach {
    foreach ($key in $script:wiringAmbientKeys) {
        $saved = $script:wiringAmbientSnapshot[$key]
        if ($saved.Present) {
            [System.Environment]::SetEnvironmentVariable($key, $saved.Value)
        }
        else {
            Remove-Item -LiteralPath "Env:\$key" -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path -LiteralPath $script:work) {
        Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
    }
    $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
    if (Test-Path $derivedDir) {
        foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) {
            if (-not $script:derivedBefore.ContainsKey($name)) {
                Remove-Item (Join-Path $derivedDir $name) -Force -ErrorAction SilentlyContinue
            }
        }
    }
    }

    Context "start-local-dms.ps1 wiring shapes" {

    It "mssql separate -InfraOnly: calls the authority exactly once, after readiness, before anything else (contract: wiring + ordering)" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel -Because "the run must stop exactly at the boundary"

        # The authority receives the EFFECTIVE (derived, marker-carrying) environment file and
        # the same container/password the readiness wait used.
        $call = $run.AuthorityCalls[0]
        $call.ContainerName | Should -Be "dms-mssql"
        $call.SaPassword | Should -Be "abcdefgh1!"
        $call.RegisteredDatastoreDatabaseName | Should -Be ""
        $run.TopologyFile | Should -Not -BeNullOrEmpty
        $call.EnvironmentFile | Should -BeLike "*$($run.TopologyFile)"
        (ReadValuesFromEnvFile $call.EnvironmentFile)["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

        # Ordering: up db, then the readiness probe, then the authority IMMEDIATELY after it -
        # adjacency is the load-bearing half: a check relocated past OpenIddict/CMS work would
        # still satisfy a mere greater-than, but its intervening docker activity cannot satisfy
        # readiness + 1. And nothing may follow the failed check.
        $dbUpIndex = Get-EventIndex -Run $run -Pattern "docker: compose*up*db"
        $readinessIndex = Get-EventIndex -Run $run -Pattern "readiness"
        $authorityIndex = Get-EventIndex -Run $run -Pattern "authority"
        $dbUpIndex | Should -BeGreaterOrEqual 0
        $readinessIndex | Should -BeGreaterThan $dbUpIndex
        $authorityIndex | Should -Be ($readinessIndex + 1) -Because "nothing may run between the readiness wait and the check"
        @($run.Events | Select-Object -Skip ($authorityIndex + 1) | Where-Object { $_ -like "docker:*" }) |
            Should -HaveCount 0 -Because "no OpenIddict, CMS, DMS, or compose activity may follow the failed check"
    }

    It "mssql separate full start (no -InfraOnly): same single boundary call before the full up" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        $authorityIndex = Get-EventIndex -Run $run -Pattern "authority"
        @($run.Events | Select-Object -Skip ($authorityIndex + 1) | Where-Object { $_ -like "docker:*" }) | Should -HaveCount 0
    }

    It "mssql shared mode: calls the authority exactly once too - the live check is not separate-only (mutant: old separate-only outer gate kept)" {
        # The review-measured regression this flips: shared mode used to have a frozen
        # zero-invocation contract, leaving the shared topology's name relations unverified on
        # the running server. Every CMS-participating MSSQL start now reaches the authority,
        # and the effective file it receives carries the shared marker for the authority's own
        # raw-read mode selection.
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel -Because "the run must stop exactly at the boundary"
        $call = $run.AuthorityCalls[0]
        $call.ContainerName | Should -Be "dms-mssql"
        $call.RegisteredDatastoreDatabaseName | Should -Be ""
        # Measured: the shared-mode resolver leaves the marker UNDECLARED when nothing needs to
        # change (the engine-composed file already aliases the seam), and the authority's raw
        # read treats absent as shared - so the pin is that the file never declares 'true'.
        Get-Content -LiteralPath $call.EnvironmentFile -Raw | Should -Not -Match '(?m)^DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true'
        $readinessIndex = Get-EventIndex -Run $run -Pattern "readiness"
        (Get-EventIndex -Run $run -Pattern "authority") | Should -Be ($readinessIndex + 1)
        @($run.Events | Select-Object -Skip ((Get-EventIndex -Run $run -Pattern "authority") + 1) | Where-Object { $_ -like "docker:*" }) |
            Should -HaveCount 0 -Because "nothing may follow the failed check in shared mode either"
    }

    It "postgresql: ZERO authority invocations even in separate mode (engine gate), proven past the boundary region" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine postgresql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.ErrorMessage | Should -Be "WIRING-POSTBOUNDARY-SENTINEL" -Because "the run must have reached the statement after the boundary, not died earlier"
    }

    It "-DbOnly (both engines): the readiness wait is NOT followed by the authority, and the phase completes cleanly" {
        foreach ($engine in @("postgresql", "mssql")) {
            $envFile = New-WiringEnvFile
            $run = Invoke-WiringRun -ScriptBlock {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine $engine -DbOnly -EnvironmentFile $envFile *>$null
            }

            $run.AuthorityCalls | Should -HaveCount 0 -Because "engine: $engine"
            $run.Error | Should -BeNullOrEmpty -Because "engine: $engine - the database-only phase must complete"
            (Get-EventIndex -Run $run -Pattern "readiness") | Should -BeGreaterOrEqual 0 -Because "engine: $engine - the run must have passed THROUGH its readiness wait"
        }
    }

    It "-DmsOnly with -SeparateConfigDatabase: non-participating, zero authority invocations" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -DmsOnly -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
    }

    It "teardown (-d -v): zero authority invocations" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -d -v *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
    }

    It "keycloak identity: Keycloak work precedes the database and is not gated; the authority is not consulted for it" {
        # The keycloak container start fails at the compose boundary here (it is not the db-up
        # call), which proves the authority was not consulted for identity-provider work.
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -IdentityProvider keycloak -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.ErrorMessage | Should -BeLike "*Failed to start Keycloak*"
    }

    It "keycloak separate mode reaches the authority (staged sibling stub): a future identity-provider gate cannot slip in" {
        # Staged copy: setup-keycloak.ps1 is an inert stub, the keycloak-up compose call is
        # allowed to succeed, so the run proceeds through its whole identity-provider phase to
        # the database boundary - and must still hit the authority exactly once.
        $stage = New-StagedComposeRoot
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -AllowKeycloakUp -ScriptBlock {
            & "$stage/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -IdentityProvider keycloak -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        $readinessIndex = Get-EventIndex -Run $run -Pattern "readiness"
        (Get-EventIndex -Run $run -Pattern "authority") | Should -Be ($readinessIndex + 1)
    }

    It "keycloak shared mode reaches the authority too (staged sibling stub): the symmetric half of the shared-mode flip" {
        $stage = New-StagedComposeRoot
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -AllowKeycloakUp -ScriptBlock {
            & "$stage/start-local-dms.ps1" -DatabaseEngine mssql -IdentityProvider keycloak -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        Get-Content -LiteralPath $run.AuthorityCalls[0].EnvironmentFile -Raw | Should -Not -Match '(?m)^DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true'
    }
    }

    Context "start-published-dms.ps1 wiring shapes" {

    It "mssql separate full with -DataStoreDatabaseName: passes the PROVIDER-PARSED registered value, exactly once" {
        # The raw parameter carries a bare trailing LINE FEED, which the connection-string
        # transport removes: the authority must receive the parsed value, never the raw text.
        $envFile = New-WiringEnvFile
        $rawRegisteredName = "edfi_probe_datastore`n"
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -DataStoreDatabaseName $rawRegisteredName *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        $run.AuthorityCalls[0].RegisteredDatastoreDatabaseName | Should -Be "edfi_probe_datastore"
        $run.AuthorityCalls[0].ContainerName | Should -Be "dms-mssql"
    }

    It "mssql separate full without the parameter: registered candidate stays empty" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.AuthorityCalls[0].RegisteredDatastoreDatabaseName | Should -Be ""
    }

    It "mssql separate -InfraOnly with -DataStoreDatabaseName: the parameter is inert (registration never runs), so no registered candidate" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly -DataStoreDatabaseName "edfi_probe_datastore" *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.AuthorityCalls[0].RegisteredDatastoreDatabaseName | Should -Be ""
    }

    It "mssql separate -NoDataStore with -DataStoreDatabaseName: registration is skipped, so no registered candidate" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -NoDataStore -DataStoreDatabaseName "edfi_probe_datastore" *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.AuthorityCalls[0].RegisteredDatastoreDatabaseName | Should -Be ""
    }

    It "bare keycloak (CMS not in the compose set): zero authority invocations" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -IdentityProvider keycloak -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.ErrorMessage | Should -BeLike "*Failed to start Keycloak*"
    }

    It "-DbOnly mssql: readiness runs, the authority does not, the phase completes cleanly" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -DbOnly -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.Error | Should -BeNullOrEmpty
        (Get-EventIndex -Run $run -Pattern "readiness") | Should -BeGreaterOrEqual 0
    }

    It "teardown (-d): zero authority invocations" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -d *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
    }

    It "postgresql: ZERO authority invocations even in separate mode, proven past the boundary region" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine postgresql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.ErrorMessage | Should -Be "WIRING-POSTBOUNDARY-SENTINEL"
    }

    It "mssql shared self-contained: calls the authority exactly once too - the live check is not separate-only (mutant: old separate-only outer gate kept)" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        $call = $run.AuthorityCalls[0]
        $call.RegisteredDatastoreDatabaseName | Should -Be ""
        Get-Content -LiteralPath $call.EnvironmentFile -Raw | Should -Not -Match '(?m)^DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true'
        $readinessIndex = Get-EventIndex -Run $run -Pattern "readiness"
        (Get-EventIndex -Run $run -Pattern "authority") | Should -Be ($readinessIndex + 1)
    }

    It "mssql shared full with -DataStoreDatabaseName: the parsed registered value is still handed over (the authority applies it only where it participates)" {
        $envFile = New-WiringEnvFile
        $rawRegisteredName = "edfi_probe_datastore`n"
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -DataStoreDatabaseName $rawRegisteredName *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.AuthorityCalls[0].RegisteredDatastoreDatabaseName | Should -Be "edfi_probe_datastore"
    }

    It "-DmsOnly with -SeparateConfigDatabase: non-participating, zero authority invocations" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -DmsOnly -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
    }

    It "keycloak separate mode reaches the authority (staged sibling stub): the compose-set participation includes it" {
        $stage = New-StagedComposeRoot
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -AllowKeycloakUp -ScriptBlock {
            & "$stage/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -IdentityProvider keycloak -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        $readinessIndex = Get-EventIndex -Run $run -Pattern "readiness"
        (Get-EventIndex -Run $run -Pattern "authority") | Should -Be ($readinessIndex + 1)
    }
    }

    Context "structural pins" {

    BeforeAll {
        $script:localText = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-local-dms.ps1") -Raw
        $script:publishedText = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-published-dms.ps1") -Raw
    }

    It "each script calls the authority exactly once, inside the mssql readiness block, gated on CMS participation alone" {
        foreach ($scriptText in @($script:localText, $script:publishedText)) {
            # Line-anchored: the CALL starts its own line; the participation comments also name
            # the authority, and comments are not invocations.
            $callMatches = [regex]::Matches($scriptText, '(?m)^\s*Assert-MssqlTopologyPhysicalConsistency\b')
            $callMatches.Count | Should -Be 1

            # Region: the main-flow mssql readiness block. The call must sit after the readiness
            # wait and before the identity/OpenIddict parameter construction that follows it,
            # and the gate must require CMS participation ONLY - the topology mode never gates
            # the invocation (shared and separate both verify live; the authority selects the
            # semantics from the file's own raw marker).
            $callIndex = $callMatches[0].Index
            $waitIndex = $scriptText.LastIndexOf("Wait-MssqlReady -ContainerName", $callIndex)
            $gateIndex = $scriptText.LastIndexOf('if ($cmsParticipates) {', $callIndex)
            $identityIndex = $scriptText.IndexOf('$identityDbParams =', $callIndex)
            $waitIndex | Should -BeGreaterThan 0
            $gateIndex | Should -BeGreaterThan $waitIndex
            $identityIndex | Should -BeGreaterThan $callIndex

            # The deleted mode-reading gate helper must never come back at the call sites.
            $scriptText | Should -Not -Match "Test-CmsSeparateTopologyDeclared"
        }
    }

    It "only the published script computes a registered candidate, and only from the provider-parsed value" {
        $script:publishedText | Should -Match ([regex]::Escape('Get-RegisteredDatastoreDatabaseValue -DatastoreDatabaseName $DataStoreDatabaseName'))
        $script:localText | Should -Not -Match "RegisteredDatastoreDatabaseName"
        # The parsed value - never the raw parameter - reaches the authority.
        $script:publishedText | Should -Match ([regex]::Escape('-RegisteredDatastoreDatabaseName $registeredDatastoreDatabaseValue'))
        $script:publishedText | Should -Not -Match ([regex]::Escape('-RegisteredDatastoreDatabaseName $DataStoreDatabaseName'))
    }
    }

    Context "harness safety (the interception itself)" {

    BeforeEach {
        # These tests seed the very resources the harness intercepts, so they snapshot and
        # exactly restore them THEMSELVES - a safety test that blindly deletes by name would
        # commit the same offense it exists to prevent.
        $script:safetySnapshots = @{
            DockerFunction = Get-CommandStateSnapshot -ItemPath "Function:\docker"
            DockerAlias    = Get-CommandStateSnapshot -ItemPath "Alias:\docker"
            InterceptAlias = Get-CommandStateSnapshot -ItemPath "Alias:\Test-NativeCommandWithTimeout"
        }
        $exitVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        $script:safetyExitSnapshot = @{
            Present = ($null -ne $exitVariable)
            Value   = if ($null -ne $exitVariable) { $exitVariable.Value } else { $null }
        }
    }

    AfterEach {
        # Same central restoration implementation as the harness itself - the safety tests must
        # not maintain a second, lower-fidelity copy (review-measured: a separate AfterEach lost
        # alias options and descriptions the harness preserved).
        $restoreCommand = ${function:Restore-CommandFromSnapshot}
        $steps = [System.Collections.Generic.List[object]]::new()
        $dockerFunctionSnapshot = $script:safetySnapshots.DockerFunction
        $steps.Add(@{ Description = "docker function"; Action = {
                    & $restoreCommand -Kind Function -Name docker -Snapshot $dockerFunctionSnapshot
                }.GetNewClosure() })
        $dockerAliasSnapshot = $script:safetySnapshots.DockerAlias
        $steps.Add(@{ Description = "docker alias"; Action = {
                    & $restoreCommand -Kind Alias -Name docker -Snapshot $dockerAliasSnapshot
                }.GetNewClosure() })
        $interceptAliasSnapshot = $script:safetySnapshots.InterceptAlias
        $steps.Add(@{ Description = "intercept alias"; Action = {
                    & $restoreCommand -Kind Alias -Name Test-NativeCommandWithTimeout -Snapshot $interceptAliasSnapshot
                }.GetNewClosure() })
        $exitSnapshot = $script:safetyExitSnapshot
        $steps.Add(@{ Description = "LASTEXITCODE"; Action = {
                    if ($exitSnapshot.Present) { $global:LASTEXITCODE = $exitSnapshot.Value }
                    else { Remove-Variable -Name LASTEXITCODE -Scope Global -Force -ErrorAction SilentlyContinue }
                }.GetNewClosure() })
        Invoke-WiringStateRestoration -Step $steps.ToArray()
    }

    It "refuses to run when an effective docker alias exists, without invoking its target" {
        # An alias outranks a function stand-in; a hostile session alias pointing at real
        # Docker would let the scripts reach it. The harness must refuse BEFORE staging
        # anything, and must not touch the alias.
        $recorderName = "__WiringAliasBypassRecorder_$([Guid]::NewGuid().ToString('N'))"
        $bypassInvocations = [System.Collections.Generic.List[string]]::new()
        Set-Item -Path "Function:\global:$recorderName" -Value { $bypassInvocations.Add("hit") }.GetNewClosure()
        Set-Alias -Name docker -Value $recorderName -Scope Global
        try {
            $envFile = New-WiringEnvFile
            { Invoke-WiringRun -ScriptBlock {
                    & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
                } } | Should -Throw "*effective 'docker' alias*"
            $bypassInvocations | Should -HaveCount 0 -Because "nothing may be invoked through the bypassing alias"
            (Get-Item Alias:\docker).Definition | Should -Be $recorderName -Because "the pre-existing alias must be left untouched"
        }
        finally {
            Remove-Item "Function:\$recorderName" -Force -ErrorAction SilentlyContinue
        }
    }

    It "restores hostile pre-existing state exactly: docker function (body and options), intercept alias, and LASTEXITCODE presence + value" {
        $preexistingDockerBody = { 'wiring-preexisting-docker-sentinel' }
        $preexistingTargetName = "__WiringPreexistingTarget_$([Guid]::NewGuid().ToString('N'))"
        Set-Item -Path Function:\global:docker -Force -Value $preexistingDockerBody
        Set-Item -Path "Function:\global:$preexistingTargetName" -Value { return $true }
        Set-Alias -Name Test-NativeCommandWithTimeout -Value $preexistingTargetName -Scope Global -Force
        $global:LASTEXITCODE = 37
        try {
            $envFile = New-WiringEnvFile
            $run = Invoke-WiringRun -ScriptBlock {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }

            # The run itself must still have worked end to end under the hostile state.
            $run.AuthorityCalls | Should -HaveCount 1
            $run.ErrorMessage | Should -Be $script:authoritySentinel

            # Exact restoration afterwards - options asserted, not merely definitions.
            (Get-Item Function:\docker).ScriptBlock.ToString() | Should -Be $preexistingDockerBody.ToString()
            (Get-Item Function:\docker).Options | Should -Be ([System.Management.Automation.ScopedItemOptions]::None)
            (Get-Item Alias:\Test-NativeCommandWithTimeout).Definition | Should -Be $preexistingTargetName
            (Get-Variable -Name LASTEXITCODE -Scope Global).Value | Should -Be 37
            @(Get-Command "__WiringReadinessShim_*", "__WiringAuthorityShim_*", "__WiringSleepShim_*" -ErrorAction SilentlyContinue) |
                Should -HaveCount 0 -Because "only resources this invocation created may be removed, and all of them must be"
        }
        finally {
            Remove-Item "Function:\$preexistingTargetName" -Force -ErrorAction SilentlyContinue
        }
    }

    It "restores a caller-owned intercept alias with its definition, DESCRIPTION, and ReadOnly OPTION exactly (review-measured fidelity)" {
        $targetName = "__WiringAliasFidelityTarget_$([Guid]::NewGuid().ToString('N'))"
        Set-Item -Path "Function:\global:$targetName" -Value { return $true }
        Set-Alias -Name Test-NativeCommandWithTimeout -Value $targetName -Scope Global -Force `
            -Description "outer-alias-description" -Option ReadOnly
        try {
            $envFile = New-WiringEnvFile
            $run = Invoke-WiringRun -ScriptBlock {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }
            $run.AuthorityCalls | Should -HaveCount 1

            $restored = Get-Item Alias:\Test-NativeCommandWithTimeout
            $restored.Definition | Should -Be $targetName
            $restored.Description | Should -Be "outer-alias-description" -Because "a restoration that stops at the definition silently empties the description"
            ($restored.Options -band [System.Management.Automation.ScopedItemOptions]::ReadOnly) |
                Should -Be ([System.Management.Automation.ScopedItemOptions]::ReadOnly) -Because "options are caller state too"
        }
        finally {
            Remove-Item "Function:\$targetName" -Force -ErrorAction SilentlyContinue
        }
    }

    It "supports a ReadOnly docker function: shadowed with -Force, restored with body AND the ReadOnly option (measured contract)" {
        $preexistingDockerBody = { 'wiring-readonly-docker-sentinel' }
        Set-Item -Path Function:\global:docker -Force -Value $preexistingDockerBody
        (Get-Item Function:\docker).Options = [System.Management.Automation.ScopedItemOptions]::ReadOnly

        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $restored = Get-Item Function:\docker
        $restored.ScriptBlock.ToString() | Should -Be $preexistingDockerBody.ToString()
        ($restored.Options -band [System.Management.Automation.ScopedItemOptions]::ReadOnly) |
            Should -Be ([System.Management.Automation.ScopedItemOptions]::ReadOnly)
    }

    It "refuses an AllScope docker function BEFORE any mutation (measured: it cannot be shadowed, only destroyed)" {
        # Scope-local so the AllScope function vanishes with this It's scope either way.
        Set-Item -Path Function:\docker -Value { 'wiring-allscope-docker-sentinel' } -Options AllScope
        $envFile = New-WiringEnvFile
        { Invoke-WiringRun -ScriptBlock {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            } } | Should -Throw "*'docker' function carries option*"
        $survivor = Get-Item Function:\docker
        $survivor.ScriptBlock.ToString() | Should -Match "wiring-allscope-docker-sentinel"
        ($survivor.Options -band [System.Management.Automation.ScopedItemOptions]::AllScope) |
            Should -Be ([System.Management.Automation.ScopedItemOptions]::AllScope)
    }

    It "refuses a Constant docker function BEFORE any mutation" {
        # Scope-local: a Constant function cannot be removed for the life of its scope, so it
        # must live in this It's scope, which ends with the test.
        Set-Item -Path Function:\docker -Value { 'wiring-constant-docker-sentinel' } -Options Constant
        $envFile = New-WiringEnvFile
        { Invoke-WiringRun -ScriptBlock {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            } } | Should -Throw "*'docker' function carries option*"
        (Get-Item Function:\docker).ScriptBlock.ToString() | Should -Match "wiring-constant-docker-sentinel"
    }

    It "restores LASTEXITCODE ABSENCE by removal, never by creating a null-valued variable" {
        Remove-Variable -Name LASTEXITCODE -Scope Global -Force -ErrorAction SilentlyContinue
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
        }
        $run.AuthorityCalls | Should -HaveCount 1
        (Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue) |
            Should -BeNullOrEmpty -Because "presence and value are separate state; absence restores as absence"
    }

    It "keeps restoring the remaining state when one restoration step fails, then reports the failure once" {
        $firstRan = [System.Collections.Generic.List[string]]::new()
        $thirdRan = [System.Collections.Generic.List[string]]::new()
        $steps = @(
            @{ Description = "first"; Action = { $firstRan.Add("yes") }.GetNewClosure() }
            @{ Description = "poisoned"; Action = { throw "deliberate restoration failure" } }
            @{ Description = "third"; Action = { $thirdRan.Add("yes") }.GetNewClosure() }
        )
        { Invoke-WiringStateRestoration -Step $steps } | Should -Throw "*poisoned: deliberate restoration failure*"
        $firstRan | Should -HaveCount 1
        $thirdRan | Should -HaveCount 1 -Because "a failed step must never prevent the steps after it"
    }

    It "hands the caller's session back intact after the COMPLETE file lifecycle, hostile seeded state (post-AfterAll, isolated child)" -Tag "WholeFileSafety" {
        # A green in-file assertion proves nothing about the file's own AfterEach/AfterAll -
        # only inspecting the session AFTER Invoke-Pester returns does. The child excludes this
        # tag, so there is no recursion.
        # LASTEXITCODE caveat, measured: Invoke-Pester ITSELF assigns FailedCount to
        # $global:LASTEXITCODE after every run (a trivial one-It control file turns a seeded 37
        # into 0, and creates the variable when absent). That is the RUNNER's contract, outside
        # this file's control - so the postcondition is runner-relative: after the wiring file,
        # LASTEXITCODE must equal exactly what the trivial control leaves behind.
        $childScript = Join-Path $script:work "wholefile-seeded.ps1"
        $controlFile = Join-Path $script:work "wholefile-control.Tests.ps1"
        'Describe "control" { It "passes" { 1 | Should -Be 1 } }' | Set-Content -LiteralPath $controlFile
        @(
            "`$global:LASTEXITCODE = 37",
            "`$null = Invoke-Pester -Path '$controlFile' -Output None -PassThru",
            "`$controlExit = (Get-Variable -Name LASTEXITCODE -Scope Global).Value",
            "Set-Item -Path Function:\global:docker -Value { 'wholefile-docker-sentinel' }",
            "Set-Item -Path Function:\global:__WholeFileTarget -Value { return `$true }",
            "Set-Alias -Name Test-NativeCommandWithTimeout -Value __WholeFileTarget -Scope Global -Description 'outer-alias-description' -Option ReadOnly",
            "`$global:LASTEXITCODE = 37",
            "`$result = Invoke-Pester -Path '$PSCommandPath' -ExcludeTagFilter 'WholeFileSafety' -Output None -PassThru",
            "`$fn = Get-Item Function:\docker -ErrorAction SilentlyContinue",
            "`$alias = Get-Item Alias:\Test-NativeCommandWithTimeout -ErrorAction SilentlyContinue",
            "`$exitVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue",
            "[pscustomobject]@{",
            "    Failed = `$result.FailedCount; Total = `$result.TotalCount; ControlExit = `$controlExit",
            "    DockerBodyMatch = (`$null -ne `$fn -and `$fn.ScriptBlock.ToString().Contains('wholefile-docker-sentinel'))",
            "    DockerOptions = if (`$fn) { [string]`$fn.Options } else { 'absent' }",
            "    AliasDefinition = if (`$alias) { [string]`$alias.Definition } else { 'absent' }",
            "    AliasOptions = if (`$alias) { [string]`$alias.Options } else { 'absent' }",
            "    AliasDescription = if (`$alias) { [string]`$alias.Description } else { 'absent' }",
            "    ExitPresent = (`$null -ne `$exitVariable)",
            "    ExitValue = if (`$exitVariable) { `$exitVariable.Value } else { `$null }",
            "} | ConvertTo-Json -Compress"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ([Environment]::ProcessPath) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        $childState.Failed | Should -Be 0 -Because "the suite must pass under the hostile state"
        $childState.Total | Should -BeGreaterThan 20
        $childState.DockerBodyMatch | Should -BeTrue -Because "the caller's docker function must survive the whole lifecycle"
        $childState.DockerOptions | Should -Be "None"
        $childState.AliasDefinition | Should -Be "__WholeFileTarget"
        $childState.AliasOptions | Should -Be "ReadOnly" -Because "a non-default alias option must survive the whole lifecycle"
        $childState.AliasDescription | Should -Be "outer-alias-description" -Because "the description is caller state too"
        $childState.ExitPresent | Should -BeTrue
        $childState.ExitValue | Should -Be $childState.ControlExit -Because "the file may leave exactly what the Pester runner itself leaves, and nothing else"
    }

    It "creates nothing in a clean session across the COMPLETE file lifecycle (post-AfterAll, isolated child)" -Tag "WholeFileSafety" {
        # Same runner-relative LASTEXITCODE contract as above: Invoke-Pester itself creates the
        # variable in a fresh session (measured on a trivial control), so the assertion is that
        # this file leaves exactly the runner's own footprint and nothing more.
        $childScript = Join-Path $script:work "wholefile-clean.ps1"
        $controlFile = Join-Path $script:work "wholefile-clean-control.Tests.ps1"
        'Describe "control" { It "passes" { 1 | Should -Be 1 } }' | Set-Content -LiteralPath $controlFile
        @(
            "`$null = Invoke-Pester -Path '$controlFile' -Output None -PassThru",
            "# Scalars captured IMMEDIATELY: a PSVariable object is live, and a later",
            "# Remove-Variable inside the suite detaches it, nulling its .Value.",
            "`$controlExitVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue",
            "`$controlExitPresent = (`$null -ne `$controlExitVariable)",
            "`$controlExitValue = if (`$controlExitPresent) { `$controlExitVariable.Value } else { `$null }",
            "`$result = Invoke-Pester -Path '$PSCommandPath' -ExcludeTagFilter 'WholeFileSafety' -Output None -PassThru",
            "`$fn = Get-Item Function:\docker -ErrorAction SilentlyContinue",
            "`$alias = Get-Item Alias:\Test-NativeCommandWithTimeout -ErrorAction SilentlyContinue",
            "`$exitVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue",
            "[pscustomobject]@{",
            "    Failed = `$result.FailedCount; Total = `$result.TotalCount",
            "    ControlExitPresent = `$controlExitPresent",
            "    ControlExitValue = `$controlExitValue",
            "    DockerPresent = (`$null -ne `$fn); AliasPresent = (`$null -ne `$alias)",
            "    ExitPresent = (`$null -ne `$exitVariable)",
            "    ExitValue = if (`$exitVariable) { `$exitVariable.Value } else { `$null }",
            "} | ConvertTo-Json -Compress"
        ) -join "`n" | Set-Content -LiteralPath $childScript

        $childState = (& ([Environment]::ProcessPath) -NoProfile -File $childScript | Select-Object -Last 1) | ConvertFrom-Json
        $childState.Failed | Should -Be 0
        $childState.DockerPresent | Should -BeFalse
        $childState.AliasPresent | Should -BeFalse
        $childState.ExitPresent | Should -Be $childState.ControlExitPresent -Because "the file may leave exactly the runner's own LASTEXITCODE footprint"
        $childState.ExitValue | Should -Be $childState.ControlExitValue
    }
    }

    Context "raw-marker invocation invariance" {
    # The marker no longer gates WHETHER the authority runs - it selects the semantics INSIDE
    # the authority (unit-tested there, including the raw-read spelling matrix). At the wiring
    # level the matched ambient pair pins the remaining invariant: an ambient marker can change
    # neither the fact nor the count of the invocation in either direction.

    It "ambient marker 'true' cannot change the shared-mode file's single invocation" {
        $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
        }
        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        Get-Content -LiteralPath $run.AuthorityCalls[0].EnvironmentFile -Raw | Should -Not -Match '(?m)^DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true'
    }

    It "ambient marker 'false' cannot suppress the authority for a separate effective file" {
        $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "false"
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
        }
        $run.AuthorityCalls | Should -HaveCount 1
        $run.ErrorMessage | Should -Be $script:authoritySentinel
        (ReadValuesFromEnvFile $run.AuthorityCalls[0].EnvironmentFile)["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"
    }
    }
}
