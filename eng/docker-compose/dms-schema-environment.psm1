# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Post-start verification that a started DMS container's ApiSchema package settings agree with the
    environment file the E2E database was provisioned from.
.DESCRIPTION
    Shared by both E2E setup wrappers (EdFi.DataManagementService.Tests.E2E and
    EdFi.InstanceManagement.Tests.E2E) so the direct and route-context flows verify the same thing in
    the same way rather than carrying two copies of the check.

    The provisioner reads SCHEMA_PACKAGES from the environment file only, so the E2E database is
    always stamped for the file's package surface. DMS receives its settings through Docker Compose,
    which resolves them ambient-first. When the two disagree the stack comes up healthy and then fails
    every data-plane request with an EffectiveSchemaHash mismatch, so the disagreement is reported
    here instead of as an HTTP 503 in every scenario of the suite.
#>

# Imported here rather than relied on from the caller's session, so the module resolves every command
# it calls regardless of what the invoking script happened to import: Get-EnvValue (env-utility),
# Resolve-ComposeEnvRawValue (database-safety), and Get-SchemaPackagesFromEnvironmentFile
# (schema-package-utility, the same file-only reader the provision phase uses).
Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "database-safety.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1") -Force

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

        The identities are only ever compared; they are never returned to a failure message, so no
        container-supplied text can reach the console.
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

    # Cause-neutral by construction. Every branch below can be reached without any ambient value being
    # involved - a wrong value in the file itself, a stale container, or an image whose settings differ
    # all produce the same disagreement - so the remediation states what disagrees and offers the two
    # actions that resolve it, rather than naming a cause the verdict cannot establish.
    $ambientRemediation = "The environment file and the started DMS container disagree. Re-run setup from a shell that does not set USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES, or select a different -EnvironmentFile."
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
    <#
    .SYNOPSIS
        Reads a container's environment into a key/value map.
    .DESCRIPTION
        Fails closed: an inspect that does not succeed is an inability to verify, never a pass.
    #>
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
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Reports setup progress to the console of a host-oriented setup script, matching its surrounding output.')]
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
    # Resolve-ComposeEnvRawValue is not a Compose-precedence reader either: it takes the file's raw
    # value directly and applies the value semantics Compose gives a single --env-file entry -
    # surrounding quotes stripped, an inline comment dropped, and ${VAR}/$VAR references resolved
    # (single-quoted values stay literal). All three are legal Compose syntax that Compose itself
    # applies before the container sees the value, so a declaration using any of them is compared as
    # the container actually received it rather than failing on raw file text.
    $declaredPackages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $EnvironmentFilePath)
    # Ordinal against lowercase 'true', matching run.sh's byte-exact gate. Compose passes the resolved
    # value through verbatim, so a file declaring USE_API_SCHEMA_PATH=TRUE yields a container that
    # skips the package download while provisioning still stamps the file's packages; reporting that
    # against the file, with the remediation that names the file, is the actionable failure. The
    # quote/comment/reference normalization still runs first, so "true", 'true' and ${SOMETHING} that
    # resolves to true all pass, and only the casing is significant.
    $environmentFileUsesApiSchemaPath = [string]::Equals(
        (Resolve-ComposeEnvRawValue `
            -EnvironmentValues $EnvironmentValues `
            -RawValue (Get-EnvValue -EnvValues $EnvironmentValues -Name "USE_API_SCHEMA_PATH" -DefaultValue "false")),
        "true",
        [System.StringComparison]::Ordinal
    )
    $environmentFileApiSchemaPath = Resolve-ComposeEnvRawValue `
        -EnvironmentValues $EnvironmentValues `
        -RawValue (Get-EnvValue -EnvValues $EnvironmentValues -Name "API_SCHEMA_PATH" -DefaultValue "")

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

Export-ModuleMember -Function `
    Get-DmsSchemaEnvironmentToken, `
    Get-DmsContainerSchemaPackage, `
    Get-DmsSchemaPackageIdentity, `
    Get-DmsSchemaEnvironmentVerdict, `
    Get-DmsContainerEnvironment, `
    Assert-DmsContainerSchemaEnvironment
