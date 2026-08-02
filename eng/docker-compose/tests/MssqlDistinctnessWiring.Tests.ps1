# Wiring, gating, and ordering coverage for the server-backed MSSQL physical-identity check in
# start-local-dms.ps1 and start-published-dms.ps1.
#
# The harness runs the REAL scripts and intercepts three seams, portably and with no PATH files:
#
# - `docker` is a global FUNCTION stand-in (the established pattern: functions beat external
#   commands, and lookup walks into `&`-invoked scripts). Its policy lets exactly the
#   `compose ... up ... db` call SUCCEED - the boundary under test lies past it - and fails
#   every other compose subcommand, so each run ends at a deterministic, assertable point.
# - Test-NativeCommandWithTimeout and Assert-MssqlPhysicalDatastoreDistinctness are intercepted
#   with global ALIASES over recording shims. An alias is required, not a function: the scripts
#   re-run Import-Module -Force, which re-registers same-named FUNCTIONS in the global table,
#   but aliases outrank functions in command precedence and imports do not touch them
#   (spike-verified). The readiness shim makes Wait-MssqlReady/-PostgresqlReady succeed
#   instantly; the authority shim records its bound arguments and THROWS a sentinel, so every
#   positive run stops exactly at the boundary and nothing downstream (OpenIddict, CMS, DMS,
#   registration) can execute.
#
# Every event (docker argv, readiness probe, authority call) lands in one ordered list, which is
# what the ordering assertions read. The authority's own behavior (marker no-op, transport,
# verdicts) is unit-tested in MssqlPhysicalDistinctnessAuthority.Tests.ps1; here the shim
# records what the SCRIPTS hand it.

# The authority shim must mirror the real parameter surface, including -SaPassword, to bind the
# scripts' named arguments; the real parameter's plaintext trade-off is documented on the
# authority itself, and the stand-in only records the value for assertions. Scriptblock-level
# suppression attributes are not honored by the analyzer, so the suppression lives here.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Test stand-in mirrors the real authority parameter surface to bind named arguments; see the suppression on Assert-MssqlPhysicalDatastoreDistinctness.')]
param()

BeforeAll {
    $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

    $script:authoritySentinel = "WIRING-AUTHORITY-SENTINEL"

    function script:Invoke-WiringRun {
        param(
            [Parameter(Mandatory)] [scriptblock]$ScriptBlock,
            # Lets `compose ... up ... keycloak` succeed too, for the staged participating
            # keycloak rows; the default policy succeeds ONLY the db-up call.
            [switch]$AllowKeycloakUp
        )

        # ---- Transactional interception: reject what cannot be shadowed or restored, snapshot
        # ---- everything this run will touch, stage GUID-named shims, and in finally remove
        # ---- ONLY what this invocation created and put back everything that pre-existed.
        $constantOption = [System.Management.Automation.ScopedItemOptions]::Constant

        # An effective `docker` ALIAS outranks any function stand-in: with one in place the
        # scripts' docker calls would resolve through it - to real Docker, if that is its
        # target. Refuse before staging anything.
        if (Get-Command docker -CommandType Alias -ErrorAction SilentlyContinue) {
            throw "Refusing to run: an effective 'docker' alias is defined in this session and would outrank the function stand-in, so the scripts could reach real Docker."
        }
        $interceptedAliasNames = @('Test-NativeCommandWithTimeout', 'Assert-MssqlPhysicalDatastoreDistinctness', 'Start-Sleep')
        $savedAliases = @{}
        foreach ($aliasName in $interceptedAliasNames) {
            $existingAlias = Get-Item "Alias:\$aliasName" -ErrorAction SilentlyContinue
            if ($null -ne $existingAlias) {
                if ($existingAlias.Options -band $constantOption) {
                    throw "Refusing to run: a constant '$aliasName' alias cannot be shadowed or restored."
                }
                $savedAliases[$aliasName] = @{ Definition = $existingAlias.Definition; Options = $existingAlias.Options }
            }
            else {
                $savedAliases[$aliasName] = $null
            }
        }
        $savedDockerFunction = $null
        $existingDockerFunction = Get-Item Function:\docker -ErrorAction SilentlyContinue
        if ($null -ne $existingDockerFunction) {
            if ($existingDockerFunction.Options -band $constantOption) {
                throw "Refusing to run: a constant 'docker' function cannot be shadowed or restored."
            }
            $savedDockerFunction = @{ ScriptBlock = $existingDockerFunction.ScriptBlock; Options = $existingDockerFunction.Options }
        }
        $savedLastExitCode = $global:LASTEXITCODE

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
            Set-Alias -Name Assert-MssqlPhysicalDatastoreDistinctness -Value $authorityShimName -Scope Global -Force

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
            # Remove ONLY what this invocation created, then restore what pre-existed, exactly.
            foreach ($aliasName in $interceptedAliasNames) {
                Remove-Item "Alias:\$aliasName" -Force -ErrorAction SilentlyContinue
                if ($null -ne $savedAliases[$aliasName]) {
                    Set-Alias -Name $aliasName -Value $savedAliases[$aliasName].Definition -Scope Global -Force
                    (Get-Item "Alias:\$aliasName").Options = $savedAliases[$aliasName].Options
                }
            }
            Remove-Item "Function:\$readinessShimName" -Force -ErrorAction SilentlyContinue
            Remove-Item "Function:\$authorityShimName" -Force -ErrorAction SilentlyContinue
            Remove-Item "Function:\$sleepShimName" -Force -ErrorAction SilentlyContinue
            Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue

            # A STAGED run imports the module siblings from the stage path, which loads SECOND
            # same-name module instances alongside the real ones - and duplicate module names
            # break Pester's -ModuleName mocking for every suite that runs later in the same
            # process (measured: 'Multiple script or manifest modules named env-utility are
            # currently loaded'). Unload exactly the instances this run staged.
            foreach ($stagedModule in @(Get-Module | Where-Object { $_.Path -like "$($script:work)*" })) {
                Remove-Module -ModuleInfo $stagedModule -Force -ErrorAction SilentlyContinue
            }
            if ($null -ne $savedDockerFunction) {
                Set-Item -Path Function:\global:docker -Force -Value $savedDockerFunction.ScriptBlock
                (Get-Item Function:\docker).Options = $savedDockerFunction.Options
            }
            $global:LASTEXITCODE = $savedLastExitCode

            foreach ($name in $identityEnvironmentName) {
                $saved = $identityEnvironmentState[$name]
                if ($saved.Present) {
                    [System.Environment]::SetEnvironmentVariable($name, $saved.Value)
                }
                else {
                    Remove-Item -LiteralPath "Env:\$name" -Force -ErrorAction SilentlyContinue
                }
            }
        }

        # The stand-ins must be gone; a pre-existing docker function is back; nothing of this
        # run may leak into the session.
        foreach ($shimName in @($readinessShimName, $authorityShimName, $sleepShimName)) {
            if (Get-Command $shimName -ErrorAction SilentlyContinue) {
                throw "The $shimName stand-in outlived the run."
            }
        }
        if ($null -eq $savedDockerFunction -and (Get-Command docker -CommandType Function -ErrorAction SilentlyContinue)) {
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

AfterAll {
    # Defensive: Invoke-WiringRun removes these itself, including on failure.
    Remove-Item Alias:\Assert-MssqlPhysicalDatastoreDistinctness -Force -ErrorAction SilentlyContinue
    Remove-Item Alias:\Test-NativeCommandWithTimeout -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\__WiringAuthorityShim -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\__WiringReadinessShim -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
}

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

    It "mssql shared mode: ZERO authority invocations, proven by a run that passes THROUGH the boundary region" {
        # The post-boundary sentinel (the sleep shim) fires only after the gate decision, so
        # reaching it with zero authority calls proves the shared-mode run genuinely traversed
        # the boundary rather than dying earlier for an unrelated reason.
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        (Get-EventIndex -Run $run -Pattern "readiness") | Should -BeGreaterOrEqual 0 -Because "the run must have passed the readiness wait"
        $run.ErrorMessage | Should -Be "WIRING-POSTBOUNDARY-SENTINEL"
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

    It "mssql shared self-contained: ZERO authority invocations, proven past the boundary region" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        (Get-EventIndex -Run $run -Pattern "readiness") | Should -BeGreaterOrEqual 0
        $run.ErrorMessage | Should -Be "WIRING-POSTBOUNDARY-SENTINEL"
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

    It "each script calls the authority exactly once, inside the mssql readiness block, gated on participating separate topology" {
        foreach ($scriptText in @($script:localText, $script:publishedText)) {
            [regex]::Matches($scriptText, [regex]::Escape("Assert-MssqlPhysicalDatastoreDistinctness")).Count | Should -Be 1

            # Region: the main-flow mssql readiness block. The call must sit after the readiness
            # wait and before the identity/OpenIddict parameter construction that follows it,
            # and the gate must require BOTH CMS participation and the declared separate
            # topology - shared mode has a frozen zero-invocation contract.
            $callIndex = $scriptText.IndexOf("Assert-MssqlPhysicalDatastoreDistinctness")
            $waitIndex = $scriptText.LastIndexOf("Wait-MssqlReady -ContainerName", $callIndex)
            $gateIndex = $scriptText.LastIndexOf('if ($cmsParticipates -and (Test-CmsSeparateTopologyDeclared -EnvironmentFile $EnvironmentFile))', $callIndex)
            $identityIndex = $scriptText.IndexOf('$identityDbParams =', $callIndex)
            $waitIndex | Should -BeGreaterThan 0
            $gateIndex | Should -BeGreaterThan $waitIndex
            $identityIndex | Should -BeGreaterThan $callIndex
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
            Remove-Item Alias:\docker -Force -ErrorAction SilentlyContinue
            Remove-Item "Function:\$recorderName" -Force -ErrorAction SilentlyContinue
        }
    }

    It "restores hostile pre-existing state exactly: docker function (body and options), intercept alias, and LASTEXITCODE" {
        $preexistingDockerBody = { 'wiring-preexisting-docker-sentinel' }
        $preexistingTargetName = "__WiringPreexistingTarget_$([Guid]::NewGuid().ToString('N'))"
        Set-Item -Path Function:\global:docker -Value $preexistingDockerBody
        Set-Item -Path "Function:\global:$preexistingTargetName" -Value { return $true }
        Set-Alias -Name Test-NativeCommandWithTimeout -Value $preexistingTargetName -Scope Global
        $global:LASTEXITCODE = 37
        try {
            $envFile = New-WiringEnvFile
            $run = Invoke-WiringRun -ScriptBlock {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }

            # The run itself must still have worked end to end under the hostile state.
            $run.AuthorityCalls | Should -HaveCount 1
            $run.ErrorMessage | Should -Be $script:authoritySentinel

            # Exact restoration afterwards.
            (Get-Item Function:\docker).ScriptBlock.ToString() | Should -Be $preexistingDockerBody.ToString()
            (Get-Item Alias:\Test-NativeCommandWithTimeout).Definition | Should -Be $preexistingTargetName
            $global:LASTEXITCODE | Should -Be 37
            @(Get-Command "__WiringReadinessShim_*", "__WiringAuthorityShim_*", "__WiringSleepShim_*" -ErrorAction SilentlyContinue) |
                Should -HaveCount 0 -Because "only resources this invocation created may be removed, and all of them must be"
        }
        finally {
            Remove-Item Alias:\Test-NativeCommandWithTimeout -Force -ErrorAction SilentlyContinue
            Remove-Item "Function:\$preexistingTargetName" -Force -ErrorAction SilentlyContinue
            Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
        }
    }
    }
}
