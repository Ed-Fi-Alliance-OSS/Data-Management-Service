# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for E2E testing
.DESCRIPTION
    This script is a convenience wrapper that runs start-local-dms.ps1 with the standard
    E2E testing configuration. It is the companion to teardown-local-dms.ps1.

    Extension schema packages (Sample, Homograph) are loaded through the file-based SCHEMA_PACKAGES path.
    The -AddExtensionSecurityMetadata switch activates Hybrid claims mode so extension
    claimset fragments are loaded from the AdditionalClaimsets directory mounted at
    /app/additional-claims. This is the non-bootstrap compatibility path; bootstrap mode
    activates staged schema and claims automatically when a manifest is present.

    The script runs (with -DatabaseEngine forwarded to every engine-aware phase):
    ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile <selected env file> -DatabaseEngine <engine> -r -AddExtensionSecurityMetadata
    ./configure-local-data-store.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DataStoreDatabaseName <E2E_DATABASE_NAME>
    ./provision-e2e-database.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DatabaseName <E2E_DATABASE_NAME>
    ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile <selected env file> -DatabaseEngine <engine> -AddExtensionSecurityMetadata

    Every Docker phase above runs with USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES
    removed from this process, so the selected environment file is the sole authority for the schema
    package surface of the whole direct setup flow. Docker Compose gives process environment
    variables precedence over --env-file entries, and local-dms.yml resolves each of those three
    with a ${VAR:-default} fallback that treats a present-but-blank value exactly like an unset one.
    An ambient blank or false value therefore used to start DMS on the image-baked schemas while
    provisioning had already stamped the environment file's full package surface, and every
    data-plane request then failed with an EffectiveSchemaHash mismatch. The caller's original
    environment is restored exactly on completion, including the absent, empty, whitespace, and
    valued distinctions, and on failure paths.

    After DMS starts, the script verifies the container actually received that package surface and
    fails the setup when it did not, so this class of mismatch is reported here rather than as an
    HTTP 503 EffectiveSchemaHash failure in every scenario of the suite. Both sides of the comparison
    are read from the environment file, never with Docker Compose precedence.

    On completion the script prints a copyable teardown command carrying the same -DatabaseEngine and
    the resolved -EnvironmentFile, so a custom or MSSQL run is torn down against its own compose
    definition/environment rather than the teardown wrapper's postgresql/.env.e2e defaults:
    ./teardown-local-dms.ps1 -DatabaseEngine <engine> -EnvironmentFile '<resolved env file>'.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script is intentionally host-oriented and uses console progress output.')]
[CmdletBinding()]
param(
    [string] $EnvironmentFile = "./.env.e2e",

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1") composed into the effective environment file.
    [string] $DataStandardVersion,

    # Database engine backing the stack. "postgresql" (default) or "mssql".
    [ValidateSet("postgresql", "mssql")]
    [string] $DatabaseEngine = "postgresql"
)

function Get-DirectSetupTeardownCommand {
    # Builds a copyable teardown command that carries the same engine and environment file this setup
    # used, so a custom or MSSQL run is torn down against its own compose definition/environment rather
    # than the teardown wrapper's postgresql/.env.e2e defaults. The path is single-quoted (with embedded
    # single quotes doubled) so a path containing spaces or quotes is safe to paste back.
    param(
        [Parameter(Mandatory)]
        [string] $DatabaseEngine,

        [Parameter(Mandatory)]
        [string] $EnvironmentFile
    )

    $quotedEnvironmentFile = "'" + ($EnvironmentFile -replace "'", "''") + "'"
    return "./teardown-local-dms.ps1 -DatabaseEngine $DatabaseEngine -EnvironmentFile $quotedEnvironmentFile"
}

function Invoke-WithEnvironmentFileSchemaSettings {
    # Runs the direct setup flow's Docker phases with the three schema package variables absent from
    # this process, so Docker Compose must resolve them from the selected --env-file, and restores
    # the caller's exact prior state afterward.
    #
    # Compose gives process environment variables precedence over --env-file entries, and
    # local-dms.yml resolves all three with a ${VAR:-default} fallback. Because ':-' substitutes the
    # default for an empty value as well as an unset one, an ambient blank value silently wins over
    # the environment file: the DMS container is created with AppSettings__UseApiSchemaPath=false and
    # empty AppSettings__ApiSchemaPath/SCHEMA_PACKAGES, run.sh skips the package download entirely,
    # and DMS loads only the image-baked schemas. Provisioning is not affected, because it reads
    # SCHEMA_PACKAGES from the environment file only - so the database is stamped for the file's full
    # package surface while DMS computes a different runtime hash, and every data-plane request fails
    # with an EffectiveSchemaHash mismatch.
    #
    # The whole phase sequence is guarded rather than only the two start-local-dms.ps1 calls: the
    # environment file is authoritative for the entire direct setup flow, and a Compose call added to
    # any phase later is covered by construction. The configure and provision phases are unaffected
    # by the removal, because neither reads these variables from the process environment.
    #
    # This is deliberately a caller-side guard. start-local-dms.ps1 must not clear these globally:
    # in bootstrap mode it sets them in-process on purpose, so process precedence makes the staged
    # .bootstrap/ApiSchema workspace authoritative.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Names the schema settings carried by an environment file, matching the equivalent build-dms.ps1 helper.')]
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Action
    )

    $schemaEnvironmentVariableNames = @(
        "USE_API_SCHEMA_PATH",
        "API_SCHEMA_PATH",
        "SCHEMA_PACKAGES"
    )

    # $null distinguishes absent from present-and-empty, which is the distinction the restore below
    # has to reproduce.
    $previousValues = @{}
    foreach ($name in $schemaEnvironmentVariableNames) {
        $previousValues[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }

    try {
        foreach ($name in $schemaEnvironmentVariableNames) {
            # Remove-Item, never an assignment: whether '$env:X = $null' removes the variable or
            # leaves it present-and-blank varies by platform and PowerShell/.NET version, and a blank
            # value satisfies ${VAR:-default} - which is the bug this guard exists to prevent.
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }

        & $Action
    }
    finally {
        # Restore each variable to its exact prior state: re-create it with the verbatim prior value
        # (including empty and whitespace) when it existed, otherwise remove it. This runs on the
        # success path, when the action throws, and when the action calls exit.
        foreach ($name in $schemaEnvironmentVariableNames) {
            if ($null -eq $previousValues[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($name, $previousValues[$name])
            }
        }
    }
}

function Get-DmsSchemaEnvironmentToken {
    # Classifies a container environment value without echoing it, so a failure message carries a
    # fixed vocabulary instead of interpolated container text.
    param(
        [Parameter(Mandatory)]
        [hashtable] $ContainerEnvironment,

        [Parameter(Mandatory)]
        [string] $Key
    )

    if (-not $ContainerEnvironment.ContainsKey($Key)) {
        return "<absent>"
    }

    $value = [string]$ContainerEnvironment[$Key]

    if ([string]::IsNullOrWhiteSpace($value)) {
        return "<blank>"
    }

    # Ordinal, deliberately not OrdinalIgnoreCase. run.sh:28 gates the entire package download on
    # [ "$AppSettings__UseApiSchemaPath" = true ], a byte-exact POSIX string comparison, so only
    # lowercase 'true' turns on the ApiSchema path at runtime. Accepting 'TRUE' or 'True' here passed a
    # container that then downloaded nothing, which is precisely the EffectiveSchemaHash mismatch this
    # gate exists to catch. Any other casing classifies as <set> below and fails.
    if ([string]::Equals($value, "true", [System.StringComparison]::Ordinal)) {
        return "true"
    }

    # OrdinalIgnoreCase is still correct here: this token is only message vocabulary for a value that
    # fails whatever its casing, not the gate itself.
    if ([string]::Equals($value, "false", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "false"
    }

    return "<set>"
}

function Get-DmsContainerSchemaPackage {
    # Returns the container's SCHEMA_PACKAGES entries as a parsed array, or $null when the value is
    # absent, blank, or not a JSON array. The value itself is never returned to a caller that logs it:
    # the entries only reach Get-DmsSchemaPackageIdentity, which is used for comparison alone.
    param(
        [Parameter(Mandatory)]
        [hashtable] $ContainerEnvironment
    )

    if (-not $ContainerEnvironment.ContainsKey("SCHEMA_PACKAGES")) {
        return $null
    }

    $value = [string]$ContainerEnvironment["SCHEMA_PACKAGES"]

    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    try {
        # -NoEnumerate keeps a single-element array an array. Without it PowerShell unwraps
        # '[{...}]' to one PSCustomObject, and the shape check below would reject a valid
        # one-package container as "not a JSON array".
        $parsed = $value | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
    }
    catch {
        return $null
    }

    # A JSON object parses without error but is not a package list; only an array is a package set.
    # This check cannot be replaced by wrapping the parse result in @(...), which would make a JSON
    # object look like a one-item package list.
    if ($parsed -isnot [System.Collections.IList]) {
        return $null
    }

    # The unary comma keeps an empty or single-entry result an array through the return: PowerShell
    # would otherwise unroll '@()' to nothing, and the caller could not tell it from the $null above.
    return , @($parsed)
}

function Get-DmsSchemaPackageIdentity {
    # Reduces ApiSchema package entries to sorted, comparable identities built from the three fields
    # that decide which schema artifact is downloaded - name, version, and feedUrl - which is what
    # run.sh and the provision phase's downloader both consume. A count comparison alone accepts a
    # container whose packages differ in any of them, which is exactly the surface the E2E database was
    # not provisioned for. A missing field normalizes to an empty string rather than throwing, so a
    # malformed entry compares unequal instead of failing the verification.
    #
    # The identities are only ever compared; they are never returned to a failure message, so no
    # container-supplied text can reach the console.
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]] $Package
    )

    $identities = @(
        foreach ($entry in $Package) {
            $fields = [ordered]@{}
            foreach ($fieldName in @("name", "version", "feedUrl")) {
                $property = if ($null -eq $entry) { $null } else { $entry.PSObject.Properties[$fieldName] }
                $fields[$fieldName] = if ($null -eq $property) { "" } else { [string]$property.Value }
            }

            # JSON rather than a delimiter join: a value containing the delimiter would otherwise let
            # two different package sets normalize to the same identity text.
            ConvertTo-Json -InputObject $fields -Compress
        }
    )

    # Declaration order is not part of the package surface, so both sides are sorted before they are
    # compared. Ordinal, so the result cannot vary with the host's culture.
    [array]::Sort($identities, [System.StringComparer]::Ordinal)

    return , $identities
}

function Get-DmsSchemaEnvironmentVerdict {
    <#
    .SYNOPSIS
        Decides whether the started DMS container's schema environment agrees with the environment file
        the E2E database was provisioned from, and returns a verdict plus a sanitized reason and
        remediation. Pure, so the decision is unit-testable without Docker.
    .DESCRIPTION
        The provisioner reads SCHEMA_PACKAGES from the environment file only, so the database is always
        stamped for the file's package surface. DMS, by contrast, receives its settings through Docker
        Compose, which resolves them ambient-first. When the two disagree the stack comes up healthy
        and then fails every data-plane request with an EffectiveSchemaHash mismatch, so the
        disagreement is worth failing on at setup time.

        Every requirement here is unconditional. The caller obtains ExpectedPackageIdentity from the same
        reader the provision phase used, which throws unless the file declares at least one package, so
        by the time this runs the database has been provisioned for a real package surface and the
        runtime must match it. That includes the environment file's own USE_API_SCHEMA_PATH: a file that
        declares packages but does not enable the ApiSchema path is internally inconsistent and
        guarantees the mismatch, so it is reported rather than tolerated.
    .OUTPUTS
        [pscustomobject] with ShouldFail, Reason, and Remediation.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable] $ContainerEnvironment,

        # The environment file's declared ApiSchema package surface, as sorted identities from
        # Get-DmsSchemaPackageIdentity. Constrained to at least one entry because the file-only reader
        # the caller uses cannot produce less: an absent, malformed, or empty SCHEMA_PACKAGES
        # declaration already failed the provision phase.
        [Parameter(Mandatory)]
        [ValidateCount(1, [int]::MaxValue)]
        [string[]] $ExpectedPackageIdentity,

        # The environment file's own USE_API_SCHEMA_PATH, read file-only (never with Compose
        # precedence, which would let the ambient override this gate exists to catch decide the
        # expected side and agree with a wrongly-started container).
        [Parameter(Mandatory)]
        [bool] $EnvironmentFileUsesApiSchemaPath,

        # The environment file's API_SCHEMA_PATH, read file-only for the same reason. Compared
        # Ordinal: this is the value Compose passes through verbatim, so any difference means the
        # container is not using the path the environment file selected.
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $EnvironmentFileApiSchemaPath
    )

    $ambientRemediation = "Remove USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES from the invoking shell, or select a different -EnvironmentFile, then re-run setup."
    $expectedPackageCount = $ExpectedPackageIdentity.Count

    if (-not $EnvironmentFileUsesApiSchemaPath) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the environment file declares $expectedPackageCount ApiSchema package(s) but does not set USE_API_SCHEMA_PATH=true, so the E2E database was provisioned for those packages while DMS was configured to load only the schemas baked into the image."
            Remediation = "Set USE_API_SCHEMA_PATH=true in the environment file so its declared packages are the runtime schema surface."
        }
    }

    $useApiSchemaPathToken = Get-DmsSchemaEnvironmentToken -ContainerEnvironment $ContainerEnvironment -Key "AppSettings__UseApiSchemaPath"
    if ($useApiSchemaPathToken -ne "true") {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's AppSettings__UseApiSchemaPath is $useApiSchemaPathToken but the environment file declares $expectedPackageCount ApiSchema package(s), so DMS loaded only the schemas baked into the image while the E2E database was provisioned for those packages."
            Remediation = $ambientRemediation
        }
    }

    $apiSchemaPathToken = Get-DmsSchemaEnvironmentToken -ContainerEnvironment $ContainerEnvironment -Key "AppSettings__ApiSchemaPath"
    if ($apiSchemaPathToken -in @("<absent>", "<blank>")) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's AppSettings__ApiSchemaPath is $apiSchemaPathToken, so the declared ApiSchema packages have nowhere to be materialized."
            Remediation = $ambientRemediation
        }
    }

    # A container path that is present but not the one the environment file selected means DMS is
    # materializing packages somewhere other than where the file said, which the token check above
    # cannot see. Ordinal comparison only: Compose passes the value through verbatim, so no path
    # normalization is warranted here. Neither path is echoed.
    if (-not [string]::Equals(
            [string]$ContainerEnvironment["AppSettings__ApiSchemaPath"],
            $EnvironmentFileApiSchemaPath,
            [System.StringComparison]::Ordinal)) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's AppSettings__ApiSchemaPath differs from the environment file's API_SCHEMA_PATH, so DMS is not materializing the declared ApiSchema packages where the environment file selected."
            Remediation = $ambientRemediation
        }
    }

    $containerPackages = Get-DmsContainerSchemaPackage -ContainerEnvironment $ContainerEnvironment
    if ($null -eq $containerPackages) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's SCHEMA_PACKAGES is absent, blank, or not a JSON array, but the environment file declares $expectedPackageCount ApiSchema package(s)."
            Remediation = $ambientRemediation
        }
    }

    if ($containerPackages.Count -ne $expectedPackageCount) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container received $($containerPackages.Count) ApiSchema package(s) but the E2E database was provisioned for the environment file's $expectedPackageCount."
            Remediation = $ambientRemediation
        }
    }

    # Counts agreeing is not the surface agreeing. A container carrying the same number of packages at
    # a different name, version, or feed URL downloads different schemas, computes a different runtime
    # hash, and fails every data-plane request exactly as a count mismatch would. Both sides are already
    # sorted, so this is a positional comparison of equal-length identity lists; neither side is echoed.
    $containerPackageIdentity = Get-DmsSchemaPackageIdentity -Package $containerPackages
    for ($index = 0; $index -lt $expectedPackageCount; $index++) {
        if (-not [string]::Equals(
                $containerPackageIdentity[$index],
                $ExpectedPackageIdentity[$index],
                [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                ShouldFail  = $true
                Reason      = "the DMS container's $expectedPackageCount ApiSchema package(s) differ from the environment file's declared packages by name, version, or feed URL, so DMS is loading a different schema surface than the E2E database was provisioned for."
                Remediation = $ambientRemediation
            }
        }
    }

    return [pscustomobject]@{
        ShouldFail  = $false
        Reason      = ""
        Remediation = ""
    }
}

function Get-DmsContainerEnvironment {
    # Reads a container's environment into a key/value map. Fails closed: an inspect that does not
    # succeed is an inability to verify, never a pass.
    param(
        [Parameter(Mandatory)]
        [string] $ContainerName
    )

    $environmentJson = docker inspect $ContainerName --format '{{json .Config.Env}}'

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect Docker container '$ContainerName' to verify its schema environment."
    }

    $containerEnvironment = @{}

    foreach ($entry in @($environmentJson | ConvertFrom-Json)) {
        $entryText = [string]$entry
        $separatorIndex = $entryText.IndexOf("=")

        if ($separatorIndex -lt 0) {
            continue
        }

        $containerEnvironment[$entryText.Substring(0, $separatorIndex)] = $entryText.Substring($separatorIndex + 1)
    }

    return $containerEnvironment
}

function Assert-DmsContainerSchemaEnvironment {
    <#
    .SYNOPSIS
        Fails the setup when the started DMS container's schema environment disagrees with the
        environment file the E2E database was provisioned from, so this class of mismatch surfaces here
        instead of as HTTP 503 EffectiveSchemaHash failures in every scenario of the suite.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $EnvironmentFilePath,

        [Parameter(Mandatory)]
        [hashtable] $EnvironmentValues,

        [Parameter(Mandatory)]
        [string] $ContainerName
    )

    # Both expectations come from the environment FILE. Get-SchemaPackagesFromEnvironmentFile is the
    # same reader the provision phase used and it throws unless at least one package is declared, so a
    # malformed or empty declaration has already failed provisioning rather than being treated as
    # acceptable here. Get-EnvValue is file-only by contract; Get-ComposeResolvedEnvValue must not be
    # used for either value, because resolving the expected side ambient-first would let the very
    # override this gate exists to catch decide what "correct" means.
    #
    # ConvertFrom-ComposeEnvironmentValue is not a Compose-precedence reader: it applies only the
    # value semantics Compose gives a single --env-file entry (surrounding quotes stripped, an inline
    # comment dropped), so a legally quoted or commented declaration in a custom -EnvironmentFile is
    # compared as the container actually received it instead of failing on raw file text.
    $declaredPackages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $EnvironmentFilePath)
    # Ordinal against lowercase 'true', matching run.sh's byte-exact gate. Compose passes the file's
    # value through verbatim, so a file declaring USE_API_SCHEMA_PATH=TRUE yields a container that
    # skips the package download while provisioning still stamps the file's packages; reporting that
    # against the file, with the remediation that names the file, is the actionable failure. The
    # quote/comment normalization still runs first, so "true" and 'true' pass and only the casing is
    # significant.
    $environmentFileUsesApiSchemaPath = [string]::Equals(
        (ConvertFrom-ComposeEnvironmentValue -Value (Get-EnvValue -EnvValues $EnvironmentValues -Name "USE_API_SCHEMA_PATH" -DefaultValue "false")),
        "true",
        [System.StringComparison]::Ordinal
    )
    $environmentFileApiSchemaPath = ConvertFrom-ComposeEnvironmentValue -Value (Get-EnvValue -EnvValues $EnvironmentValues -Name "API_SCHEMA_PATH" -DefaultValue "")

    $verdict = Get-DmsSchemaEnvironmentVerdict `
        -ContainerEnvironment (Get-DmsContainerEnvironment -ContainerName $ContainerName) `
        -ExpectedPackageIdentity (Get-DmsSchemaPackageIdentity -Package $declaredPackages) `
        -EnvironmentFileUsesApiSchemaPath $environmentFileUsesApiSchemaPath `
        -EnvironmentFileApiSchemaPath $environmentFileApiSchemaPath

    if ($verdict.ShouldFail) {
        throw "DMS E2E setup mismatch: $($verdict.Reason) $($verdict.Remediation)"
    }

    Write-Host "Verified DMS container schema environment matches the environment file ($($declaredPackages.Count) ApiSchema package(s))." -ForegroundColor Green
}

Write-Host @"
Ed-Fi DMS Local Environment Setup for E2E Testing
=================================================
"@ -ForegroundColor Cyan

# Check if Docker is running
Write-Host "Checking Docker status..." -ForegroundColor Yellow
$dockerCheck = $null
try {
    $dockerCheck = docker version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed"
    }
}
catch {
    Write-Host ""
    Write-Error "Docker is not running or not installed. Please start Docker and try again."
    Write-Host ""
    Write-Host "Error details:" -ForegroundColor Red
    if ($dockerCheck) {
        Write-Host $dockerCheck -ForegroundColor Red
    } else {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    exit 1
}
Write-Host "Docker is running" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir
    Import-Module ./env-utility.psm1 -Force
    # Shared Compose-equivalent resolver so this wrapper selects the same E2E target database the
    # provision phase resets (an ambient E2E_DATABASE_NAME override wins over the env file).
    Import-Module ./database-safety.psm1 -Force
    # The same file-only schema package reader the provision phase uses, so the post-start
    # verification below compares the container against exactly the package surface the database was
    # provisioned for.
    Import-Module ../schema-package-utility.psm1 -Force

    $baseEnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $dockerComposeDir
    # Compose the data-standard overlay first, then the database-engine overlay (same order as
    # start-local-dms.ps1), so every phase below reads the one resolved file. For postgresql the
    # engine step is a no-op.
    $resolvedEnvironmentFile = Resolve-DataStandardEnvironmentFile `
        -DataStandardVersion $DataStandardVersion `
        -BaseEnvironmentFile $baseEnvironmentFile `
        -DockerComposeRoot $dockerComposeDir
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile `
        -DatabaseEngine $DatabaseEngine `
        -BaseEnvironmentFile $resolvedEnvironmentFile `
        -DockerComposeRoot $dockerComposeDir
    $envValues = ReadValuesFromEnvFile $resolvedEnvironmentFile
    $e2eDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "E2E_DATABASE_NAME"

    if ([string]::IsNullOrWhiteSpace($e2eDatabaseName)) {
        throw "E2E_DATABASE_NAME must be set in '$resolvedEnvironmentFile' or the process environment so direct DMS E2E setup creates a CMS data store against the provisioned E2E database."
    }

    $bootstrapDir = Join-Path $dockerComposeDir ".bootstrap"
    if (Test-Path -LiteralPath $bootstrapDir) {
        Write-Output "Removing stale .bootstrap workspace before file-based schema package E2E startup..."
        # Fail fast on cleanup errors: a stale manifest left here would trigger bootstrap mode
        # on the next start-local-dms.ps1 invocation and silently divert the E2E run.
        Remove-Item -LiteralPath $bootstrapDir -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $bootstrapDir) {
            throw "Failed to remove stale .bootstrap workspace at '$bootstrapDir'. Resolve any file locks or permissions before re-running setup."
        }
    }

    Write-Host "Starting DMS environment with E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Search Engine UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: $resolvedEnvironmentFile" -ForegroundColor Gray
    Write-Host "  - Database Engine: $DatabaseEngine" -ForegroundColor Gray
    Write-Host "  - E2E Database: $e2eDatabaseName" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Output "  - Extension Security Metadata: Yes"
    Write-Host ""

    Write-Output "Using file-based schema packages from $resolvedEnvironmentFile for E2E (non-bootstrap compatibility path)."

    Invoke-WithEnvironmentFileSchemaSettings -Action {
        # Start only the infrastructure and Configuration Service first. DMS starts after the
        # E2E data store exists and the relational schema has been provisioned.
        ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -r -AddExtensionSecurityMetadata

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to start DMS infrastructure. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        # Create the default data store via the configuration phase. start-local-dms.ps1 no longer
        # creates a data store automatically; instance creation is owned by configure-local-data-store.ps1.
        # Config Service is already healthy at this point because the -InfraOnly phase waits for
        # CMS readiness before returning.
        ./configure-local-data-store.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DataStoreDatabaseName $e2eDatabaseName

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to configure local data store. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        Write-Host "`nProvisioning E2E database '$e2eDatabaseName'..." -ForegroundColor Cyan
        ./provision-e2e-database.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DatabaseName $e2eDatabaseName

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to provision E2E database '$e2eDatabaseName'. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        Write-Host "`nStarting DMS after E2E database provisioning..." -ForegroundColor Cyan
        ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -AddExtensionSecurityMetadata

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to start DMS service after E2E database provisioning. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        # Prove DMS actually came up on the environment file's schema package surface before any
        # scenario runs. Inside the guard, so the file-only expectation cannot be contaminated by an
        # ambient override even if a future edit reaches for a Compose-precedence reader.
        Assert-DmsContainerSchemaEnvironment `
            -EnvironmentFilePath $resolvedEnvironmentFile `
            -EnvironmentValues $envValues `
            -ContainerName "ed-fi-api"
    }

    # Pass the fully resolved environment file (data-standard then engine overlay) so teardown uses the
    # same effective environment the setup composed, not the pre-overlay base.
    $teardownCommand = Get-DirectSetupTeardownCommand -DatabaseEngine $DatabaseEngine -EnvironmentFile $resolvedEnvironmentFile
    Write-Host "`nDMS E2E environment setup complete!" -ForegroundColor Green
    Write-Host "To tear down this environment, run: $teardownCommand" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
