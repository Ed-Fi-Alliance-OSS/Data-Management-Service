# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    The environment file's authority over a DMS E2E setup flow's ApiSchema package surface: the
    pre-phase process guard that hands the file that authority, and the post-start verification that
    the started DMS container actually received it.
.DESCRIPTION
    Shared by both E2E setup wrappers (EdFi.DataManagementService.Tests.E2E and
    EdFi.InstanceManagement.Tests.E2E) so the direct and route-context flows guard and verify the same
    thing in the same way rather than carrying two copies of each.

    The provisioner reads SCHEMA_PACKAGES from the environment file only, so the E2E database is
    always stamped for the file's package surface. DMS receives its settings through Docker Compose,
    which resolves them ambient-first. When the two disagree the stack comes up healthy and then fails
    every data-plane request with an EffectiveSchemaHash mismatch, so the disagreement is reported
    here instead of as an HTTP 503 in every scenario of the suite.
#>

# Imported here rather than relied on from the caller's session, so the module resolves every command
# it calls regardless of what the invoking script happened to import: Resolve-DotenvFileSequentially
# (database-safety) and Get-SchemaPackagesFromEnvironmentFile (schema-package-utility, the same
# file-only reader the provision phase uses).
#
# Deliberately without -Force. -Force removes a module before re-importing it, and removal is
# session-wide: a caller that had already imported database-safety.psm1 into its own session state
# lost every command from it the moment this module loaded, and the E2E setup wrappers then failed at
# their first database-safety call after this import. Without -Force an already-loaded module is
# reused, so this import can only ADD command resolution for this module - never take it away from a
# caller - and it still loads the dependency when nothing else has.
Import-Module (Join-Path $PSScriptRoot "database-safety.psm1")
Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1")

function Invoke-WithDmsEnvironmentFileSchemaAuthority {
    <#
    .SYNOPSIS
        Runs a setup flow's Docker phases with the three schema package variables absent from this
        process, so Docker Compose must resolve them from the selected --env-file, and restores the
        caller's exact prior state afterward.
    .DESCRIPTION
        Compose gives process environment variables precedence over --env-file entries, and
        local-dms.yml resolves all three with a ${VAR:-default} fallback. Because ':-' substitutes the
        default for an empty value as well as an unset one, an ambient blank value silently wins over
        the environment file: the DMS container is created with AppSettings__UseApiSchemaPath=false and
        empty AppSettings__ApiSchemaPath/SCHEMA_PACKAGES, run.sh skips the package download entirely,
        and DMS loads only the image-baked schemas. Provisioning is not affected, because it reads
        SCHEMA_PACKAGES from the environment file only - so the provisioned database is stamped for the
        file's full package surface while DMS computes a different runtime hash, and every data-plane
        request fails with an EffectiveSchemaHash mismatch. That path is confirmed by construction from
        Compose's documented precedence; it is not a diagnosis of any particular reported incident, and
        this guard is cheap enough to hold regardless of which cause produced one.

        Callers guard EACH PHASE INVOCATION separately rather than wrapping their whole phase sequence
        in one call. The removal lasts only for the duration of the action, because the restore below
        has to put the caller's shell back the way it found it - so one call around the whole sequence
        removes the three names exactly once, before the first phase. A phase script runs in the same
        PowerShell process as the wrapper, and one that re-creates any of the three (start-local-dms.ps1
        does exactly that in bootstrap mode, deliberately) would then still be setting it for every
        later phase in that sequence, which is the state this guard exists to prevent. Guarding per
        phase re-applies the removal immediately before each phase, so a Compose call added to any
        phase later is covered by construction, and phases that do not read these three names from the
        process environment are unaffected by the removal.

        Defined in this shared module rather than copied into each E2E setup wrapper, for the same
        reason the verification below lives here: two copies drift, and only one of them gets the next
        fix. It remains a CALLER-side guard - start-local-dms.ps1 must not clear these names globally,
        because in bootstrap mode it sets them in-process on purpose so process precedence makes the
        staged .bootstrap/ApiSchema workspace authoritative. build-dms.ps1 imports this module and
        calls this function directly at every one of its schema-guarded call sites; the -Enabled
        parameter below is what lets it do that rather than keep a gating wrapper of its own.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidDefaultValueSwitchParameter', '', Justification = 'Guarding is the safe default: a call site that omits -Enabled must remove the three schema names, not silently run its Compose phase with them present.')]
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Action,

        # Whether to guard at all, for the callers that gate the removal. build-dms.ps1 gates most of
        # its call sites on a caller switch or on the E2E settings object, and those phases must run
        # WITHOUT the removal when the gate is off - the developer-stack bootstrap path depends on the
        # process precedence this otherwise removes.
        #
        # Defaults to ON, which is why it is a parameter here rather than a wrapper around this
        # function: the E2E setup wrappers call without it and get the guard, a new call site that
        # forgets it fails safe, and there is no second name for a caller's command lookup to bind
        # instead of this one.
        [switch] $Enabled = $true
    )

    if (-not $Enabled) {
        # A pass-through, deliberately: a gated-off call site must behave exactly as if the guard were
        # not there, leaving the three names as its phase found them.
        & $Action
        return
    }

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
            # value satisfies ${VAR:-default} - so an assignment-based clear can leave this guard
            # doing nothing at all, which is the bug it exists to prevent.
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
    <#
    .SYNOPSIS
        Classifies a container environment value without echoing it, so a failure message carries a
        fixed vocabulary instead of interpolated container text.
    .OUTPUTS
        [string] One of "<absent>", "<blank>", "true", "false", or "<set>".
    #>
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
    <#
    .SYNOPSIS
        Returns the container's SCHEMA_PACKAGES entries as a parsed array, or $null when the value is
        absent, blank, or not a JSON array.
    .DESCRIPTION
        The value itself is never returned to a caller that logs it: the entries only reach
        Get-DmsSchemaPackageIdentity, which is used for comparison alone.
    #>
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
    <#
    .SYNOPSIS
        Reduces ApiSchema package entries to sorted, comparable identities.
    .DESCRIPTION
        The identity is built from the three fields that decide which schema artifact is downloaded -
        name, version, and feedUrl - which is what run.sh and the provision phase's downloader both
        consume. A count comparison alone accepts a container whose packages differ in any of them,
        which is exactly the surface the E2E database was not provisioned for. A missing field
        normalizes to an empty string rather than throwing, so a malformed entry compares unequal
        instead of failing the verification.

        Identities from BOTH sides are compared, but only the environment FILE's identity is ever
        returned to a failure message - the surface-mismatch reason names the expected package at the
        mismatching index, because "they differ somehow" is not something a developer can act on. The
        CONTAINER's identity is never returned, so no container-supplied text can reach the console.
    #>
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
        runtime must match it. That includes the environment file's own USE_API_SCHEMA_PATH and
        API_SCHEMA_PATH: a file that declares packages but does not enable the ApiSchema path, or enables
        it without selecting a path, is internally inconsistent and guarantees the mismatch, so each is
        reported against the file rather than tolerated or reported against the container.
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

    # Cause-neutral by construction, and named for what it reports rather than for a cause. Every
    # branch below can be reached without any ambient value being involved - a wrong value in the file
    # itself, a stale container left by an earlier run, or an image whose baked settings differ all
    # produce the same disagreement - so the remediation offers the two actions that resolve those
    # reachable causes rather than naming one the verdict cannot establish.
    #
    # Both actions are reachable from where a developer already is. The stack is torn down through the
    # E2E suite's own teardown wrapper, so the container is REMOVED rather than reused, and the
    # subsequent setup re-creates it from the selected environment file; if that file is the wrong one
    # for the run, choosing another is the second action. Deliberately not a path to that wrapper: the
    # setup flows run their phases from eng/docker-compose, where no such relative path resolves, and
    # this module is shared by two suites that each have their own copy in their own directory.
    #
    # The advice this replaced asked for a re-run "from a shell that does not set USE_API_SCHEMA_PATH,
    # API_SCHEMA_PATH, and SCHEMA_PACKAGES", which sent developers after a cause that cannot apply:
    # the guard has already removed those three names from the process by the time this runs.
    $containerDisagreementRemediation = "The environment file and the started DMS container disagree. Tear the stack down with this E2E suite's teardown-local-dms.ps1 wrapper and re-run setup, so the DMS container is re-created from the selected environment file, or select a different -EnvironmentFile."
    $expectedPackageCount = $ExpectedPackageIdentity.Count

    if (-not $EnvironmentFileUsesApiSchemaPath) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the environment file declares $expectedPackageCount ApiSchema package(s) but does not set USE_API_SCHEMA_PATH=true, so the E2E database was provisioned for those packages while DMS was configured to load only the schemas baked into the image."
            Remediation = "Set USE_API_SCHEMA_PATH=true in the environment file so its declared packages are the runtime schema surface."
        }
    }

    # The symmetric file-side inconsistency, checked before any container value: a file that declares
    # packages and enables the ApiSchema path but selects no path leaves those packages nowhere to be
    # materialized. Reaching this only through the container's blank-path branch below would answer a
    # missing file value with $containerDisagreementRemediation, which asks for a teardown and a re-run
    # - and re-creating the container cannot fix a value the file never declared.
    if ([string]::IsNullOrWhiteSpace($EnvironmentFileApiSchemaPath)) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the environment file declares $expectedPackageCount ApiSchema package(s) but no API_SCHEMA_PATH, so the E2E database was provisioned for those packages while the environment file selected nowhere for DMS to materialize them."
            Remediation = "Set API_SCHEMA_PATH in the environment file so its declared packages have a materialization path."
        }
    }

    $useApiSchemaPathToken = Get-DmsSchemaEnvironmentToken -ContainerEnvironment $ContainerEnvironment -Key "AppSettings__UseApiSchemaPath"
    if ($useApiSchemaPathToken -ne "true") {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's AppSettings__UseApiSchemaPath is $useApiSchemaPathToken but the environment file declares $expectedPackageCount ApiSchema package(s), so DMS loaded only the schemas baked into the image while the E2E database was provisioned for those packages."
            Remediation = $containerDisagreementRemediation
        }
    }

    $apiSchemaPathToken = Get-DmsSchemaEnvironmentToken -ContainerEnvironment $ContainerEnvironment -Key "AppSettings__ApiSchemaPath"
    if ($apiSchemaPathToken -in @("<absent>", "<blank>")) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's AppSettings__ApiSchemaPath is $apiSchemaPathToken, so the declared ApiSchema packages have nowhere to be materialized."
            Remediation = $containerDisagreementRemediation
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
            Remediation = $containerDisagreementRemediation
        }
    }

    $containerPackages = Get-DmsContainerSchemaPackage -ContainerEnvironment $ContainerEnvironment
    if ($null -eq $containerPackages) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container's SCHEMA_PACKAGES is absent, blank, or not a JSON array, but the environment file declares $expectedPackageCount ApiSchema package(s)."
            Remediation = $containerDisagreementRemediation
        }
    }

    if ($containerPackages.Count -ne $expectedPackageCount) {
        return [pscustomobject]@{
            ShouldFail  = $true
            Reason      = "the DMS container received $($containerPackages.Count) ApiSchema package(s) but the E2E database was provisioned for the environment file's $expectedPackageCount."
            Remediation = $containerDisagreementRemediation
        }
    }

    # Counts agreeing is not the surface agreeing. A container carrying the same number of packages at
    # a different name, version, or feed URL downloads different schemas, computes a different runtime
    # hash, and fails every data-plane request exactly as a count mismatch would. Both sides are already
    # sorted, so this is a positional comparison of equal-length identity lists.
    #
    # The FILE-side identity at the mismatching index is named, so the failure says which package the
    # database was provisioned for rather than only that something differs - "differ by name, version,
    # or feed URL" alone leaves a developer to diff two package sets by hand, neither of which the
    # message showed them. The CONTAINER-side identity stays unechoed: it is the untrusted half, and
    # naming the expected package is what makes the failure actionable anyway.
    $containerPackageIdentity = Get-DmsSchemaPackageIdentity -Package $containerPackages
    for ($index = 0; $index -lt $expectedPackageCount; $index++) {
        if (-not [string]::Equals(
                $containerPackageIdentity[$index],
                $ExpectedPackageIdentity[$index],
                [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                ShouldFail  = $true
                Reason      = "the DMS container's $expectedPackageCount ApiSchema package(s) differ from the environment file's declared packages by name, version, or feed URL, so DMS is loading a different schema surface than the E2E database was provisioned for. The first difference is at sorted position $($index + 1) of $expectedPackageCount, where the environment file declares $($ExpectedPackageIdentity[$index])."
                Remediation = $containerDisagreementRemediation
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
    <#
    .SYNOPSIS
        Reads a container's environment into a key/value map.
    .DESCRIPTION
        Fails closed: an inspect that does not succeed is an inability to verify, never a pass.

        Exported, because build-dms.ps1's runtime effective-schema-hash gate reads the same
        'docker inspect' output to print the container's schema settings before it compares hashes.
        That script carried a second copy of this reader; one implementation means the next fix
        cannot land in only one of them.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $ContainerName
    )

    $environmentJson = docker inspect $ContainerName --format '{{json .Config.Env}}'

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect Docker container '$ContainerName' to verify its schema environment."
    }

    # A PowerShell hashtable, so its key lookups are case-INSENSITIVE, and that is deliberate here.
    # Docker Compose is the sole writer of this container's environment and emits the exact
    # AppSettings__* spelling local-dms.yml declares, so the gate never has two spellings to tell
    # apart and a case-sensitive map would only add a way for the gate to miss its own keys. Casing
    # matters for the VALUES, which is why Get-DmsSchemaEnvironmentToken compares those Ordinal.
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

function Get-DmsEnvironmentFileDeclaredValue {
    <#
    .SYNOPSIS
        Returns the value an environment FILE declares for a name, as Docker Compose froze it while
        reading that file.
    .DESCRIPTION
        Docker Compose resolves an --env-file in declaration order: each value is resolved against
        what is in effect at its own line, and a value it has resolved is terminal. A later
        re-declaration of a name that an earlier line referenced therefore cannot change that earlier
        value. A collapsed key/value map cannot express this - it keeps only the final value of every
        name - so reading an expected value from one resolves references against declarations Compose
        had not yet read, and reports a correctly started stack as a mismatch.

        The LAST declaration of the requested name wins, which is what the compose file itself sees.
        Read from Declarations rather than the Effective map deliberately: Effective applies ambient
        precedence to the requested key, and the setup guard has removed these names from the process
        before this runs, so the expected side must come from the file alone. Taking Effective would
        let a shell value that Compose never saw define what "correct" means.

        Names REFERENCED by a declaration are still resolved ambient-first inside the sequential
        resolver, which is exactly what Compose does.
    #>
    param(
        # A Resolve-DotenvFileSequentially result.
        [Parameter(Mandatory)]
        [object] $ResolvedEnvironmentFile,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $DefaultValue
    )

    # Ordinal: a dotenv identifier is case-sensitive on the Linux CI and runtime path, and
    # PowerShell's -eq is not, so a lowercase decoy declaration must not satisfy an uppercase lookup.
    $declaration = @(
        $ResolvedEnvironmentFile.Declarations |
            Where-Object { [string]::Equals($_.Key, $Name, [System.StringComparison]::Ordinal) }
    ) | Select-Object -Last 1

    # Absent and resolving-to-nothing both fall back to the documented default, matching the
    # file-only reader this replaced.
    if ($null -eq $declaration -or [string]::IsNullOrWhiteSpace($declaration.ResolvedValue)) {
        return $DefaultValue
    }

    return [string]$declaration.ResolvedValue
}

function Assert-DmsContainerSchemaEnvironment {
    <#
    .SYNOPSIS
        Fails the setup when the started DMS container's schema environment disagrees with the
        environment file the E2E database was provisioned from, so this class of mismatch surfaces here
        instead of as HTTP 503 EffectiveSchemaHash failures in every scenario of the suite.
    .DESCRIPTION
        Verification is at the CONFIGURATION level - the container's schema settings against the
        environment file's - rather than at the effective-schema-hash level, which is the stronger
        check. The stronger one is not available on this path: the direct setup wrappers do not
        capture the provisioned hash that provision-e2e-database.ps1 reports, so there is nothing
        here to compare a runtime hash against. build-dms.ps1 E2ETest does capture it and already
        gates on the hash comparison, so that path is covered by the stronger check and this one adds
        the same protection to the direct wrappers without reworking their phase output capture.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Reports setup progress to the console of a host-oriented setup script, matching its surrounding output.')]
    param(
        [Parameter(Mandatory)]
        [string] $EnvironmentFilePath,

        [Parameter(Mandatory)]
        [string] $ContainerName
    )

    # Both expectations come from the environment FILE. Get-SchemaPackagesFromEnvironmentFile is the
    # same reader the provision phase used and it throws unless at least one package is declared, so a
    # malformed or empty declaration has already failed provisioning rather than being treated as
    # acceptable here. Get-ComposeResolvedEnvValue must not be used for either scalar, because
    # resolving the expected side ambient-first would let the very override this gate exists to catch
    # decide what "correct" means.
    #
    # Resolve-DotenvFileSequentially is this repository's model of how Compose actually reads an
    # --env-file: line by line, in declaration order, so each value is frozen against what was in
    # effect at its own line. It is the single canonical parser for that, which is why the scalars are
    # read through it rather than through another normalization step layered on a collapsed map.
    $declaredPackages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $EnvironmentFilePath)

    # Resolved to an absolute path first: Resolve-DotenvFileSequentially reads through
    # [System.IO.File], which resolves a relative path against the PROCESS working directory rather
    # than PowerShell's current location. Get-SchemaPackagesFromEnvironmentFile above reads with
    # Get-Content, which honours the location, so it has always accepted either form.
    $sequentialEnvironmentFile = Resolve-DotenvFileSequentially `
        -Path (Resolve-Path -LiteralPath $EnvironmentFilePath).ProviderPath

    # Ordinal against lowercase 'true', matching run.sh's byte-exact gate. Compose passes the resolved
    # value through verbatim, so a file declaring USE_API_SCHEMA_PATH=TRUE yields a container that
    # skips the package download while provisioning still stamps the file's packages; reporting that
    # against the file, with the remediation that names the file, is the actionable failure. The
    # sequential resolution above already applied Compose's quote, inline-comment and reference
    # semantics, so "true", 'true' and a reference that resolves to true all pass, and only the casing
    # is significant.
    $environmentFileUsesApiSchemaPath = [string]::Equals(
        (Get-DmsEnvironmentFileDeclaredValue `
            -ResolvedEnvironmentFile $sequentialEnvironmentFile `
            -Name "USE_API_SCHEMA_PATH" `
            -DefaultValue "false"),
        "true",
        [System.StringComparison]::Ordinal
    )
    $environmentFileApiSchemaPath = Get-DmsEnvironmentFileDeclaredValue `
        -ResolvedEnvironmentFile $sequentialEnvironmentFile `
        -Name "API_SCHEMA_PATH" `
        -DefaultValue ""

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

# Only the three commands a caller outside this module invokes: the pre-phase guard (both E2E setup
# wrappers, and build-dms.ps1, which passes -Enabled at its gated call sites), the post-start
# verification (both wrappers), and the container-environment reader (build-dms.ps1's runtime hash
# gate, which reads the same 'docker inspect' output to print the container's schema settings before
# comparing hashes). Get-DmsContainerEnvironment is exported because build-dms.ps1 is a real external
# caller of it, not for testability: it used to have a second copy of this reader under its own name.
#
# Nothing else above is exported. The remaining functions are internals of these three, and the tests
# reach them by extracting the function text through the AST and dot-sourcing it, so none of them
# needs an export - and a narrower surface is also less of it reachable from the E2E setup wrappers
# build-dms.ps1 invokes in-process, whose scope chain is searched before this module's exports.
Export-ModuleMember -Function `
    Invoke-WithDmsEnvironmentFileSchemaAuthority, `
    Assert-DmsContainerSchemaEnvironment, `
    Get-DmsContainerEnvironment
