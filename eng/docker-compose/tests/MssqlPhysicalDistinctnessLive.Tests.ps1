# Live suite for the stage-2 MSSQL physical-identity authority, end-to-end through the REAL
# Assert-MssqlPhysicalDatastoreDistinctness: env-file marker gate, sequential Compose-precedence
# candidate resolution, UTF-16 hex transport, docker exec + sqlcmd against a running SQL Server,
# strict parse, and the redacted diagnostics - nothing mocked.
#
# WHAT THIS PROVES: the running instance is the sole MSSQL name authority, and its verdicts are
# INSTANCE-relative by contract. On the pinned default-collation fixture the reviewer's own row -
# a legal Unicode datastore name - is ACCEPTED while collation look-alikes are REFUSED; on a
# case-sensitive instance a case variant the old offline rule would have refused is ACCEPTED
# (while width still folds); and on an instance whose sa login lands in a non-master default
# database the verdicts are unchanged, because the batch pins its context with -d master and
# asserts it in-batch. No test anywhere asserts that any name must be refused for MSSQL without
# asking the server.
#
# OPT-IN AND READ-ONLY, same convention as MssqlCollationParity.Tests.ps1. Each Describe gates on
# its own fixture variable and self-skips when unset (every CI lane and the hermetic Linux run:
# no docker is ever touched). The default-collation Describe reuses the parity suite's fixture:
#   DMS_MSSQL_COLLATION_FIXTURE_CONTAINER   (+ DMS_MSSQL_COLLATION_FIXTURE_SA_PASSWORD)
# The instance-fidelity scenarios each need a DISPOSABLE fixture of a specific shape - never a
# shared server, because the shape itself is the subject:
#   DMS_MSSQL_DISTINCTNESS_CS_CONTAINER     (+ _SA_PASSWORD): started with
#     MSSQL_COLLATION=SQL_Latin1_General_CP1_CS_AS
#   DMS_MSSQL_DISTINCTNESS_ALTDB_CONTAINER  (+ _SA_PASSWORD): default collation, plus
#     CREATE DATABASE altdb; ALTER LOGIN sa WITH DEFAULT_DATABASE = altdb;
# The authority's own probes are read-only by construction (the generated batch is SELECT-only
# and travels over stdin); each Describe's fixture-shape probe is likewise a SELECT over the same
# stdin transport. Every non-ASCII candidate is built from [char] code points so this file stays
# ASCII-only.

# The live helpers hand the fixture's SA password to the real authority (and to the read-only
# fixture-shape probe) by parameter; the plaintext trade-off is documented on the authority
# itself. Scriptblock-level suppression attributes are not honored by the analyzer, so the
# suppression lives here.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Live helpers mirror the real authority parameter surface; see the suppression on Assert-MssqlPhysicalDatastoreDistinctness.')]
param()

BeforeDiscovery {
    $script:defaultFixtureEnabled = -not [string]::IsNullOrWhiteSpace($env:DMS_MSSQL_COLLATION_FIXTURE_CONTAINER)
    $script:csFixtureEnabled = -not [string]::IsNullOrWhiteSpace($env:DMS_MSSQL_DISTINCTNESS_CS_CONTAINER)
    $script:altDbFixtureEnabled = -not [string]::IsNullOrWhiteSpace($env:DMS_MSSQL_DISTINCTNESS_ALTDB_CONTAINER)
}

BeforeAll {
    $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

    $script:reservedName = 'edfi_configurationservice'

    # The candidate table shared by every fixture Describe, keyed for the -ForEach rows (whose
    # data is bound at discovery time, before this block runs). Exotic values reach the authority
    # as AMBIENT MSSQL_DB_NAME overrides (full UTF-16 fidelity, and it exercises the documented
    # Compose ambient precedence); the env file itself stays ASCII-only.
    $script:liveName = @{
        'reviewer'          = "$([char]0xE9)dfi_configurationservice"          # U+00E9 first letter
        'second-unicode'    = "edfi_d$([char]0xE1)tastore"                     # U+00E1 embedded
        'unrelated'         = 'edfi_datamanagementservice'
        'exact'             = $script:reservedName
        'case-variant'      = 'EDFI_ConfigurationService'
        'trail-space'       = "$($script:reservedName) "
        'full-width'        = "$([char]0xFF45)dfi_configurationservice"        # U+FF45 first letter
        'fi-ligature'       = "ed$([char]0xFB01)_configurationservice"         # U+FB01 expands to fi
        'zw-joiner'         = "edfi$([char]0x200D)_configurationservice"       # U+200D embedded
        'trail-lf'          = "$($script:reservedName)`n"
    }

    # Writes a separate-topology env file whose file-authored initialized candidate is the given
    # ASCII name; ambient MSSQL_DB_NAME (when a test sets one) takes Compose precedence over it.
    function script:New-LiveTopologyEnvFile {
        param([Parameter(Mandatory)] [string]$FileAuthoredName)

        $path = Join-Path $TestDrive "live-topology-$([Guid]::NewGuid().ToString('N')).env"
        Set-Content -LiteralPath $path -NoNewline -Value (@(
            'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
            "MSSQL_DB_NAME=$FileAuthoredName"
        ) -join "`n")
        return $path
    }

    # Drives the REAL authority. The initialized candidate travels ambiently unless -FileAuthored
    # is requested; a registered candidate is handed over as the PROVIDER-PARSED value, exactly as
    # the wired start script does (never raw parameter text).
    function script:Invoke-LiveAuthority {
        param(
            [Parameter(Mandatory)] [string]$ContainerName,
            [Parameter(Mandatory)] [string]$SaPassword,
            [Parameter(Mandatory)] [string]$InitializedName,
            [switch]$FileAuthored,
            [string]$RegisteredRawName = ""
        )

        if ($FileAuthored) {
            $envFile = New-LiveTopologyEnvFile -FileAuthoredName $InitializedName
        }
        else {
            $envFile = New-LiveTopologyEnvFile -FileAuthoredName 'edfi_datamanagementservice'
            [System.Environment]::SetEnvironmentVariable('MSSQL_DB_NAME', $InitializedName)
        }

        $registeredParsedValue = ""
        if (-not [string]::IsNullOrWhiteSpace($RegisteredRawName)) {
            $registeredParsedValue = Get-RegisteredDatastoreDatabaseValue -DatastoreDatabaseName $RegisteredRawName
        }

        Assert-MssqlPhysicalDatastoreDistinctness `
            -EnvironmentFile $envFile `
            -ContainerName $ContainerName `
            -SaPassword $SaPassword `
            -RegisteredDatastoreDatabaseName $registeredParsedValue
    }

    # Read-only fixture-shape probe over the same stdin transport the parity suite uses: no file
    # is copied into the container and nothing is created or dropped.
    function script:Invoke-LiveFixtureSql {
        param(
            [Parameter(Mandatory)] [string]$ContainerName,
            [Parameter(Mandatory)] [string]$SaPassword,
            [Parameter(Mandatory)] [string]$Sql
        )

        $output = ($Sql + [System.Environment]::NewLine + 'GO') |
            docker exec -i $ContainerName /opt/mssql-tools18/bin/sqlcmd `
                -S localhost -U sa -P $SaPassword -C -h -1
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd in fixture container failed: $($output -join [System.Environment]::NewLine)" }
        @($output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne '' })
    }

    function script:Get-LiveFixturePassword {
        param([string]$ConfiguredValue)
        if ([string]::IsNullOrWhiteSpace($ConfiguredValue)) { 'EdFi_Dms1!' } else { $ConfiguredValue }
    }
}

Describe "MSSQL live distinctness: the pinned default-collation fixture" -Skip:(-not $script:defaultFixtureEnabled) {
    BeforeAll {
        $script:fixtureContainer = $env:DMS_MSSQL_COLLATION_FIXTURE_CONTAINER
        $script:saPassword = Get-LiveFixturePassword -ConfiguredValue $env:DMS_MSSQL_COLLATION_FIXTURE_SA_PASSWORD

        # Production resolves MSSQL_DB_NAME with ambient precedence, so the suite owns that name
        # for its whole run: presence-aware snapshot here, per-test clearing, faithful restore.
        $script:ambientDbNameWasPresent = Test-Path -LiteralPath 'Env:\MSSQL_DB_NAME'
        $script:ambientDbNameValue = [System.Environment]::GetEnvironmentVariable('MSSQL_DB_NAME')
    }

    AfterAll {
        if ($script:ambientDbNameWasPresent) {
            [System.Environment]::SetEnvironmentVariable('MSSQL_DB_NAME', $script:ambientDbNameValue)
        }
        else {
            Remove-Item -LiteralPath 'Env:\MSSQL_DB_NAME' -Force -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        Remove-Item -LiteralPath 'Env:\MSSQL_DB_NAME' -Force -ErrorAction SilentlyContinue
    }

    It "ACCEPTS <Label> as the initialized candidate" -ForEach @(
        @{ Label = 'the reviewer row: an accented first letter (U+00E9)'; NameKey = 'reviewer' }
        @{ Label = 'a second valid non-ASCII name (embedded U+00E1)'; NameKey = 'second-unicode' }
        @{ Label = 'a normal unrelated ASCII name'; NameKey = 'unrelated' }
    ) {
        # The rows the superseded offline rules could not accept: legal Unicode datastore names the
        # instance itself reports DISTINCT. Refusing them violated this design's
        # leave-the-datastore-unchanged contract; the server-backed verdict admits them.
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName $script:liveName[$NameKey] } |
            Should -Not -Throw
    }

    It "ACCEPTS <Label> as the parsed registered candidate" -ForEach @(
        @{ Label = 'the reviewer row (U+00E9)'; NameKey = 'reviewer' }
        @{ Label = 'the second valid non-ASCII name (U+00E1)'; NameKey = 'second-unicode' }
    ) {
        # Matched coverage through the OTHER transport: the same names, arriving as the value the
        # provider receives from -DataStoreDatabaseName (these survive serialization unchanged).
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName 'edfi_datamanagementservice' -RegisteredRawName $script:liveName[$NameKey] } |
            Should -Not -Throw
    }

    It "REFUSES <Label> as the initialized candidate, naming MSSQL_DB_NAME" -ForEach @(
        @{ Label = 'the exact reserved literal'; NameKey = 'exact'; FileAuthored = $true }
        @{ Label = 'a case variant'; NameKey = 'case-variant'; FileAuthored = $true }
        @{ Label = 'a trailing-space variant'; NameKey = 'trail-space'; FileAuthored = $false }
        @{ Label = 'a full-width first letter (U+FF45)'; NameKey = 'full-width'; FileAuthored = $false }
        @{ Label = 'the fi ligature (U+FB01)'; NameKey = 'fi-ligature'; FileAuthored = $false }
        @{ Label = 'an embedded zero-width joiner (U+200D)'; NameKey = 'zw-joiner'; FileAuthored = $false }
    ) {
        # The instance's own equivalence class under the pinned default collation, taken from the
        # server at start time - including the exact-typo shape, which now fails HERE (after
        # readiness, the accepted UX cost) rather than pre-Docker. Plain ASCII shapes run
        # file-authored to prove the file path; exotic shapes run ambient, proving Compose
        # precedence carries full UTF-16 fidelity to the batch.
        $invoke = { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName $script:liveName[$NameKey] -FileAuthored:$FileAuthored }
        $thrown = $invoke | Should -Throw "*SAME physical database*" -PassThru
        $thrown.Exception.Message | Should -BeLike "*'MSSQL_DB_NAME'*" -Because "the diagnostic names the source key of the colliding candidate"
    }

    It "REFUSES <Label> as the parsed registered candidate, naming -DataStoreDatabaseName" -ForEach @(
        @{ Label = 'the exact reserved literal'; NameKey = 'exact' }
        @{ Label = 'a case variant'; NameKey = 'case-variant' }
        @{ Label = 'a trailing-space variant'; NameKey = 'trail-space' }
        @{ Label = 'a full-width first letter (U+FF45)'; NameKey = 'full-width' }
    ) {
        # The same collision class through the registered transport (these shapes survive
        # serialization and parsing unchanged, the trailing space included).
        $invoke = { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName 'edfi_datamanagementservice' -RegisteredRawName $script:liveName[$NameKey] }
        $thrown = $invoke | Should -Throw "*SAME physical database*" -PassThru
        $thrown.Exception.Message | Should -BeLike "*'-DataStoreDatabaseName'*" -Because "the diagnostic names the source parameter of the colliding candidate"
    }

    It "splits the bare-trailing-LF name by transport: registered REFUSED, initialized takes the instance verdict" {
        # As the REGISTERED candidate the provider-parsed value IS the reserved literal (the
        # serializer/parser transport drops a bare trailing LF) - a collision on ANY instance, so
        # it is refused. As the INITIALIZED candidate the LF-bearing name reaches the server
        # verbatim and gets whatever the instance answers - measured DISTINCT on the pinned
        # fixture, so it is ACCEPTED: the documented, deliberate flip from the superseded
        # conservative refusal (a transport fact and a collation fact are different questions).
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName 'edfi_datamanagementservice' -RegisteredRawName $script:liveName['trail-lf'] } |
            Should -Throw "*SAME physical database*"

        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName $script:liveName['trail-lf'] } |
            Should -Not -Throw
    }

    It "withholds the candidate value in a refusal diagnostic, for a non-ASCII candidate" {
        # The candidate travels to the server as hex and never into a message: the refusal names
        # the key and the reserved literal only. Asserted on the full-width shape, whose code
        # point would be unmistakable in a leaked value.
        $thrown = { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName $script:liveName['full-width'] } |
            Should -Throw "*SAME physical database*" -PassThru

        $thrown.Exception.Message | Should -Not -BeLike "*$([char]0xFF45)*" -Because "diagnostics never echo a caller-authored value"
        $thrown.Exception.Message | Should -BeLike "*edfi_configurationservice*" -Because "the reserved literal is the one name the diagnostic may state"
    }

    It "fails closed as UNVERIFIABLE when the container does not exist, without echoing the candidate" {
        # The live half of the transport fail-closed contract: a docker exec against a container
        # that is not there exits nonzero, and the authority refuses to treat that as success.
        $thrown = { Invoke-LiveAuthority -ContainerName "dms-nonexistent-$([Guid]::NewGuid().ToString('N'))" -SaPassword $script:saPassword -InitializedName $script:liveName['reviewer'] } |
            Should -Throw "*could not be confirmed*" -PassThru

        $thrown.Exception.Message | Should -BeLike "*Startup does not proceed unverified*"
        $thrown.Exception.Message | Should -Not -BeLike "*$([char]0xE9)*" -Because "even transport diagnostics withhold the candidate"
    }
}

Describe "MSSQL live distinctness: the case-sensitive-instance scenario" -Skip:(-not $script:csFixtureEnabled) {
    BeforeAll {
        $script:fixtureContainer = $env:DMS_MSSQL_DISTINCTNESS_CS_CONTAINER
        $script:saPassword = Get-LiveFixturePassword -ConfiguredValue $env:DMS_MSSQL_DISTINCTNESS_CS_SA_PASSWORD

        $script:ambientDbNameWasPresent = Test-Path -LiteralPath 'Env:\MSSQL_DB_NAME'
        $script:ambientDbNameValue = [System.Environment]::GetEnvironmentVariable('MSSQL_DB_NAME')
    }

    AfterAll {
        if ($script:ambientDbNameWasPresent) {
            [System.Environment]::SetEnvironmentVariable('MSSQL_DB_NAME', $script:ambientDbNameValue)
        }
        else {
            Remove-Item -LiteralPath 'Env:\MSSQL_DB_NAME' -Force -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        Remove-Item -LiteralPath 'Env:\MSSQL_DB_NAME' -Force -ErrorAction SilentlyContinue
    }

    It "is talking to a case-sensitive instance, the shape this scenario is about" {
        $lines = Invoke-LiveFixtureSql -ContainerName $script:fixtureContainer -SaPassword $script:saPassword `
            -Sql "SET NOCOUNT ON; SELECT 'collation=' + CONVERT(varchar(128), SERVERPROPERTY('Collation'));"
        @($lines | Where-Object { $_ -like 'collation=*' }) | Should -Be @('collation=SQL_Latin1_General_CP1_CS_AS') -Because "the scenario exists to prove instance fidelity, so the instance must actually be the CS shape"
    }

    It "ACCEPTS the upper-case variant the offline case-folding rule would have refused" {
        # THE row that kills any resurrected offline case-fold: on this instance
        # EDFI_CONFIGURATIONSERVICE is a genuinely distinct database (measured: DB_ID does not
        # resolve, and its CREATE fails file-level 1802, not duplicate-name 1801), and the
        # authority inherits that answer because the instance is the authority.
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName 'EDFI_CONFIGURATIONSERVICE' -FileAuthored } |
            Should -Not -Throw
    }

    It "ACCEPTS the accented reviewer name here too" {
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName "$([char]0xE9)dfi_configurationservice" } |
            Should -Not -Throw
    }

    It "still REFUSES the trailing-space variant: padding is collation-independent" {
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName "edfi_configurationservice " } |
            Should -Throw "*SAME physical database*"
    }

    It "still REFUSES the full-width variant: case sensitivity is not width sensitivity" {
        { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName "$([char]0xFF45)dfi_configurationservice" } |
            Should -Throw "*SAME physical database*"
    }
}

Describe "MSSQL live distinctness: the alternate-default-database scenario" -Skip:(-not $script:altDbFixtureEnabled) {
    BeforeAll {
        $script:fixtureContainer = $env:DMS_MSSQL_DISTINCTNESS_ALTDB_CONTAINER
        $script:saPassword = Get-LiveFixturePassword -ConfiguredValue $env:DMS_MSSQL_DISTINCTNESS_ALTDB_SA_PASSWORD

        $script:ambientDbNameWasPresent = Test-Path -LiteralPath 'Env:\MSSQL_DB_NAME'
        $script:ambientDbNameValue = [System.Environment]::GetEnvironmentVariable('MSSQL_DB_NAME')
    }

    AfterAll {
        if ($script:ambientDbNameWasPresent) {
            [System.Environment]::SetEnvironmentVariable('MSSQL_DB_NAME', $script:ambientDbNameValue)
        }
        else {
            Remove-Item -LiteralPath 'Env:\MSSQL_DB_NAME' -Force -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        Remove-Item -LiteralPath 'Env:\MSSQL_DB_NAME' -Force -ErrorAction SilentlyContinue
    }

    It "is talking to an instance whose sa login lands outside master, the shape this scenario is about" {
        # Measured during the architecture gate: WITHOUT -d master the batch runs in the login's
        # default database, whose collation then silently decides every comparison. This fixture
        # makes that hazard real so the next two tests prove the pin.
        $lines = Invoke-LiveFixtureSql -ContainerName $script:fixtureContainer -SaPassword $script:saPassword `
            -Sql "SET NOCOUNT ON; SELECT 'default-db=' + default_database_name FROM sys.server_principals WHERE name = 'sa';"
        $defaultDb = @($lines | Where-Object { $_ -like 'default-db=*' } | ForEach-Object { $_.Substring(11) }) | Select-Object -First 1
        $defaultDb | Should -Not -BeNullOrEmpty
        $defaultDb | Should -Not -Be 'master' -Because "the scenario exists to prove -d master pins the context regardless of where the login lands"
    }

    It "keeps every verdict unchanged: <Label>" -ForEach @(
        @{ Label = 'the exact reserved literal is REFUSED'; Name = 'edfi_configurationservice'; Refused = $true }
        @{ Label = 'a case variant is REFUSED (default collation)'; Name = 'EDFI_ConfigurationService'; Refused = $true }
        @{ Label = 'the accented reviewer name is ACCEPTED'; Name = ''; Refused = $false }
    ) {
        # -d master is explicit in the runner argv and the batch asserts DB_NAME() = master plus
        # master/server collation agreement, so the login's default database cannot leak into the
        # verdicts: they match the pinned default-collation fixture exactly.
        $candidateName = if ($Name -eq '') { "$([char]0xE9)dfi_configurationservice" } else { $Name }
        $invoke = { Invoke-LiveAuthority -ContainerName $script:fixtureContainer -SaPassword $script:saPassword -InitializedName $candidateName }
        if ($Refused) {
            $invoke | Should -Throw "*SAME physical database*"
        }
        else {
            $invoke | Should -Not -Throw
        }
    }
}
