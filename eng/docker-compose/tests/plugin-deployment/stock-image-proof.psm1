# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    The decisions the stock-image plugin proof makes, separated from the deployment that gathers
    what they decide about.

    Every function here takes recorded evidence and returns a verdict, so each one can be driven
    against the failure it is meant to tell apart without a container, a registry or a daemon. That
    separation is the point: the interesting question in this proof is never "did something fail"
    but "did it fail for the reason claimed", and a fetch that could not reach the feed, a DMS that
    started and then died, a core validation rejection, and a permission error that has nothing to
    do with a read-only mount all look like success to an assertion that only checks for failure.

    Stock-only. Invoke-PluginDeploymentCheck.ps1, which proves the same mechanism against a locally
    built image, is unchanged and does not import this.
#>

Set-StrictMode -Version Latest

# The container image reference the proof runs. Not a parameter: official stock acceptance is about
# these two repositories, and making them configurable would only create a way to prove the claim
# against something else.
$script:EdFiApiRepository = 'edfialliance/ed-fi-api'
$script:ConfigurationServiceRepository = 'edfialliance/ed-fi-api-configuration-service'

function Get-StockProofRepository {
    return [pscustomobject]@{
        EdFiApi              = $script:EdFiApiRepository
        ConfigurationService = $script:ConfigurationServiceRepository
    }
}

function Protect-StockProofText {
    <#
    .SYNOPSIS
    Redacts credential-bearing values before they reach evidence or a command log.

    .DESCRIPTION
    Evidence from this proof is written to disk and uploaded by CI, and the restore path carries a
    feed token. Redaction happens where the text is recorded rather than where it is produced, so a
    value cannot reach a file by way of a path nobody remembered to sanitize.

    Every known-secret value is replaced wherever it appears, including in the middle of a command
    line. The patterns below also catch the shapes a token takes when it was never passed through
    -Secret, which is the case that matters: a value nobody declared is exactly the one that leaks.
    #>
    param(
        [string] $Text,
        [string[]] $Secret = @()
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }

    $redacted = $Text

    foreach ($value in $Secret) {
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $redacted = $redacted.Replace($value, '***REDACTED***')
        }
    }

    # Shapes rather than known values: an Authorization header, a NuGet feed password or api key, a
    # PAT-looking query argument, and the credential segment of a URL.
    $pattern = @(
        # To end of line, not one token: "Authorization: Basic <value>" puts the scheme first, and
        # redacting only the first token leaves the credential sitting right beside it.
        '(?i)(authorization\s*:\s*).*'
        '(?i)(-{1,2}(?:password|api-?key|token)[= ])\S+'
        '(?i)((?:password|apikey|token|pat)["'':=]\s*)[^\s"'',;]+'
        '(?i)(https?://)[^/\s:@]+:[^/\s@]+@'
    )

    foreach ($expression in $pattern) {
        $redacted = [regex]::Replace($redacted, $expression, '$1***REDACTED***')
    }

    return $redacted
}

function Test-FetchFailedOnChecksum {
    <#
    .SYNOPSIS
    Decides whether the fetch-plugins service failed on the digest comparison specifically.

    .DESCRIPTION
    The wrong-digest scenario proves that a package whose bytes do not match the pinned SHA-256 is
    refused. An unreachable feed, a name that matched nothing, or a container that never ran also
    exit non-zero, and would let that scenario pass while proving nothing about digest verification
    at all. So the exit code is necessary and nowhere near sufficient: the log has to carry BusyBox
    sha256sum's own comparison failure, and must not carry a transport failure instead.
    #>
    param(
        [Nullable[int]] $ExitCode,
        [string] $Log
    )

    $reason = @()

    if ($null -eq $ExitCode) {
        $reason += 'the fetch service did not run, so nothing was verified'
    }
    elseif ($ExitCode -eq 0) {
        $reason += "the fetch service exited 0, so the wrong digest was accepted"
    }

    $text = if ($null -eq $Log) { '' } else { $Log }

    # sha256sum -c prints "<file>: FAILED" and then "WARNING: 1 computed checksum did NOT match".
    $checksum = $text -cmatch 'did NOT match' -or $text -cmatch ':\s*FAILED'

    # A transport failure reaches the same exit code by a different route, and saying so is the
    # difference between a proof and a coincidence.
    $transport =
        $text -match 'wget: ' -or
        $text -match 'bad address' -or
        $text -match 'Connection refused' -or
        $text -match 'network is unreachable' -or
        $text -match 'name does not resolve'

    if (-not $checksum) {
        $reason += 'the fetch log carries no checksum comparison failure'
    }

    if ($transport -and -not $checksum) {
        $reason += 'the fetch log carries a transport failure, so the package was never downloaded and its digest was never compared'
    }

    return [pscustomobject]@{
        Verified = ($reason.Count -eq 0)
        Reason   = ($reason -join '; ')
    }
}

function Test-DmsNeverStarted {
    <#
    .SYNOPSIS
    Decides whether the DMS container never started, as opposed to starting and then stopping.

    .DESCRIPTION
    What service_completed_successfully buys is that DMS is never started when the fetch step
    fails. A container that started and then exited is a different and much weaker outcome: the
    plugin root would have been mounted and the host would have run. Docker reports a
    never-started container's StartedAt as the zero timestamp, read as raw text, because
    ConvertFrom-Json turns an ISO-8601 string into a DateTime and the zero value then stops
    comparing equal to the string form, which silently inverts this check.
    #>
    param(
        [string] $Status,
        [string] $StartedAtRaw,
        [Nullable[int]] $ExitCode
    )

    $zero = '0001-01-01T00:00:00Z'

    if ([string]::IsNullOrWhiteSpace($Status)) {
        # No container at all is the strongest form of never started.
        return [pscustomobject]@{ Verified = $true; Reason = 'no DMS container was created' }
    }

    if ($StartedAtRaw -ceq $zero) {
        return [pscustomobject]@{ Verified = $true; Reason = "the DMS container was created but never started (status '$Status')" }
    }

    if ($Status -ceq 'exited' -or ($null -ne $ExitCode -and $ExitCode -ne 0)) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "the DMS container started at $StartedAtRaw and then exited, which is not the same as never starting: the host ran with the plugin root mounted"
        }
    }

    return [pscustomobject]@{
        Verified = $false
        Reason   = "the DMS container started at $StartedAtRaw (status '$Status')"
    }
}

function Test-LoadPluginsFailure {
    <#
    .SYNOPSIS
    Decides whether the startup status document records a failed plugin load naming the expected path.

    .DESCRIPTION
    The misspelled-allowlist scenario asserts a specific refusal, so the document is required rather
    than preferred: an absent or unreadable one is a failed assertion and never an excuse to fall
    back to the container log. The phase value is the PascalCase constant the host writes
    (DmsStartupPhases.LoadPlugins is the literal "LoadPlugins", serialized with no naming policy),
    not a kebab-case rendering of it.
    #>
    param(
        $StatusDocument,
        [string] $ExpectedPath,
        [Nullable[int]] $ExitCode
    )

    $reason = @()

    if ($null -eq $StatusDocument) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = 'the startup status document was missing or unreadable, so the refusal it is supposed to record cannot be asserted; the container log is diagnostic only'
        }
    }

    $phase = if ($null -ne $StatusDocument.PSObject.Properties['Phase']) { [string]$StatusDocument.Phase } else { '' }
    $state = if ($null -ne $StatusDocument.PSObject.Properties['State']) { [string]$StatusDocument.State } else { '' }
    $summary = @(
        if ($null -ne $StatusDocument.PSObject.Properties['Summary']) { [string]$StatusDocument.Summary }
        if ($null -ne $StatusDocument.PSObject.Properties['ErrorMessage']) { [string]$StatusDocument.ErrorMessage }
    ) -join ' '

    if ($phase -cne 'LoadPlugins') {
        $reason += "the status records phase '$phase' rather than 'LoadPlugins'"
    }

    if ($state -ceq 'Ready') {
        $reason += "the status records State 'Ready', so the host started rather than refusing"
    }
    elseif ($state -cne 'Failed') {
        $reason += "the status records State '$state' rather than a failure"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedPath) -and -not $summary.Contains($ExpectedPath)) {
        $reason += "the status does not name the expected path '$ExpectedPath'"
    }

    if ($null -ne $ExitCode -and $ExitCode -eq 0) {
        $reason += 'the DMS container exited 0, so the refusal did not stop startup'
    }

    return [pscustomobject]@{
        Verified = ($reason.Count -eq 0)
        Reason   = ($reason -join '; ')
    }
}

function Test-FixtureValidationFailure {
    <#
    .SYNOPSIS
    Decides whether an HTTP response is the fixture validator's rejection and not some other 400.

    .DESCRIPTION
    A generic 400 is the failure mode this scenario exists to rule out. So is an authentication or
    authorization failure, which would mean the request never reached a validator, and a core
    validation rejection, which would mean it reached the pipeline but not the plugin. The fixture's
    own message is the only thing that distinguishes its rejection from all three.
    #>
    param(
        [Nullable[int]] $StatusCode,
        [string] $Body,
        [Parameter(Mandatory)] [string] $ExpectedMessage,
        [string] $ExpectedPath,
        [ValidateSet('Path', 'Resource')] [string] $Arm = 'Path'
    )

    if ($null -eq $StatusCode) {
        return [pscustomobject]@{ Verified = $false; Reason = 'no response was recorded' }
    }

    if ($StatusCode -in @(401, 403)) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "the request was refused with HTTP $StatusCode, so it never reached a validator; this is an authentication or authorization failure rather than a validation outcome"
        }
    }

    if ($StatusCode -ne 400) {
        return [pscustomobject]@{ Verified = $false; Reason = "the response was HTTP $StatusCode rather than 400" }
    }

    $text = if ($null -eq $Body) { '' } else { $Body }
    $reason = @()

    if (-not $text.Contains($ExpectedMessage)) {
        $reason += "the 400 does not carry the fixture's message, so it is a different rejection: core validation reached this document before the plugin did, or the plugin never ran"
    }

    # The two arms are different response shapes, and a fixture rejection reported through the wrong
    # one would mean the failure was mapped incorrectly.
    $expectedArm = if ($Arm -ceq 'Path') { 'validationErrors' } else { 'errors' }

    if (-not $text.Contains($expectedArm)) {
        $reason += "the 400 does not carry a '$expectedArm' member, which is the arm a $Arm failure is reported through"
    }

    if ($Arm -ceq 'Path' -and -not [string]::IsNullOrWhiteSpace($ExpectedPath) -and -not $text.Contains($ExpectedPath)) {
        $reason += "the 400 does not name the JSON path '$ExpectedPath' the rejected value sits at"
    }

    return [pscustomobject]@{
        Verified = ($reason.Count -eq 0)
        Reason   = ($reason -join '; ')
    }
}

function Test-PluginMountReadOnly {
    <#
    .SYNOPSIS
    Decides whether /app/plugins is read-only as observed from inside the container.

    .DESCRIPTION
    Control 1 of the trust model is the one DMS cannot verify about itself, and docker inspect's RW
    flag describes what the daemon was asked for rather than what the process sees. So the evidence
    is the mount table and a write attempt, both executed inside the container.

    A write that failed for an unrelated reason proves nothing: a missing directory, a quota, or an
    ordinary permission denial on a writable mount would all fail. The refusal has to name a
    read-only file system. A probe that could not run at all is a failed assertion rather than a
    pass, because "the tool was missing" and "the mount is read-only" are indistinguishable from an
    exit code alone.
    #>
    param(
        [string] $MountTable,
        [Nullable[int]] $WriteExitCode,
        [string] $WriteOutput,
        [Nullable[int]] $MountTableExitCode
    )

    $reason = @()

    if ($null -eq $MountTableExitCode -or $MountTableExitCode -ne 0) {
        $reason += 'the mount table could not be read inside the container, so this control was not verified rather than verified as holding'
    }
    elseif ([string]::IsNullOrWhiteSpace($MountTable)) {
        $reason += 'the mount table names no /app/plugins mount'
    }
    else {
        # " /app/plugins " as a whole field, so /app/plugins-something cannot answer for it, and the
        # option list read as options rather than as a substring: "ro" must be an option, where a
        # naive match would accept "relatime".
        $entry = @($MountTable -split "`n" | Where-Object { $_ -match '\s/app/plugins\s' })

        if ($entry.Count -eq 0) {
            $reason += 'the mount table names no /app/plugins mount'
        }
        else {
            $option = @($entry[0] -split '\s+')[3]
            if ($null -eq $option -or 'ro' -cnotin @($option -split ',')) {
                $reason += "the /app/plugins mount is not read-only; its options are '$option'"
            }
        }
    }

    if ($null -eq $WriteExitCode) {
        $reason += 'no write attempt was recorded inside the container'
    }
    elseif ($WriteExitCode -eq 0) {
        $reason += 'a write into /app/plugins succeeded from inside the container'
    }
    else {
        $text = if ($null -eq $WriteOutput) { '' } else { $WriteOutput }

        if ($text -notmatch '(?i)read-only file system') {
            $reason += "the write failed for a reason other than a read-only file system: '$($text.Trim())'"
        }
    }

    return [pscustomobject]@{
        Verified = ($reason.Count -eq 0)
        Reason   = ($reason -join '; ')
    }
}

function Get-RemoteImageDigest {
    <#
    .SYNOPSIS
    Reads the descriptor digest a tag currently resolves to, out of buildx imagetools output.

    .DESCRIPTION
    A tag and a digest are two claims, and only a registry can say whether they are the same
    artifact. Two local pulls cannot: a local RepoDigests list accumulates every digest an image id
    has ever been pulled under, so membership there says the digest was seen at some point and
    nothing about what the tag points at now.

    This reads the index (manifest list) digest, which is what a repository digest names, rather
    than the platform-specific image id. An empty or unparseable response is a refusal, never a
    pass: the caller fails with an actionable diagnostic instead of degrading to a weaker claim.
    #>
    param(
        [string] $ImagetoolsOutput
    )

    if ([string]::IsNullOrWhiteSpace($ImagetoolsOutput)) {
        return $null
    }

    # `--format '{{json .Manifest}}'` is the shape asked for; a bare inspect prints a "Digest:"
    # line. Both are read, so the caller is not broken by a Buildx that changes its default layout.
    $manifest = try { $ImagetoolsOutput | ConvertFrom-Json } catch { $null }

    if ($null -ne $manifest -and
        $manifest.PSObject.Properties.Name -contains 'digest' -and
        -not [string]::IsNullOrWhiteSpace([string]$manifest.digest)) {
        return [string]$manifest.digest
    }

    $match = [regex]::Match($ImagetoolsOutput, '(?m)^\s*Digest:\s*(sha256:[0-9a-f]{64})\s*$')

    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Test-RemoteImageDescriptor {
    <#
    .SYNOPSIS
    Decides whether a tag resolves, in the registry, to the digest the pin names.
    #>
    param(
        [string] $Tag,
        [string] $PinnedDigest,
        [string] $ResolvedDigest,
        [bool] $ImagetoolsAvailable = $true
    )

    if (-not $ImagetoolsAvailable) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "docker buildx imagetools is not available, so the tag '$Tag' cannot be resolved against the registry. This proof claims a tag AND a digest, so it fails here rather than continuing on the digest alone. Install Buildx, or add docker/setup-buildx-action to the job."
        }
    }

    if ([string]::IsNullOrWhiteSpace($ResolvedDigest)) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "the registry returned no descriptor digest for tag '$Tag', so what that tag points at is unknown"
        }
    }

    if ($ResolvedDigest -cne $PinnedDigest) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "tag '$Tag' resolves to $ResolvedDigest in the registry, but the pin names $PinnedDigest. The tag has moved, or the pin describes a different artifact."
        }
    }

    return [pscustomobject]@{
        Verified = $true
        Reason   = "tag '$Tag' resolves to $PinnedDigest in the registry"
    }
}

function Test-BuildCommandAbsent {
    <#
    .SYNOPSIS
    Decides whether a recorded command log contains anything that builds an image.

    .DESCRIPTION
    The proof's central claim is that a published image runs third-party code with no image derived
    and no DMS rebuilt, so the harness must never build one. Packing the fixture and its contracts
    is permitted and is not a build of DMS; a docker build, a compose build, or build-dms.ps1's
    DockerBuild command is not.
    #>
    param(
        [string[]] $Command = @()
    )

    $offending = @($Command | Where-Object {
            $text = [string]$_
            $text -match '(?i)\bdocker(\.exe)?\s+(buildx\s+)?build\b' -or
            $text -match '(?i)\bdocker(\.exe)?\s+compose\b.*\bbuild\b' -or
            $text -match '(?i)build-dms\.ps1.*\bDockerBuild\b'
        })

    return [pscustomobject]@{
        Verified = ($offending.Count -eq 0)
        Reason   = if ($offending.Count -eq 0) {
            'no recorded command builds an image'
        }
        else {
            "the harness ran $($offending.Count) image-building command(s): $($offending -join ' | ')"
        }
    }
}

Export-ModuleMember -Function @(
    'Get-StockProofRepository'
    'Protect-StockProofText'
    'Test-FetchFailedOnChecksum'
    'Test-DmsNeverStarted'
    'Test-LoadPluginsFailure'
    'Test-FixtureValidationFailure'
    'Test-PluginMountReadOnly'
    'Get-RemoteImageDigest'
    'Test-RemoteImageDescriptor'
    'Test-BuildCommandAbsent'
)
