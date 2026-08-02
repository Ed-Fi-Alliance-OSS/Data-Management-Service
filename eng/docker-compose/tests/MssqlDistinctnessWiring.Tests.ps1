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
            # When set, `compose ... up ... db` fails like every other compose call - used to
            # prove non-mssql runs never reach the authority even at their earliest boundary.
            [switch]$FailDbUp
        )

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
        $allowDbUp = -not $FailDbUp
        $hadRealDocker = $null -ne (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)
        $caught = $null
        try {
            Set-Item -Path Function:\global:docker -Value {
                $flattened = @($args | ForEach-Object { $_ })
                $joined = $flattened -join " "
                $events.Add("docker: $joined")
                if ($flattened.Count -gt 0 -and $flattened[0] -eq "compose") {
                    $isDbUp = ($joined -match " up ") -and ($flattened[-1] -eq "db")
                    $global:LASTEXITCODE = if ($isDbUp -and $allowDbUp) { 0 } else { 1 }
                }
                else {
                    $global:LASTEXITCODE = 0
                }
            }.GetNewClosure()

            Set-Item -Path Function:\global:__WiringReadinessShim -Value {
                # The parameters exist only to bind the readiness probe's named arguments; the
                # shim answers "ready" unconditionally. The $null assignment consumes them,
                # which is what the analyzer's unused-parameter rule wants stated explicitly.
                param($FilePath, $ArgumentList, $TimeoutSeconds)
                $null = $FilePath, $ArgumentList, $TimeoutSeconds
                $events.Add("readiness")
                return $true
            }.GetNewClosure()
            Set-Alias -Name Test-NativeCommandWithTimeout -Value __WiringReadinessShim -Scope Global

            Set-Item -Path Function:\global:__WiringAuthorityShim -Value {
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
            Set-Alias -Name Assert-MssqlPhysicalDatastoreDistinctness -Value __WiringAuthorityShim -Scope Global

            & $ScriptBlock
        }
        catch {
            $caught = $_
        }
        finally {
            Remove-Item Alias:\Assert-MssqlPhysicalDatastoreDistinctness -Force -ErrorAction SilentlyContinue
            Remove-Item Alias:\Test-NativeCommandWithTimeout -Force -ErrorAction SilentlyContinue
            Remove-Item Function:\__WiringAuthorityShim -Force -ErrorAction SilentlyContinue
            Remove-Item Function:\__WiringReadinessShim -Force -ErrorAction SilentlyContinue
            Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue

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

        if ($hadRealDocker -and (Get-Command docker -CommandType Function -ErrorAction SilentlyContinue)) {
            throw "The docker stand-in outlived the run; refusing to continue with a live docker on PATH."
        }
        if (Get-Command Assert-MssqlPhysicalDatastoreDistinctness -CommandType Alias -ErrorAction SilentlyContinue) {
            throw "The authority alias shim outlived the run."
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

    It "mssql shared mode: the script still hands the authority the shared-mode file, whose marker gates it off (transport no-op is unit-proven)" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 1
        $sharedValues = ReadValuesFromEnvFile $run.AuthorityCalls[0].EnvironmentFile
        ($sharedValues["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] -eq "true") | Should -BeFalse -Because "the marker is what gates the authority off in shared mode"
    }

    It "postgresql: the authority is never invoked, even in separate mode (engine gate)" {
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -FailDbUp -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine postgresql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.ErrorMessage | Should -BeLike "*Failed to start Postgresql*"
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

    It "keycloak identity: Keycloak work precedes the database and is not gated; the authority still guards everything after readiness" {
        # The keycloak container start fails at the compose boundary here (it is not the db-up
        # call), which proves the authority was not consulted for identity-provider work.
        $envFile = New-WiringEnvFile
        $run = Invoke-WiringRun -ScriptBlock {
            & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -IdentityProvider keycloak -EnvironmentFile $envFile -InfraOnly *>$null
        }

        $run.AuthorityCalls | Should -HaveCount 0
        $run.ErrorMessage | Should -BeLike "*Failed to start Keycloak*"
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
    }

    Context "structural pins" {

    BeforeAll {
        $script:localText = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-local-dms.ps1") -Raw
        $script:publishedText = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-published-dms.ps1") -Raw
    }

    It "each script calls the authority exactly once, inside the mssql readiness block, gated on cmsParticipates" {
        foreach ($scriptText in @($script:localText, $script:publishedText)) {
            [regex]::Matches($scriptText, [regex]::Escape("Assert-MssqlPhysicalDatastoreDistinctness")).Count | Should -Be 1

            # Region: the main-flow mssql readiness block. The call must sit after the readiness
            # wait and before the identity/OpenIddict parameter construction that follows it.
            $callIndex = $scriptText.IndexOf("Assert-MssqlPhysicalDatastoreDistinctness")
            $waitIndex = $scriptText.LastIndexOf("Wait-MssqlReady -ContainerName", $callIndex)
            $gateIndex = $scriptText.LastIndexOf('if ($cmsParticipates)', $callIndex)
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
}
