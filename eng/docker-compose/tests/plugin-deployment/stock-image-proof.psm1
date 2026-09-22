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

# Remove-EnvFileKeys, which understands both a scalar declaration and a multi-line quoted block.
# Composing the deployment's environment file means removing what the base already says about a
# governed key rather than appending over it, and that helper already knows both shapes.
# Deliberately not -Force: this import binds into this module's scope, and forcing it would unload
# the copy a caller had already imported into its own session.
Import-Module (Join-Path $PSScriptRoot '../../env-utility.psm1') -DisableNameChecking

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
        [Nullable[int]] $ExitCode,

        # Whether the container list this status was read from was actually obtained. An empty
        # status means "absent" only when a successful enumeration said so; when the enumeration
        # itself failed it means "unknown", and the two must not collapse into the same answer.
        [bool] $EnumerationSucceeded = $true
    )

    $zero = '0001-01-01T00:00:00Z'

    if (-not $EnumerationSucceeded) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = 'the container list could not be read, so whether a DMS container was created is unknown; a failed enumeration is not evidence of absence'
        }
    }

    if ([string]::IsNullOrWhiteSpace($Status)) {
        # No container at all is the strongest form of never started, but only because a successful
        # enumeration above established that it is absent rather than unreadable.
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

    if ([string]::IsNullOrWhiteSpace($ExpectedPath)) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = 'no expected path was supplied, and this scenario asserts that the refusal names the path the misspelled allowlist entry pointed at'
        }
    }

    # The scenario asserts that DMS exits, so the exit evidence is required rather than optional.
    # Absent evidence most often means an inspect failed, and an inspect that failed is not a
    # container that exited.
    if ($null -eq $ExitCode) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = 'no exit code was recorded for the DMS container, so that it stopped is unestablished; a failed inspection is not proof of an exit'
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

    if (-not $summary.Contains($ExpectedPath)) {
        $reason += "the status does not name the expected path '$ExpectedPath'"
    }

    if ($ExitCode -eq 0) {
        $reason += 'the DMS container exited 0, so the refusal did not stop startup'
    }

    return [pscustomobject]@{
        Verified = ($reason.Count -eq 0)
        Reason   = ($reason -join '; ')
    }
}

function Get-JsonMember {
    <#
    .SYNOPSIS
    Reads one member of a parsed JSON object, preserving its shape.

    .DESCRIPTION
    Two PowerShell behaviours make a direct read wrong here, and both turn an array into a string.

    A member is read through the property collection rather than as $object.$name, because a
    JSONPath key carries dots and member access on a variable holding one does not resolve to the
    property whose name it is.

    The value is returned comma-wrapped, because a bare return is enumerated by the pipeline and a
    one-element array arrives at the caller as its element. An arm carrying exactly one message is
    the ordinary case in a validation response, so this is not an edge.
    #>
    param($Object, [Parameter(Mandatory)] [string] $Name)

    if ($null -eq $Object -or $Object -isnot [psobject]) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]

    if ($null -eq $property) {
        return $null
    }

    return , $property.Value
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
        [string] $ExpectedMessage,
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

    # An assertion with nothing to assert passes everything, so an absent expectation is a failure
    # of the caller rather than a pass for the response.
    if ([string]::IsNullOrWhiteSpace($ExpectedMessage)) {
        return [pscustomobject]@{ Verified = $false; Reason = 'no expected fixture message was supplied, so this response was not checked against anything' }
    }

    if ($Arm -ceq 'Path' -and [string]::IsNullOrWhiteSpace($ExpectedPath)) {
        return [pscustomobject]@{ Verified = $false; Reason = 'no expected JSON path was supplied, and a path-arm failure is identified by the path it is keyed under' }
    }

    # Parsed, not searched. A DMS write-path 400 carries BOTH arms every time - a path failure
    # leaves errors empty rather than absent, and a document-level failure leaves validationErrors
    # an empty object - so every keyword this could look for is present in either case. Substring
    # presence therefore says nothing about which arm reported what, and a body whose expected path
    # carries an unrelated message while the fixture's text sits under the other arm would pass.
    $document = try { $Body | ConvertFrom-Json -ErrorAction Stop } catch { $null }

    if ($null -eq $document -or $document -isnot [psobject]) {
        return [pscustomobject]@{ Verified = $false; Reason = 'the 400 body is not a JSON object, so no arm could be read from it' }
    }

    $reason = @()

    # Read through Get-JsonMember below rather than with an if-expression. Assigning the result of
    # an `if` sends it through the pipeline, which enumerates a one-element array into its element,
    # and an arm carrying exactly one message is the ordinary case here.
    if ($Arm -ceq 'Path') {
        $validationErrors = Get-JsonMember -Object $document -Name 'validationErrors'

        if ($null -eq $validationErrors -or $validationErrors -isnot [psobject] -or $validationErrors -is [System.Collections.IList]) {
            $reason += "the 400 carries no validationErrors object, which is the arm a path failure is reported through"
        }
        else {
            $entry = Get-JsonMember -Object $validationErrors -Name $ExpectedPath

            if ($null -eq $entry) {
                $reason += "the 400's validationErrors carries no entry for '$ExpectedPath', so nothing was rejected at the path the token sits at"
            }
            elseif ($entry -isnot [System.Collections.IList]) {
                $reason += "the 400's validationErrors entry for '$ExpectedPath' is not an array of messages"
            }
            # Exact equality, not containment: a longer message that merely includes the fixture's
            # text is a different message, and a substring match would accept it.
            elseif ($ExpectedMessage -cnotin @($entry | ForEach-Object { [string]$_ })) {
                $reason += "the 400's validationErrors entry for '$ExpectedPath' does not carry the fixture's exact message, so it is a different rejection: core validation reached this document before the plugin did, or the plugin never ran"
            }
        }
    }
    else {
        $errors = Get-JsonMember -Object $document -Name 'errors'

        if ($null -eq $errors -or $errors -isnot [System.Collections.IList]) {
            $reason += "the 400 carries no errors array, which is the arm a document-level failure is reported through"
        }
        elseif ($ExpectedMessage -cnotin @($errors | ForEach-Object { [string]$_ })) {
            $reason += "the 400's errors array does not carry the fixture's exact message, so it is a different rejection"
        }
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

    The command is read as a command rather than searched for a word. "build" appears inside
    --no-build, which is the strongest correct form of a compose up, and inside any file or
    directory path that happens to contain it; treating either as a build would refuse exactly the
    invocations this proof wants.

    This covers the commands the harness itself records. It says nothing about what a process the
    harness launches goes on to run, which is why the deployed container's image identity is the
    evidence for the no-build claim and this is the guard on the harness's own behaviour.
    #>
    param(
        [string[]] $Command = @()
    )

    # Options that take a separate value, so the token after them is that value and never a
    # subcommand: -f build.yml must not be read as the build subcommand.
    $valueOption = @('-f', '--file', '-p', '--project-name', '--project-directory', '--env-file', '--profile', '-c', '--context')

    function Test-OneCommand([string]$Text) {
        if ([string]::IsNullOrWhiteSpace($Text)) {
            return $false
        }

        if ($Text -match '(?i)build-dms\.ps1.*\bDockerBuild\b') {
            return $true
        }

        $token = @($Text -split '\s+' | Where-Object { -not [string]::IsNullOrEmpty($_) })
        $dockerAt = -1

        for ($i = 0; $i -lt $token.Count; $i++) {
            if ($token[$i] -match '(?i)(^|[\\/])docker(\.exe)?$') {
                $dockerAt = $i
                break
            }
        }

        if ($dockerAt -lt 0) {
            return $false
        }

        # The first bare word after `docker`, skipping global options and their values, is the
        # command: build, buildx, compose, pull and so on.
        $next = $dockerAt + 1
        while ($next -lt $token.Count -and $token[$next].StartsWith('-')) {
            if ($token[$next] -in $valueOption) { $next++ }
            $next++
        }

        if ($next -ge $token.Count) {
            return $false
        }

        $command = $token[$next]

        if ($command -ceq 'build') {
            return $true
        }

        if ($command -ceq 'buildx') {
            $sub = $next + 1
            while ($sub -lt $token.Count -and $token[$sub].StartsWith('-')) { $sub++ }
            return ($sub -lt $token.Count -and $token[$sub] -ceq 'build')
        }

        if ($command -ceq 'compose') {
            $sub = $next + 1
            while ($sub -lt $token.Count -and $token[$sub].StartsWith('-')) {
                if ($token[$sub] -in $valueOption) { $sub++ }
                $sub++
            }

            if ($sub -lt $token.Count -and $token[$sub] -ceq 'build') {
                return $true
            }

            # `up --build` rebuilds before starting. Matched as a whole token so --no-build, which
            # is the opposite instruction, cannot satisfy it.
            return (@($token[($sub)..($token.Count - 1)] | Where-Object { $_ -ceq '--build' }).Count -gt 0)
        }

        return $false
    }

    $offending = @($Command | Where-Object { Test-OneCommand ([string]$_) })

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

# -------------------------------------------------------------------------------------------------
# Orchestration decisions: what the harness is allowed to touch, and what it must put back.
# -------------------------------------------------------------------------------------------------

function Get-OccupiedHostResource {
    <#
    .SYNOPSIS
    Lists everything that would make this deployment collide with something already on the host.

    .DESCRIPTION
    The published stack claims resources whose names are fixed, so a project rename buys nothing:
    postgresql.yml declares container_name dms-postgresql, published-config.yml declares
    ed-fi-api-config-service, swagger-ui.yml declares ed-fi-api-swagger-ui and keycloak.yml declares
    dms-keycloak. It also claims a compose project, that project's named volumes, host ports, the
    shared external dms network, and eng/docker-compose/.bootstrap, which is one fixed path shared
    with the local stack.

    Every one of those is refused when it is already there. There is no override: the harness is
    destructive to what it does claim, and the only safe answer to "somebody else is using this" is
    to stop, name the thing, and let a human decide.

    The external network is deliberately NOT refused for existing. It is shared infrastructure that
    other stacks join and that this run neither creates nor removes. What is refused is a container
    attached to it that this run did not create, because that is a live neighbour rather than an
    empty network.
    #>
    param(
        [string[]] $ExistingContainerName = @(),
        [string[]] $ProjectContainer = @(),
        [string[]] $ProjectVolume = @(),
        [string[]] $NetworkAttachment = @(),
        [bool] $BootstrapPresent = $false,
        [int[]] $BoundPort = @(),
        [int[]] $RequiredPort = @(),

        # Inventories that could not be taken. An empty result from a command that failed is not an
        # empty inventory, and reading it as one is how a preflight reports a clear host because
        # Docker was unreachable. Each entry here is a blocker in its own right.
        [string[]] $InventoryFailure = @()
    )

    # The names the compose files hard-code, which no project name can move.
    $reservedName = @('dms-postgresql', 'ed-fi-api-config-service', 'ed-fi-api-swagger-ui', 'dms-keycloak', 'ed-fi-api')

    $blocker = @()

    foreach ($failure in $InventoryFailure) {
        $blocker += "the host could not be inspected: $failure. A failed inventory is not an empty one, so this run stops rather than assuming nothing is in the way"
    }

    foreach ($name in @($ExistingContainerName | Where-Object { $_ -in $reservedName })) {
        $blocker += "a container named '$name' already exists, and the compose files that declare it use that exact name regardless of project"
    }

    foreach ($name in $ProjectContainer) {
        $blocker += "the compose project already has a container '$name', so a previous run was not torn down"
    }

    foreach ($name in $ProjectVolume) {
        $blocker += "the compose project already has a volume '$name', which would carry state into this run"
    }

    foreach ($name in $NetworkAttachment) {
        $blocker += "container '$name' is attached to the shared external network and was not created by this run"
    }

    if ($BootstrapPresent) {
        $blocker += 'eng/docker-compose/.bootstrap already exists, and it is one fixed path shared with the local stack rather than something this run can own'
    }

    foreach ($port in @($RequiredPort | Where-Object { $_ -in $BoundPort })) {
        $blocker += "host port $port is already bound"
    }

    return [pscustomobject]@{
        Available = ($blocker.Count -eq 0)
        Blocker   = $blocker
    }
}

function Test-PreparedSchemaIdentity {
    <#
    .SYNOPSIS
    Decides whether the schema packages a bootstrap actually staged are the ones the pin names.

    .DESCRIPTION
    prepare-dms-schema.ps1 records what it staged into .bootstrap/bootstrap-manifest.json as
    schema.selectedPackages, one "<packageId>@<version>" string per package, built from the
    SCHEMA_PACKAGES entries it resolved. That is the only place the prepared set is stated, and
    comparing it to the pin is what makes "the proof ran against the schema content the pin names"
    a checked claim rather than an assumption about an environment variable.

    The raw property is examined rather than wrapped in @(). A scalar wrapped that way becomes a
    one-element array and a manifest recording a single string would read as a well-formed
    one-package set, which is exactly the shape a truncated or hand-edited manifest takes.

    Comparison is ordinal throughout. The identity is the recorded spelling: a differing case in an
    id, or a version written differently, is a different package as far as this check is concerned,
    because it is a different string in the manifest that a later reader would have to reconcile.
    #>
    param(
        # The raw schema.selectedPackages value, passed without coercion.
        $Prepared,

        # The pin's provisioning.schemaPackages entries.
        $PinnedPackage
    )

    $ordinal = [System.StringComparer]::Ordinal

    # Comma-wrapped: an empty array returned bare is unwrapped to nothing, and every caller below
    # reads .Length off the result.
    function Get-OrdinalSorted([string[]]$Value) {
        $copy = [string[]]::new($Value.Length)
        [Array]::Copy($Value, $copy, $Value.Length)
        [Array]::Sort($copy, $ordinal)
        return , $copy
    }

    $expected = @(
        foreach ($package in @($PinnedPackage)) {
            "$($package.name)@$($package.version)"
        }
    )

    $empty = [string[]]@()

    if ($null -eq $Prepared) {
        return [pscustomobject]@{
            Verified = $false
            Observed = $empty
            Missing  = (Get-OrdinalSorted ([string[]]$expected))
            Extra    = $empty
            Reason   = 'the bootstrap manifest records no schema.selectedPackages, so what was staged is unknown'
        }
    }

    # A JSON array and nothing else. A string is enumerable but is not an array, and an object is
    # neither; both would otherwise be coerced into a plausible-looking one-element set.
    if ($Prepared -is [string] -or $Prepared -isnot [System.Array]) {
        return [pscustomobject]@{
            Verified = $false
            Observed = $empty
            Missing  = (Get-OrdinalSorted ([string[]]$expected))
            Extra    = $empty
            Reason   = "the bootstrap manifest's schema.selectedPackages is a $($Prepared.GetType().Name) rather than an array"
        }
    }

    if ($Prepared.Length -eq 0) {
        return [pscustomobject]@{
            Verified = $false
            Observed = $empty
            Missing  = (Get-OrdinalSorted ([string[]]$expected))
            Extra    = $empty
            Reason   = 'the bootstrap manifest staged no schema packages at all'
        }
    }

    $observed = [System.Collections.Generic.List[string]]::new()
    $malformed = [System.Collections.Generic.List[string]]::new()

    for ($index = 0; $index -lt $Prepared.Length; $index++) {
        $entry = $Prepared[$index]

        if ($entry -isnot [string]) {
            $malformed.Add("[$index] is a $(if ($null -eq $entry) { 'null' } else { $entry.GetType().Name }) rather than a string")
            continue
        }

        # One '@' separating a non-empty id from a non-empty version, which is the shape
        # prepare-dms-schema.ps1 writes.
        if ($entry -cnotmatch '^[^@\s]+@[^@\s]+$') {
            $malformed.Add("[$index] '$entry' is not a <packageId>@<version> identity")
            continue
        }

        $observed.Add($entry)
    }

    if ($malformed.Count -gt 0) {
        return [pscustomobject]@{
            Verified = $false
            Observed = (Get-OrdinalSorted $observed.ToArray())
            Missing  = $empty
            Extra    = $empty
            Reason   = "the bootstrap manifest's schema.selectedPackages carries malformed entries: $($malformed -join '; ')"
        }
    }

    $duplicate = @(
        $observed | Group-Object -CaseSensitive |
            Where-Object { $_.Count -gt 1 } |
            ForEach-Object { $_.Name }
    )

    if ($duplicate.Count -gt 0) {
        return [pscustomobject]@{
            Verified = $false
            Observed = (Get-OrdinalSorted $observed.ToArray())
            Missing  = $empty
            Extra    = $empty
            Reason   = "the bootstrap manifest stages $($duplicate -join ', ') more than once"
        }
    }

    $expectedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$expected, $ordinal)
    $observedSet = [System.Collections.Generic.HashSet[string]]::new($observed.ToArray(), $ordinal)

    $missing = (Get-OrdinalSorted (@($expected | Where-Object { -not $observedSet.Contains($_) })))
    $extra = (Get-OrdinalSorted (@($observed | Where-Object { -not $expectedSet.Contains($_) })))

    $reason = @()
    if ($missing.Length -gt 0) { $reason += "the pin names $($missing -join ', ') but the bootstrap did not stage them" }
    if ($extra.Length -gt 0) { $reason += "the bootstrap staged $($extra -join ', ') which the pin does not name" }

    return [pscustomobject]@{
        Verified = ($missing.Length -eq 0 -and $extra.Length -eq 0)
        Observed = (Get-OrdinalSorted $observed.ToArray())
        Missing  = $missing
        Extra    = $extra
        Reason   = if ($reason.Count -eq 0) { "the bootstrap staged exactly the $($observed.Count) package(s) the pin names" } else { $reason -join '; ' }
    }
}

function Get-NetworkAttachmentInventory {
    <#
    .SYNOPSIS
    Who is attached to the shared external network, or why that could not be established.

    .DESCRIPTION
    An absent network really does mean nothing is attached, but it has to be established by a
    successful enumeration rather than assumed from a failed inspect. A daemon that is down, or a
    permission error, fails the inspect too, and treating that as "no neighbours" is how a run
    starts on a host it was never allowed to see.
    #>
    param(
        [Parameter(Mandatory)] [string] $NetworkName,
        [Nullable[int]] $ListExitCode,
        [string[]] $ListedNetwork = @(),
        [Nullable[int]] $InspectExitCode,
        [string[]] $InspectedAttachment = @()
    )

    if ($null -eq $ListExitCode -or $ListExitCode -ne 0) {
        return [pscustomobject]@{
            Attachment = @()
            Failure    = "the network list could not be read, so whether '$NetworkName' exists is unknown"
        }
    }

    if ($NetworkName -cnotin $ListedNetwork) {
        # Established, not assumed: the enumeration succeeded and did not name it.
        return [pscustomobject]@{ Attachment = @(); Failure = $null }
    }

    if ($null -eq $InspectExitCode -or $InspectExitCode -ne 0) {
        return [pscustomobject]@{
            Attachment = @()
            Failure    = "network '$NetworkName' exists but could not be inspected, so whether anything is attached to it is unknown"
        }
    }

    return [pscustomobject]@{ Attachment = @($InspectedAttachment); Failure = $null }
}

function Test-LocalPortAvailable {
    <#
    .SYNOPSIS
    Whether a TCP port can actually be bound on the loopback interface.

    .DESCRIPTION
    The container inventory only sees Docker's own publications. A port held by anything else -
    another service, a debugger, an IDE - is invisible to it, and the deployment would fail at
    compose up rather than at the preflight. Binding it is the only answer that covers both.
    #>
    param([Parameter(Mandatory)] [int] $Port)

    $listener = $null

    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            try { $listener.Stop() } catch { Write-Debug "listener stop failed: $($_.Exception.Message)" }
        }
    }
}

function Get-ReparsePointAncestor {
    <#
    .SYNOPSIS
    The first link or junction at or above a path, or $null when there is none.

    .DESCRIPTION
    Resolving a provider path does not follow a junction or a symbolic link to its target, so a
    containment answer about a name says nothing about the directory that name reaches. Every
    caller that decides something from a path's spelling walks it with this first and refuses the
    ancestry, rather than claiming a canonicalization PowerShell does not perform.

    Only existing components can be inspected, so a path that does not exist yet answers $null.
    That is why callers ask again at the moment they use the path.
    #>
    param([Parameter(Mandatory)] [string] $Path)

    $walk = $Path

    while (-not [string]::IsNullOrEmpty($walk)) {
        if ((Test-Path -LiteralPath $walk) -and
            ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            return $walk
        }

        $parent = Split-Path -Parent $walk
        if ($parent -ieq $walk) { break }
        $walk = $parent
    }

    return $null
}

function Test-ProofPathSafety {
    <#
    .SYNOPSIS
    Decides whether the workspace and evidence paths are ones this run may own and delete.

    .DESCRIPTION
    The workspace is emptied before use and removed on the way out, so the question is not whether
    the path is convenient but whether destroying it is this run's business. Claiming a path that
    already holds somebody's files does not make them this run's to delete, so an existing workspace
    is refused rather than cleared: ownership is something a run establishes by creating, not by
    naming.

    A path that contains the repository, the compose directory, or is a filesystem root, would take
    all of it with them, so those are refused by containment rather than by a list of spellings.

    The evidence directory must not overlap the workspace in either direction, because cleanup
    removes the workspace and the evidence is the record of why.
    #>
    param(
        [Parameter(Mandatory)] [string] $WorkspacePath,
        [Parameter(Mandatory)] [string] $EvidencePath,
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $ComposeRoot,
        [bool] $WorkspaceExists = $false
    )

    $blocker = @()

    function Test-PathContainment([string]$Parent, [string]$Child) {
        $normalizedParent = $Parent.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $normalizedChild = $Child.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)

        if ($normalizedParent -ieq $normalizedChild) {
            return $true
        }

        return $normalizedChild.StartsWith($normalizedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }

    if (-not [IO.Path]::IsPathRooted($WorkspacePath)) {
        $blocker += "the workspace path '$WorkspacePath' is not absolute, so what it names depends on the working directory at the moment it is read"
    }

    if (-not [IO.Path]::IsPathRooted($EvidencePath)) {
        $blocker += "the evidence path '$EvidencePath' is not absolute"
    }

    if ($blocker.Count -gt 0) {
        return [pscustomobject]@{ Safe = $false; Blocker = $blocker }
    }

    if ($WorkspaceExists) {
        $blocker += "the workspace '$WorkspacePath' already exists. This run empties and removes its workspace, and naming a directory does not make its contents this run's to delete. Choose a path that does not exist."
    }

    $root = [IO.Path]::GetPathRoot($WorkspacePath)
    if ($WorkspacePath.TrimEnd([IO.Path]::DirectorySeparatorChar) -ieq $root.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        $blocker += "the workspace '$WorkspacePath' is a filesystem root"
    }

    foreach ($protected in @(
            @{ Path = $RepositoryRoot; What = 'the repository' }
            @{ Path = $ComposeRoot; What = 'the compose directory' }
        )) {
        if (Test-PathContainment -Parent $WorkspacePath -Child $protected.Path) {
            $blocker += "the workspace '$WorkspacePath' contains $($protected.What), so removing it would take $($protected.What) with it"
        }
    }

    if (Test-PathContainment -Parent $WorkspacePath -Child $EvidencePath) {
        $blocker += "the evidence directory '$EvidencePath' is inside the workspace, so cleanup would remove the record of why the run failed"
    }

    if (Test-PathContainment -Parent $EvidencePath -Child $WorkspacePath) {
        $blocker += "the workspace '$WorkspacePath' contains the evidence directory '$EvidencePath'"
    }

    # A reparse point anywhere above either path means the name checked here and the directory
    # written to can be different places, and every containment answer above is about the name.
    foreach ($candidate in @(
            @{ Path = $WorkspacePath; What = 'workspace' }
            @{ Path = $EvidencePath; What = 'evidence directory' }
        )) {
        $link = Get-ReparsePointAncestor -Path $candidate.Path

        if ($null -ne $link) {
            $blocker += "the $($candidate.What) path passes through '$link', which is a link or junction; what this run would delete is then not the path it checked"
        }
    }

    return [pscustomobject]@{
        Safe    = ($blocker.Count -eq 0)
        Blocker = $blocker
    }
}

function Test-OwnedDeletionPath {
    <#
    .SYNOPSIS
    Decides whether a path may be removed, given what this run owns.

    .DESCRIPTION
    Every recursive removal is checked against the specific resources the run claimed rather than
    against a convention. A path is removable when it is one of the owned directories or sits
    inside one; anything else is refused however it was arrived at, including by a link or a
    relative segment, because the caller resolves before asking.
    #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string[]] $OwnedDirectory = @()
    )

    $normalized = $Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)

    foreach ($owned in $OwnedDirectory) {
        $normalizedOwned = $owned.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)

        if ($normalized -ieq $normalizedOwned -or
            $normalized.StartsWith($normalizedOwned + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-StockProofOwnership {
    <#
    .SYNOPSIS
    An empty record of what this run has taken responsibility for.
    #>
    return [pscustomobject]@{
        Resource = [System.Collections.Generic.List[pscustomobject]]::new()
    }
}

function Add-OwnedResource {
    <#
    .SYNOPSIS
    Records ownership of a resource BEFORE the operation that may create it.

    .DESCRIPTION
    Ownership is claimed ahead of the attempt rather than after it succeeds. An operation that
    creates a container and then fails waiting for it has created something, and a flag set only on
    success would leave that behind. So the record is written first and the cleanup plan below
    includes everything claimed, whether or not the attempt that claimed it returned.
    #>
    param(
        [Parameter(Mandatory)] $Ownership,
        [Parameter(Mandatory)] [ValidateSet('ComposeProject', 'Bootstrap', 'Directory')] [string] $Kind,
        [Parameter(Mandatory)] [string] $Name
    )

    if (-not @($Ownership.Resource | Where-Object { $_.Kind -eq $Kind -and $_.Name -eq $Name })) {
        $Ownership.Resource.Add([pscustomobject]@{ Kind = $Kind; Name = $Name })
    }

    return $Ownership
}

function Get-CleanupPlan {
    <#
    .SYNOPSIS
    What to tear down, in order, given what this run claimed.

    .DESCRIPTION
    Only what was claimed. A run that refused at the preflight claimed nothing and therefore tears
    down nothing, which is what stops a workflow's always() step from removing a stack it never
    created. The compose project goes first, because it owns the containers that hold the
    directories open.
    #>
    param($Ownership)

    if ($null -eq $Ownership) {
        return @()
    }

    # The compose project first: it owns the containers that hold the staged bootstrap
    # workspace and the scratch directories open.
    $order = @{ ComposeProject = 0; Bootstrap = 1; Directory = 2 }

    return @(
        $Ownership.Resource |
            Sort-Object -Property @{ Expression = { $order[$_.Kind] } } |
            ForEach-Object { [pscustomobject]@{ Kind = $_.Kind; Name = $_.Name } }
    )
}

function Get-ContractPackageReference {
    <#
    .SYNOPSIS
    The version string to put in a PackageReference so NuGet resolves exactly the pinned version.

    .DESCRIPTION
    The pin records a version identity. NuGet reads a bare version in a PackageReference as a floor,
    so the identity alone does not make the restore resolve it; the bracketed form does. This is the
    one place that conversion happens, and Test-RestoredPackageVersion is the check that it worked.
    #>
    param([Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $Version)

    if ($Version.StartsWith('[') -or $Version.StartsWith('(')) {
        throw "The pinned version '$Version' is already NuGet range syntax. The pin records a version identity, so this would produce a doubly bracketed reference."
    }

    return "[$Version]"
}

function Test-RestoredPackageVersion {
    <#
    .SYNOPSIS
    Decides whether what was actually restored is what the pin named.

    .DESCRIPTION
    Bracketing asks for an exact version; this establishes that the ask was honoured. A restore can
    still produce something else - a floating dependency, a package the cache already held, a feed
    that resolved differently - and the whole point of pinning is that the proof runs against the
    artifact the pin names.
    #>
    param(
        [Parameter(Mandatory)] [string] $PackageId,
        [Parameter(Mandatory)] [string] $ExpectedVersion,
        [string] $RestoredVersion
    )

    if ([string]::IsNullOrWhiteSpace($RestoredVersion)) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "no restored version was observed for $PackageId, so what the restore produced is unknown"
        }
    }

    if ($RestoredVersion -cne $ExpectedVersion) {
        return [pscustomobject]@{
            Verified = $false
            Reason   = "$PackageId restored as $RestoredVersion but the pin names $ExpectedVersion"
        }
    }

    return [pscustomobject]@{ Verified = $true; Reason = "$PackageId restored as $ExpectedVersion" }
}

function Get-SchemaToolInstallArgument {
    <#
    .SYNOPSIS
    The dotnet tool install arguments that resolve exactly the pinned SchemaTools package.

    .DESCRIPTION
    EdFi.Api.SchemaTools is packed as a DotnetTool with the command name api-schema-tools, so the
    released tool is installed rather than built. --tool-path keeps it out of the machine-wide tool
    store, and the resolved executable is handed to the bootstrap through DMS_SCHEMA_TOOL_PATH,
    which Resolve-DmsSchemaTool honours ahead of every path that would otherwise find this
    worktree's build output.
    #>
    param(
        [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $Version,
        [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $ToolPath,
        [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $FeedUrl
    )

    return @(
        'tool', 'install', 'EdFi.Api.SchemaTools'
        '--tool-path', $ToolPath
        # Exact, not a floor: dotnet tool install resolves the highest matching version otherwise,
        # and "highest" is not "the one the pin names".
        '--version', $Version
        '--add-source', $FeedUrl
    )
}

function Get-GovernedEnvironmentKey {
    <#
    .SYNOPSIS
    The environment keys the pin decides, and which nothing else may decide.

    .DESCRIPTION
    Two things read these: the base environment file, which declares some of them for an ordinary
    local run, and the process environment, which Compose gives precedence over any file. Both would
    silently replace a pinned value, so the composer strips them out of the base and the entry
    script refuses to start when one is set ambiently.

    DMS_IMAGE_TAG is on the list even though nothing is written in its place. It selects both
    published images at once, so leaving a stale declaration behind would be one more thing that
    could answer the image question.
    #>
    return @(
        'DMS_STOCK_IMAGE_REFERENCE'
        'DMS_CONFIG_DOCKER_IMAGE'
        'DMS_IMAGE_TAG'
        'DMS_CONFIG_DATA_STANDARD_VERSION'
        'SCHEMA_PACKAGES'
        # No port key is governed. DMS_HTTP_PORTS and DMS_CONFIG_ASPNETCORE_HTTP_PORTS publish
        # '127.0.0.1:${VAR}:${VAR}', one variable on both sides, so moving either would move the
        # port the container itself listens on while six addresses in the base file name
        # ed-fi-api-config:8081 directly. The run therefore uses the ports the environment file
        # declares, and the preflight reads those same declared values rather than a second list.
        'DMS_PLUGINS_COMPOSE_FILES'
        'DMS_PLUGINS_ALLOWED'
        'DMS_PLUGINS_MOUNT_SOURCE'
        'PLUGIN_FEED_SOURCE'
        'PLUGIN_PACKAGE_URL'
        'PLUGIN_PACKAGE_SHA256'
        'PLUGIN_NAME'
    )
}

function Get-AmbientRefusedKey {
    <#
    .SYNOPSIS
    Every key that must not already be set in the process environment.

    .DESCRIPTION
    A superset of the governed keys. The port keys are deliberately NOT governed - their values are
    kept from the base file, because moving them would move the port the container itself listens
    on - but an ambient one would still win over the file in Compose and in Get-EnvValue, while the
    preflight and the URLs read only the file. That is the same silent divergence for a different
    reason, so they are refused here without being rewritten there.
    #>
    return @(Get-GovernedEnvironmentKey) + @(
        'DMS_HTTP_PORTS'
        'DMS_CONFIG_ASPNETCORE_HTTP_PORTS'
        'POSTGRES_PORT'
    )
}

function Test-ProofPort {
    <#
    .SYNOPSIS
    Decides whether the ports read from the environment file are usable at all.

    .DESCRIPTION
    Before anything is bound. A key the file does not declare reads as an empty string and becomes
    port 0, which binds to whatever the operating system hands out and would make the preflight's
    answer meaningless; an out-of-range value fails later and less clearly.
    #>
    param([hashtable] $EnvironmentValue = @{}, [string[]] $Key = @())

    $blocker = @()
    $port = @{}

    foreach ($name in $Key) {
        $raw = if ($EnvironmentValue.ContainsKey($name)) { [string]$EnvironmentValue[$name] } else { '' }
        $parsed = 0

        if ([string]::IsNullOrWhiteSpace($raw)) {
            $blocker += "the environment file declares no $name, so the port this run would bind is undefined"
        }
        elseif (-not [int]::TryParse($raw, [ref]$parsed)) {
            $blocker += "the environment file's $name is '$raw', which is not a number"
        }
        elseif ($parsed -lt 1 -or $parsed -gt 65535) {
            $blocker += "the environment file's $name is $parsed, which is not a usable TCP port"
        }
        else {
            $port[$name] = $parsed
        }
    }

    return [pscustomobject]@{
        Valid   = ($blocker.Count -eq 0)
        Port    = $port
        Blocker = $blocker
    }
}

function Get-AmbientOverride {
    <#
    .SYNOPSIS
    The governed keys that are already set in the process environment.

    .DESCRIPTION
    Compose reads a process variable in preference to the same key in an --env-file, so an ambient
    SCHEMA_PACKAGES or DMS_CONFIG_DOCKER_IMAGE would decide what this proof runs against and the
    pin would be a description of something else. Presence is what matters, not the value: an
    ambient empty string still wins.
    #>
    param([hashtable] $ProcessEnvironment = @{})

    return @(Get-AmbientRefusedKey | Where-Object { $ProcessEnvironment.ContainsKey($_) })
}

function Get-StockProofEnvironmentContent {
    <#
    .SYNOPSIS
    The environment file the stock deployment runs from, composed out of the pin.

    .DESCRIPTION
    One file, so the schema preparation phase and the container cannot disagree about anything. In
    particular SCHEMA_PACKAGES is what selects schema content on both sides, and it comes from the
    pin's own set.

    Every governed key is REMOVED from the base before the pinned values are appended, rather than
    appended over. The base file declares some of them already - .env.e2e carries DMS_IMAGE_TAG and
    a multi-line SCHEMA_PACKAGES block - and relying on a later declaration winning would leave the
    effective value depending on which reader parsed the file and how it treated the earlier
    multi-line one. Removing them first makes each governed key declared exactly once.

    The two images are pinned independently, from their own repositories. DMS_IMAGE_TAG is never
    written: it selects both published images at once, and a digest belongs to one repository.
    #>
    param(
        [Parameter(Mandatory)] $Pin,
        [Parameter(Mandatory)] [string] $BaseContent,
        [Parameter(Mandatory)] [string] $PluginComposeFiles,
        [string] $PluginMountSource,
        [string] $PluginPackageUrl,
        [string] $PluginPackageSha256,
        [string] $PluginName,
        [string] $PluginFeedSource,
        [string] $AllowedPlugins = ''
    )

    $line = [System.Collections.Generic.List[string]]::new()

    # Remove-EnvFileKeys understands both a scalar declaration and a multi-line quoted block, which
    # is the shape the base file's SCHEMA_PACKAGES takes.
    foreach ($existing in (Remove-EnvFileKeys -Lines ($BaseContent -split "`r?`n") -Keys (Get-GovernedEnvironmentKey))) {
        $line.Add($existing)
    }

    $line.Add('')
    $line.Add('# Written by Invoke-StockImagePluginProof.ps1 from the stock image pin. Do not edit.')

    $edFiApi = "$($Pin.edFiApi.repository):$($Pin.edFiApi.tag)@$($Pin.edFiApi.digest)"
    $configurationService = "$($Pin.configurationService.repository)@$($Pin.configurationService.digest)"

    $line.Add("DMS_STOCK_IMAGE_REFERENCE=$edFiApi")
    $line.Add("DMS_CONFIG_DOCKER_IMAGE=$configurationService")
    $line.Add("DMS_CONFIG_DATA_STANDARD_VERSION=$($Pin.provisioning.dataStandardVersion)")

    # Single line and single quoted, which is the form .env.bootstrap.ds52 uses and the form Compose
    # hands to the container verbatim.
    $schemaPackages = ($Pin.provisioning.schemaPackages | ForEach-Object {
            [ordered]@{ name = $_.name; version = $_.version; feedUrl = $_.feedUrl }
        } | ConvertTo-Json -Depth 4 -Compress)

    if ($schemaPackages -notmatch '^\[') {
        # ConvertTo-Json renders a one-element set as an object; SCHEMA_PACKAGES is always an array.
        $schemaPackages = "[$schemaPackages]"
    }

    $line.Add("SCHEMA_PACKAGES='$schemaPackages'")
    $line.Add("DMS_PLUGINS_COMPOSE_FILES=$PluginComposeFiles")
    $line.Add("DMS_PLUGINS_ALLOWED=$AllowedPlugins")

    foreach ($optional in @(
            @{ Key = 'DMS_PLUGINS_MOUNT_SOURCE'; Value = $PluginMountSource }
            @{ Key = 'PLUGIN_FEED_SOURCE'; Value = $PluginFeedSource }
            @{ Key = 'PLUGIN_PACKAGE_URL'; Value = $PluginPackageUrl }
            @{ Key = 'PLUGIN_PACKAGE_SHA256'; Value = $PluginPackageSha256 }
            @{ Key = 'PLUGIN_NAME'; Value = $PluginName }
        )) {
        if (-not [string]::IsNullOrWhiteSpace($optional.Value)) {
            $line.Add("$($optional.Key)=$($optional.Value)")
        }
    }

    return ($line -join "`n")
}

Export-ModuleMember -Function @(
    # Decisions about recorded evidence.
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
    'Get-JsonMember'

    # Decisions about what the run may touch, and what it must put back.
    'Get-OccupiedHostResource'
    'Get-StockProofOwnership'
    'Add-OwnedResource'
    'Get-CleanupPlan'
    'Get-ContractPackageReference'
    'Test-RestoredPackageVersion'
    'Get-SchemaToolInstallArgument'
    'Test-ProofPathSafety'
    'Test-OwnedDeletionPath'
    'Get-ReparsePointAncestor'
    'Test-PreparedSchemaIdentity'
    'Get-NetworkAttachmentInventory'
    'Test-LocalPortAvailable'
    'Get-GovernedEnvironmentKey'
    'Get-AmbientRefusedKey'
    'Get-AmbientOverride'
    'Test-ProofPort'
    'Get-StockProofEnvironmentContent'
)
