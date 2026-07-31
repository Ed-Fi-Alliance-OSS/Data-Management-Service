# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Shared Compose-equivalent resolver: Resolve-CmsDatabaseTopologyEnvironmentFile and
# Confirm-CmsDatabaseTopologyAgreement (below) read ambient-precedence-aware values through
# Get-ComposeResolvedEnvValue and parse connection strings through the resolved-string extractors,
# so the CMS database topology seam (DMS-1270) is validated against exactly what Docker Compose
# would actually resolve, not merely the env-file's own text.
Import-Module (Join-Path $PSScriptRoot "database-safety.psm1") -Force

function Test-NativeCommandWithTimeout {
    <#
    .SYNOPSIS
        Runs a native command with a hard timeout and returns whether it exited successfully.

    .DESCRIPTION
        Uses ProcessStartInfo.ArgumentList so every argument retains its exact boundary. When the
        timeout expires, the process tree is terminated before the function returns false. Output
        is captured and discarded because this helper is intended for readiness probes.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ArgumentList,

        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds = 10
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        $null = $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        if (-not $process.Start()) {
            return $false
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
            }
            catch [System.InvalidOperationException] {
                Write-Debug "The process exited between the timeout result and Kill()."
            }
            $process.WaitForExit()
            $null = $standardOutputTask.GetAwaiter().GetResult()
            $null = $standardErrorTask.GetAwaiter().GetResult()
            return $false
        }

        $null = $standardOutputTask.GetAwaiter().GetResult()
        $null = $standardErrorTask.GetAwaiter().GetResult()
        return $process.ExitCode -eq 0
    }
    catch {
        return $false
    }
    finally {
        $process.Dispose()
    }
}

function ReadValuesFromEnvFile {
    param (
        [string]$EnvironmentFile
    )

    if (-Not (Test-Path $EnvironmentFile)) {
        throw "Environment file not found: $EnvironmentFile"
    }
    $envFile = @{}

    try {
        Get-Content $EnvironmentFile | ForEach-Object {
            if ($_ -match "^\s*#") { return }
            $split = $_.Split('=', 2)
            if ($split.Length -eq 2) {
                $key = $split[0].Trim()
                $value = $split[1].Trim()
                $envFile[$key] = $value
            }
        }
    }
    catch {
         Write-Error "Please provide valid .env file."
    }
    return $envFile
}

function Resolve-LocalSettingsEnvironmentFile {
    <#
    .SYNOPSIS
    Single source of truth for resolving the -EnvironmentFile parameter that every story-aligned
    phase command (start, configure, provision, seed) accepts. Returns the absolute path to a
    readable env file or throws if it cannot be located.

    .DESCRIPTION
    Resolution precedence (highest first):
      1. The supplied -Path, when non-empty:
         - absolute paths are kept as-is;
         - relative paths are resolved against the caller's current working directory.
      2. <docker-compose>/.env when present.
      3. When .env is absent, it is seeded once as a copy of <docker-compose>/.env.example
         and the new .env is returned. .env.example itself is never consumed at runtime:
         it stays a pure, tracked example, while .env (gitignored) is the live local
         settings file the user can edit durably.

    A missing file always throws. This is intentionally narrower than ReadValuesFromEnvFile
    so phase commands fail fast on a typo rather than silently fall through to ambient process
    environment defaults.

    .PARAMETER Path
    Caller-supplied env file path. May be empty (use defaults) or relative.

    .PARAMETER DockerComposeRoot
    Optional override for the docker-compose root directory used for default lookup. Defaults
    to this module's directory (eng/docker-compose). Tests pass an isolated copy.
    #>
    param(
        [string]$Path,
        [string]$DockerComposeRoot
    )

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $defaultEnv = Join-Path $DockerComposeRoot ".env"
        if (-not (Test-Path -LiteralPath $defaultEnv -PathType Leaf)) {
            $exampleEnv = Join-Path $DockerComposeRoot ".env.example"
            if (Test-Path -LiteralPath $exampleEnv -PathType Leaf) {
                Copy-Item -LiteralPath $exampleEnv -Destination $defaultEnv
                Write-Information "No .env found; created $defaultEnv from .env.example. Edit it to customize local settings." -InformationAction Continue
            }
        }
        $Path = $defaultEnv
    }
    elseif (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Environment file not found: $Path."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-EnvValue {
    <#
    .SYNOPSIS
    Shared helper that returns the value of an env-file key when present and non-blank,
    otherwise the documented default. Equivalent to the duplicated Get-EnvValueOrDefault
    helpers in configure-local-data-store.ps1 and provision-dms-schema.ps1, lifted into
    the shared module so the precedence rule is single-sourced.

    Precedence: explicit env-file value > documented default. Process environment variables
    are deliberately not consulted - direct phase invocation must not depend on ambient state.
    #>
    param(
        [hashtable]$EnvValues,
        [Parameter(Mandatory)]
        [string]$Name,
        [string]$DefaultValue = ""
    )

    if ($null -eq $EnvValues) {
        return $DefaultValue
    }

    if ($EnvValues.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$Name])) {
        return [string]$EnvValues[$Name]
    }

    return $DefaultValue
}


function Resolve-BootstrapAdminClient {
    <#
    .SYNOPSIS
        Returns the bootstrap admin client id and secret used by configure-local-data-store.ps1
        and provision-dms-schema.ps1 to acquire a CMS admin token. Reads
        DMS_BOOTSTRAP_ADMIN_CLIENT_ID / DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET from the env file and
        falls back to the historical local-dev defaults so the standard developer flow needs no
        env-file changes. Single-sources the two values so configure (which registers) and
        provision (which authenticates) always agree on the client.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param(
        [hashtable]$EnvValues
    )

    return [pscustomobject]@{
        ClientId     = Get-EnvValue -EnvValues $EnvValues -Name "DMS_BOOTSTRAP_ADMIN_CLIENT_ID" -DefaultValue "dms-data-store-admin"
        ClientSecret = Get-EnvValue -EnvValues $EnvValues -Name "DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET" -DefaultValue "ValidClientSecret1234567890!Abcd"
    }
}

function Resolve-IdentityClientSecretConfiguration {
    <#
    .SYNOPSIS
        Returns the parameters used to register the local identity clients so that both the
        secrets and the length-validation bounds match the env-file values DMS and CMS use.

        - DmsConfigurationService (full_access) is registered with
          DMS_CONFIG_IDENTITY_CLIENT_SECRET (the CMS IdentitySettings:ClientSecret).
        - CMSReadOnlyAccess (readonly_access) is registered with CONFIG_SERVICE_CLIENT_SECRET
          (the DMS ConfigurationServiceSettings:ClientSecret used at runtime to obtain CMS tokens).
        - ClientSecretMinimumLength / ClientSecretMaximumLength come from
          DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH / _MAXIMUM_LENGTH, which also configure
          CMS IdentitySettings:ClientSecretValidation. They are passed to setup-keycloak.ps1 /
          setup-openiddict.ps1 so a CMS-valid secret is not rejected by the setup scripts' own
          default 32/128 bounds.

        All values fall back to the historical local-dev defaults so the standard developer flow
        needs no env-file changes. Previously the setup scripts registered every client with the
        hard-coded default secret and validated against the default 32/128 bounds, so overriding
        CONFIG_SERVICE_CLIENT_SECRET / DMS_CONFIG_IDENTITY_CLIENT_SECRET (or the length bounds)
        produced a mismatch and CMS token acquisition or local registration failed. Single-sources
        the mapping so registration and runtime always agree.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param(
        [hashtable]$EnvValues
    )

    return [pscustomobject]@{
        DmsConfigurationServiceClientSecret = Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_IDENTITY_CLIENT_SECRET" -DefaultValue "ValidClientSecret1234567890!Abcd"
        CmsReadOnlyAccessClientSecret       = Get-EnvValue -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SECRET" -DefaultValue "ValidClientSecret1234567890!Abcd"
        ClientSecretMinimumLength           = [int](Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH" -DefaultValue "32")
        ClientSecretMaximumLength           = [int](Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH" -DefaultValue "128")
    }
}

Set-Alias -Name Resolve-IdentityClientSecrets -Value Resolve-IdentityClientSecretConfiguration

function Resolve-CmsBaseUrl {
    <#
    .SYNOPSIS
        Returns the CMS base URL derived from the supplied env-file values.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param (
        [hashtable]$EnvValues
    )

    $port = $EnvValues['DMS_CONFIG_ASPNETCORE_HTTP_PORTS']
    if (-not [string]::IsNullOrWhiteSpace($port)) {
        return "http://localhost:$port"
    }
    return "http://localhost:8081"
}

function Resolve-DockerLocalDmsBaseUrl {
    <#
    .SYNOPSIS
        Returns the Docker-local DMS base URL derived from the supplied env-file values.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param (
        [hashtable]$EnvValues
    )

    $port = $EnvValues['DMS_HTTP_PORTS']
    if (-not [string]::IsNullOrWhiteSpace($port)) {
        return "http://localhost:$port"
    }
    return "http://localhost:8080"
}

function Resolve-DmsRouteUrl {
    <#
    .SYNOPSIS
        Composes the tenant- and qualifier-prefixed DMS base URL for data writes. The canonical
        shape is `{base}[/{tenant}][/{qualifier-values}]/data/{**dmsPath}` (see
        CoreEndpointModule.BuildRoutePattern). This function returns the portion up to (but
        excluding) `/data/...`; callers append the data suffix.
        /health is registered only at the unqualified root, so health probes must use the bare
        base URL and must not pass through this composer.
    .PARAMETER BaseUrl
        The DMS base URL (e.g. http://localhost:8080).
    .PARAMETER Tenant
        Optional tenant identifier. When non-empty, becomes the first path segment after the base.
    .PARAMETER RouteQualifierValues
        Ordered route-qualifier values (e.g. school year) appended after the tenant segment.
        Order must match the server's appsettings RouteQualifierSegments configuration.
    #>
    param (
        [Parameter(Mandatory)] [string]$BaseUrl,
        [string]$Tenant = "",
        [string[]]$RouteQualifierValues = @()
    )

    $segments = @()
    if (-not [string]::IsNullOrWhiteSpace($Tenant)) {
        $segments += $Tenant
    }
    foreach ($value in $RouteQualifierValues) {
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $segments += [string]$value
        }
    }
    $normalizedBaseUrl = $BaseUrl.TrimEnd('/')
    if ($segments.Count -eq 0) {
        return $normalizedBaseUrl
    }
    return "$normalizedBaseUrl/" + ($segments -join "/")
}

function Resolve-IdentityProvider {
    <#
    .SYNOPSIS
        Returns the active identity provider name.
        Resolution order: -OverrideProvider, env DMS_CONFIG_IDENTITY_PROVIDER, default self-contained.
        Throws for unsupported values.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    .PARAMETER OverrideProvider
        Caller-supplied provider string that wins over the env-file value when non-empty.
    #>
    param (
        [hashtable]$EnvValues,
        [string]$OverrideProvider = ""
    )

    $supported = @("keycloak", "self-contained")

    if (-not [string]::IsNullOrWhiteSpace($OverrideProvider)) {
        if ($supported -notcontains $OverrideProvider) {
            throw "Unsupported identity provider '$OverrideProvider'. Supported values: $($supported -join ', ')."
        }
        return $OverrideProvider
    }

    $fromEnv = $EnvValues['DMS_CONFIG_IDENTITY_PROVIDER']
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        if ($supported -notcontains $fromEnv) {
            throw "Unsupported identity provider '$fromEnv' (from env file). Supported values: $($supported -join ', ')."
        }
        return $fromEnv
    }

    return "self-contained"
}

function Resolve-OAuthTokenUrl {
    <#
    .SYNOPSIS
        Returns the host-side OAuth token endpoint URL for the selected identity provider.
        BulkLoadClient and other host processes call OAuth from the host, so URLs are built
        from the published port env-vars (DMS_CONFIG_ASPNETCORE_HTTP_PORTS, KEYCLOAK_PORT)
        with localhost, not from container-flavored *_OAUTH_TOKEN_ENDPOINT env-vars which
        resolve only inside the Docker network.
        For self-contained with a school year, appends /{schoolYear} to the /connect/token path.
        Throws for unsupported providers.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    .PARAMETER IdentityProvider
        The resolved identity provider name (keycloak or self-contained).
    .PARAMETER SchoolYear
        Optional school year integer. When supplied with self-contained, the year is appended
        to the token endpoint path (e.g. http://localhost:8081/connect/token/2024).
        Ignored for keycloak.
    #>
    param (
        [hashtable]$EnvValues,
        [string]$IdentityProvider,
        [System.Nullable[int]]$SchoolYear = $null
    )

    switch ($IdentityProvider) {
        "keycloak" {
            $port = $EnvValues['KEYCLOAK_PORT']
            if ([string]::IsNullOrWhiteSpace($port)) {
                $port = "8045"
            }
            return "http://localhost:$port/realms/edfi/protocol/openid-connect/token"
        }
        "self-contained" {
            $port = $EnvValues['DMS_CONFIG_ASPNETCORE_HTTP_PORTS']
            if ([string]::IsNullOrWhiteSpace($port)) {
                $port = "8081"
            }
            $base = "http://localhost:$port/connect/token"
            if ($null -ne $SchoolYear) {
                return "$base/$SchoolYear"
            }
            return $base
        }
        default {
            throw "Unsupported identity provider '$IdentityProvider'. Supported values: keycloak, self-contained."
        }
    }
}

function Write-DerivedEnvFile {
    <#
    .SYNOPSIS
        Materializes a derived environment file from a base env file, applying scalar key
        overrides. The base file is left untouched. Used by the bootstrap wrapper to produce
        a per-run profile (e.g. a loose circuit-breaker for bulk loads) without mutating the
        developer's checked-in env files.

    .PARAMETER BaseEnvironmentFile
        Path to the source env file (e.g. eng/docker-compose/.env or .env.example).

    .PARAMETER TargetPath
        Path where the derived file is written. Parent directory is created if missing.

    .PARAMETER KeyOverrides
        Hashtable of KEY=VALUE entries to set. If the key exists in the base file, the existing line
        is replaced; if not, a new line is appended. Values are written verbatim (caller is responsible
        for quoting if the value needs it).

    .OUTPUTS
        None. Writes the derived file to TargetPath as UTF-8 without BOM, with LF line endings and
        a final newline.

    .EXAMPLE
        Write-DerivedEnvFile `
            -BaseEnvironmentFile ./.env `
            -TargetPath ./.bootstrap/.env.derived `
            -KeyOverrides @{ FAILURE_RATIO = "0.95" }
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bootstrap helper, no -WhatIf surface needed.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'matchInfo', Justification = 'MatchEvaluator delegate parameter: the evaluator deliberately ignores the match and returns the literal replacement line, which is the whole point - the string-replacement overload would interpret $-sequences in caller values as substitution directives.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$TargetPath,
        [hashtable]$KeyOverrides = @{}
    )

    if (-not (Test-Path -LiteralPath $BaseEnvironmentFile -PathType Leaf)) {
        throw "Write-DerivedEnvFile: base environment file not found: $BaseEnvironmentFile"
    }

    $content = Get-Content -LiteralPath $BaseEnvironmentFile -Raw
    if ($null -eq $content) { $content = "" }

    # 1) Apply scalar key overrides. Replace the key's assignment line, or append if missing. The
    # pattern mirrors the shared assignment grammar (Get-DotenvAssignment): optional indent, optional
    # `export ` prefix, and optional whitespace around '='. A narrower pattern would append a second
    # declaration of the same key instead of replacing the existing one, and Compose keeps the LAST
    # declaration for the final environment while earlier lines still see the first - so the file would
    # carry two different effective values for one key.
    foreach ($key in $KeyOverrides.Keys) {
        $value = [string]$KeyOverrides[$key]
        $linePattern = "(?m)^[ \t]*(?:export[ \t]+)?$([Regex]::Escape($key))[ \t]*=.*$"
        $newLine = "$key=$value"
        if ([Regex]::IsMatch($content, $linePattern)) {
            # A match evaluator returns $newLine literally. The string-replacement overload of
            # Regex.Replace instead treats it as a REPLACEMENT PATTERN, where sequences like $&, $0,
            # $', and $` are substitution directives (e.g. $& re-inserts the entire matched text) -
            # a caller-authored value containing one of those literal sequences (a password, for
            # instance) would otherwise be corrupted rather than written verbatim as documented.
            $content = [Regex]::Replace($content, $linePattern, { param($matchInfo) $newLine })
        }
        else {
            if ($content.Length -gt 0 -and -not $content.EndsWith("`n")) { $content += "`n" }
            $content += "$newLine`n"
        }
    }

    # 2) Normalize line endings (LF) and ensure final newline.
    $content = $content -replace "`r`n", "`n"
    if (-not $content.EndsWith("`n")) { $content += "`n" }

    $targetDir = Split-Path -Parent $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($targetDir) -and -not (Test-Path -LiteralPath $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($TargetPath, $content, $utf8NoBom)
}

function Resolve-BootstrapDerivedEnv {
    <#
    .SYNOPSIS
        Materializes the per-run derived env file with the canonical bootstrap seed-loading profile.
        Always sets FAILURE_RATIO=0.95 so the circuit breaker tolerates bulk-load failures.
        The base env file is left untouched. Shared by bootstrap-{local,published}-dms.ps1
        wrappers so the two stay in lockstep.

    .PARAMETER BaseEnvironmentFile
        Absolute path to the source env file. Must exist.

    .PARAMETER DerivedTargetPath
        Path where the derived file is written. Parent directory is created if missing.

    .OUTPUTS
        [string] Returns the DerivedTargetPath on success.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bootstrap helper, no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$DerivedTargetPath
    )

    Write-DerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -TargetPath $DerivedTargetPath `
        -KeyOverrides @{
            FAILURE_RATIO = "0.95"
        }

    return $DerivedTargetPath
}

function Remove-EnvFileKeys {
    <#
    .SYNOPSIS
        Returns the base env-file lines with every entry for the supplied keys removed. Handles both
        single-line scalars (KEY=value) and multi-line quoted values (e.g. the SCHEMA_PACKAGES JSON
        block written as KEY='[ ... ]' across several lines). Comments and unrelated lines are kept.

    .PARAMETER Lines
        The base env file content, one element per line.

    .PARAMETER Keys
        The key names to remove (case-insensitive).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure helper: returns a filtered copy of the lines and does not change system state.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'The helper removes a set of keys.')]
    param(
        [string[]]$Lines,
        $Keys
    )

    $keySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $Keys) {
        [void]$keySet.Add([string]$key)
    }

    $result = [System.Collections.Generic.List[string]]::new()
    $index = 0
    while ($index -lt $Lines.Count) {
        $line = $Lines[$index]
        $match = [regex]::Match($line, "^[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*=(.*)$")

        if ($match.Success -and $keySet.Contains($match.Groups[1].Value)) {
            $value = $match.Groups[2].Value.TrimStart()
            $openingQuote = if ($value.StartsWith("'")) { "'" } elseif ($value.StartsWith('"')) { '"' } else { $null }

            # A quoted value with no matching closing quote on the same line spans multiple lines;
            # skip continuation lines through the one that closes the quote.
            if ($null -ne $openingQuote -and $value.IndexOf($openingQuote, 1) -lt 0) {
                $index++
                while ($index -lt $Lines.Count -and -not $Lines[$index].Contains($openingQuote)) {
                    $index++
                }
                if ($index -lt $Lines.Count) {
                    $index++
                }
            }
            else {
                $index++
            }
            continue
        }

        $result.Add($line)
        $index++
    }

    return , $result.ToArray()
}

function New-DataStandardDerivedEnvFile {
    <#
    .SYNOPSIS
        Composes a base environment file with a data-standard overlay (e.g. .env.ds52, .env.ds61)
        into a single derived env file, so callers keep passing one -EnvironmentFile / --env-file
        while selecting a data standard version. The base and overlay files are left untouched.

    .DESCRIPTION
        Overlay keys (e.g. SCHEMA_PACKAGES, DATABASE_TEMPLATE_PACKAGE, DMS_CONFIG_DATA_STANDARD_VERSION)
        replace the matching entries from the base file; every other base line is preserved. Authoring
        the overlay's SCHEMA_PACKAGES on a single line keeps overlay parsing trivial; the base file's
        multi-line SCHEMA_PACKAGES block is removed wholesale before the overlay is appended.

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file (e.g. .env.e2e). Must exist.

    .PARAMETER OverlayEnvironmentFile
        Absolute path to the overlay env file (e.g. .env.ds61). Must exist.

    .PARAMETER TargetPath
        Path where the derived file is written. Parent directory is created if missing.

    .OUTPUTS
        [string] Returns the TargetPath on success.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Local-dev helper, no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$OverlayEnvironmentFile,
        [Parameter(Mandatory)] [string]$TargetPath
    )

    if (-not (Test-Path -LiteralPath $BaseEnvironmentFile -PathType Leaf)) {
        throw "New-DataStandardDerivedEnvFile: base environment file not found: $BaseEnvironmentFile"
    }
    if (-not (Test-Path -LiteralPath $OverlayEnvironmentFile -PathType Leaf)) {
        throw "New-DataStandardDerivedEnvFile: data standard overlay file not found: $OverlayEnvironmentFile"
    }

    $overlayKeys = (ReadValuesFromEnvFile $OverlayEnvironmentFile).Keys
    $baseLines = @(Get-Content -LiteralPath $BaseEnvironmentFile)
    $baseWithoutOverlayKeys = Remove-EnvFileKeys -Lines $baseLines -Keys $overlayKeys

    $overlayContent = (Get-Content -LiteralPath $OverlayEnvironmentFile -Raw) -replace "`r`n", "`n"

    $merged = (($baseWithoutOverlayKeys -join "`n").TrimEnd("`n")) + "`n`n" + $overlayContent.TrimEnd("`n") + "`n"

    $targetDir = Split-Path -Parent $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($targetDir) -and -not (Test-Path -LiteralPath $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($TargetPath, $merged, $utf8NoBom)

    return $TargetPath
}

function Get-DataStandardOverlayToken {
    <#
    .SYNOPSIS
        Normalizes a data standard version (e.g. "5.2", "6.1", "ds52") to its overlay token
        ("ds52", "ds61"), used to locate the .env.<token> overlay file.
    #>
    param(
        [Parameter(Mandatory)] [string]$DataStandardVersion
    )

    $value = $DataStandardVersion.Trim().ToLowerInvariant()
    if ($value -match '^ds[0-9]+$') {
        return $value
    }

    $digits = ($value -replace '[^0-9]', '')
    if ([string]::IsNullOrWhiteSpace($digits)) {
        throw "Get-DataStandardOverlayToken: '$DataStandardVersion' is not a recognizable data standard version (expected e.g. '5.2', '6.1', or 'ds52')."
    }

    return "ds$digits"
}

function Resolve-DataStandardEnvironmentFile {
    <#
    .SYNOPSIS
        Returns the effective environment file path for a requested data standard version. With no
        version (the default) the base file is returned unchanged, preserving DS 5.2 default behavior.
        With a version, the matching .env.<token> overlay is composed onto the base into a derived
        file under <DockerComposeRoot>/.derived/ and that path is returned.

    .PARAMETER DataStandardVersion
        e.g. "5.2", "6.1", "ds52", "ds61"; empty/whitespace selects the default (base file unchanged).

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file.

    .PARAMETER DockerComposeRoot
        Directory holding the .env.<token> overlays and the .derived output. Defaults to this module's
        directory (eng/docker-compose).

    .PARAMETER OverlayPrefix
        Overlay file-name prefix. Defaults to ".env" (the shared E2E/SDK-surface overlays,
        e.g. .env.ds61). The bootstrap wrapper passes ".env.bootstrap" to compose the
        local-bootstrap surfaces (e.g. .env.bootstrap.ds61) instead. A non-default prefix is
        reflected in the derived file name (e.g. <base>.bootstrap.<token>) so both derivations
        can coexist under .derived/.
    #>
    param(
        [string]$DataStandardVersion,
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot,
        [string]$OverlayPrefix = ".env"
    )

    if ([string]::IsNullOrWhiteSpace($DataStandardVersion)) {
        return $BaseEnvironmentFile
    }

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    $token = Get-DataStandardOverlayToken $DataStandardVersion
    $overlayPath = Join-Path $DockerComposeRoot "$OverlayPrefix.$token"
    if (-not (Test-Path -LiteralPath $overlayPath -PathType Leaf)) {
        $available = @(Get-ChildItem -Path $DockerComposeRoot -Filter "$OverlayPrefix.ds*" -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name) -join ", "
        throw "Resolve-DataStandardEnvironmentFile: no overlay for data standard version '$DataStandardVersion' (expected '$overlayPath'). Available overlays: $available."
    }

    # A non-default prefix contributes its distinguishing segment(s) to the derived name
    # (".env.bootstrap" -> "<base>.bootstrap.<token>"); the default ".env" contributes nothing
    # ("<base>.<token>", the pre-existing naming).
    $prefixSegment = ($OverlayPrefix -replace '^\.env\.?', '').Trim('.')
    $derivedName = if ([string]::IsNullOrEmpty($prefixSegment)) {
        "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).$token"
    } else {
        "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).$prefixSegment.$token"
    }
    $derivedPath = Join-Path (Join-Path $DockerComposeRoot ".derived") $derivedName

    return New-DataStandardDerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -OverlayEnvironmentFile $overlayPath `
        -TargetPath $derivedPath
}

function Convert-TemplatePackageToken {
    <#
    .SYNOPSIS
        Rewrites the engine segment of a DATABASE_TEMPLATE_PACKAGE-shaped package id, leaving
        every other segment (including the template and version) untouched.

    .DESCRIPTION
        Package ids follow the shape <prefix>.<template>.Template.<engine>.<version>, e.g.
        EdFi.Api.Populated.Template.PostgreSql.5.2.0 or EdFi.Dms.Minimal.Template.MsSql.6.1.0.
        <prefix> varies (EdFi.Api, EdFi.Dms, ...) and is preserved verbatim, as are the
        template segment (Minimal/Populated/Smoke) and <version>. When PackageId does not
        match the expected shape (blank, or an unrecognized format), it is returned unchanged.

    .PARAMETER PackageId
        The package id to rewrite.

    .PARAMETER Engine
        Target engine token ("PostgreSql" or "MsSql") to replace the existing engine segment.

    .OUTPUTS
        [string] The rewritten package id, or PackageId unchanged when it is blank or does not
        match the expected <template>.Template.<engine>.<version> shape.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$PackageId,
        [Parameter(Mandatory)]
        [ValidateSet("PostgreSql", "MsSql")]
        [string]$Engine
    )

    if ([string]::IsNullOrWhiteSpace($PackageId)) {
        return $PackageId
    }

    $match = [regex]::Match($PackageId, '^(?<prefix>.+)\.(?<template>Minimal|Populated|Smoke)\.Template\.(?<engine>PostgreSql|MsSql)\.(?<version>.+)$')
    if (-not $match.Success) {
        return $PackageId
    }

    return "$($match.Groups['prefix'].Value).$($match.Groups['template'].Value).Template.$Engine.$($match.Groups['version'].Value)"
}

function Test-MssqlConnectionStringValue {
    param(
        [AllowEmptyString()]
        [string]$ConnectionString
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $false
    }

    # Require a SQL Server data-source keyword at a connection-string segment boundary.
    # This distinguishes SQL Server values from the PostgreSQL host=... strings carried by
    # the shared base env files while accepting the standard SqlClient aliases.
    return [regex]::IsMatch(
        $ConnectionString,
        '(?:^|;)\s*(?:Server|Data Source|Address|Addr|Network Address)\s*=',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
}

function Get-MssqlComposedEnvContent {
    <#
    .SYNOPSIS
        Returns exactly the lines the MSSQL engine composition would write, without writing anything.

    .DESCRIPTION
        Validation and the write must be about the SAME artifact. Modelling the composed environment
        separately - resolving the base file and the overlay independently and preferring the base value -
        does not describe the file that actually gets produced, and the two disagreed in three ways:

          - a base value that depends on an overlay default (DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}
            where MSSQL_DB_NAME lives only in .env.mssql) froze EMPTY in the model, while the written
            file places the preserved value inside the overlay block, after MSSQL_DB_NAME, where it
            resolves;
          - an `export `-spelled base declaration was not recognized as an overlay-owned key, so the
            written file kept it AND appended the overlay's own declaration - two declarations of one
            key;
          - an outer-quoted or whole-reference connection string passed the resolved gate and was then
            dropped by a raw-text shape check, so the written file silently carried the overlay default
            instead of the caller's value.

        This function is pure and prospective: the caller evaluates these lines sequentially, validates
        that result, and writes these same lines. Overlay-owned keys are dropped from the base block
        through the shared assignment grammar (so `export KEY=...` counts), and a preserved caller value
        is substituted at the overlay's own position - which both guarantees exactly one declaration per
        overlay key and gives the preserved value the overlay's ordering.

    .PARAMETER ExcludeKey
        Overlay keys that must NOT be preserved from the base file even when declared there. The caller
        uses this to re-derive after discovering that a specific preserved connection string does not
        resolve to an MSSQL-shaped value. It is per KEY, not per category: the admin and CMS connection
        strings are independent settings, so a valid customized admin string must survive an invalid CMS
        string, and a PostgreSQL-shaped admin string must not be carried into an MSSQL environment just
        because the CMS string happens to be valid.
    #>
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$OverlayEnvironmentFile,
        [switch]$BaseDeclaresMssql,
        [string[]]$ExcludeKey = @(),
        [string]$TemplatePackageOverride
    )

    $baseLines = @([System.IO.File]::ReadAllLines($BaseEnvironmentFile))
    $overlayLines = @([System.IO.File]::ReadAllLines($OverlayEnvironmentFile))

    $overlayKeys = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $overlayLines) {
        $assignment = Get-DotenvAssignment -Line $line
        if ($null -ne $assignment -and -not $overlayKeys.Contains($assignment.Key)) {
            $overlayKeys.Add($assignment.Key)
        }
    }

    # Caller-authored values for overlay-owned keys are preserved, except the two engine discriminators
    # which the overlay always owns. The raw value is carried verbatim so authored quoting survives.
    $preserved = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    $excluded = [System.Collections.Generic.List[string]]::new()
    foreach ($key in $ExcludeKey) { if (-not $excluded.Contains($key)) { $excluded.Add($key) } }
    if ($BaseDeclaresMssql) {
        $baseEvaluation = Resolve-DotenvFileSequentially -Line $baseLines
        foreach ($key in $overlayKeys) {
            if ($key -eq 'DMS_DATASTORE' -or $key -eq 'DMS_CONFIG_DATASTORE') { continue }
            if ($excluded.Contains($key)) { continue }

            $declaration = Get-DotenvLastDeclaration -Evaluation $baseEvaluation -Name $key
            if ($null -ne $declaration -and -not [string]::IsNullOrWhiteSpace($declaration.RawValue)) {
                $preserved[$key] = [string]$declaration.RawValue
            }
        }
    }

    $keptBaseLines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $baseLines) {
        $assignment = Get-DotenvAssignment -Line $line
        if ($null -ne $assignment -and $overlayKeys.Contains($assignment.Key)) { continue }
        $keptBaseLines.Add($line)
    }

    $composedOverlayLines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $overlayLines) {
        $assignment = Get-DotenvAssignment -Line $line
        if ($null -ne $assignment -and $preserved.ContainsKey($assignment.Key)) {
            $composedOverlayLines.Add("$($assignment.Key)=$($preserved[$assignment.Key])")
        }
        else {
            $composedOverlayLines.Add($line)
        }
    }

    $composed = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $keptBaseLines) { $composed.Add($line) }
    while ($composed.Count -gt 0 -and [string]::IsNullOrWhiteSpace($composed[$composed.Count - 1])) {
        $composed.RemoveAt($composed.Count - 1)
    }
    $composed.Add('')
    foreach ($line in $composedOverlayLines) { $composed.Add($line) }
    while ($composed.Count -gt 0 -and [string]::IsNullOrWhiteSpace($composed[$composed.Count - 1])) {
        $composed.RemoveAt($composed.Count - 1)
    }

    if (-not [string]::IsNullOrWhiteSpace($TemplatePackageOverride)) {
        $replaced = $false
        for ($index = 0; $index -lt $composed.Count; $index++) {
            if (Test-DotenvAssignmentLine -Line $composed[$index] -Key 'DATABASE_TEMPLATE_PACKAGE') {
                $composed[$index] = "DATABASE_TEMPLATE_PACKAGE=$TemplatePackageOverride"
                $replaced = $true
            }
        }
        if (-not $replaced) { $composed.Add("DATABASE_TEMPLATE_PACKAGE=$TemplatePackageOverride") }
    }

    return , @($composed)
}

function Assert-MssqlCmsDatabaseIsShared {
    <#
    .SYNOPSIS
        Enforces the legacy shared-mode invariant: a caller-authored CMS MSSQL connection string must
        target MSSQL_DB_NAME, so CMS and the self-contained OpenIddict store cannot silently land in
        different databases.

    .DESCRIPTION
        Takes ALREADY-RESOLVED values. Resolution belongs to the caller so the connection string is
        resolved exactly once, against one composed environment, and the shape check, the reserved-name
        signal, and this invariant all judge the same text. This function previously resolved the string
        itself with a narrow grammar that accepted only a literal or a bare ${NAME} and threw
        "unsupported environment expression" on anything else - so a connection string using a
        documented Compose operator, including the ${A:-${B}} form the checked-in .yml fallbacks
        themselves use, could not be validated at all.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ResolvedConnectionString,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ExpectedDatabaseName
    )

    $expectedDatabaseName = $ExpectedDatabaseName
    if ([string]::IsNullOrWhiteSpace($expectedDatabaseName)) {
        throw "MSSQL_DB_NAME must be non-blank when preserving a caller-authored CMS MSSQL connection string."
    }

    $resolvedConnectionString = $ResolvedConnectionString

    # Validate that the resolved text is a parseable connection string before reading segments from it,
    # so a malformed value still gets its own diagnostic rather than looking like a missing database.
    try {
        $parseProbe = [System.Data.Common.DbConnectionStringBuilder]::new()
        $parseProbe.set_ConnectionString($resolvedConnectionString)
    }
    catch {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING is not a valid connection string."
    }

    $databaseNames = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString $resolvedConnectionString)
    if ($databaseNames.Count -eq 0) {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING must include Database or Initial Catalog and target MSSQL_DB_NAME ('$expectedDatabaseName')."
    }

    foreach ($actualDatabaseName in $databaseNames) {
        if (-not [string]::Equals(
            $actualDatabaseName,
            $expectedDatabaseName,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            throw "MSSQL shared-database configuration mismatch: DMS_CONFIG_DATABASE_CONNECTION_STRING targets '$actualDatabaseName', but MSSQL_DB_NAME resolves to '$expectedDatabaseName'. DMS-1255 requires CMS and OpenIddict to share MSSQL_DB_NAME; align the values. Separate CMS database support is tracked by DMS-1270."
        }
    }
}

function Resolve-DatabaseEngineEnvironmentFile {
    <#
    .SYNOPSIS
        Returns the effective environment file path for the requested database engine. With the
        default "postgresql" engine the base file is returned unchanged. With "mssql" the
        .env.mssql overlay (DMS_DATASTORE=mssql, the MSSQL_* keys, and the SQL Server admin
        connection string) is composed onto the base into a derived file under
        <DockerComposeRoot>/.derived/ and that path is returned. DATABASE_TEMPLATE_PACKAGE
        (inherited from the base file - .env.mssql never carries it, so DS-version and
        Minimal/Populated variance keep coming from the base file) is rewritten from its
        PostgreSql engine token to MsSql in the returned file.

    .DESCRIPTION
        Composition goes through Get-MssqlComposedEnvContent, which returns exactly the lines that will
        be written, so DMS_DATASTORE and the SQL Server connection strings reach every phase - configure,
        provision, and the start scripts - from one canonical path. Without this, a run could provision
        an MSSQL data store in CMS while the DMS container itself still starts on its postgresql default
        (local-dms.yml AppSettings__Datastore), since that setting comes only from the env file.

        Validation and the write describe the SAME artifact: the prospective lines are evaluated
        sequentially, that result is validated, and those same lines are written. An earlier design
        validated a separately-modelled environment and re-composed on the way out through
        New-DataStandardDerivedEnvFile plus a second pass of raw-text key overrides; the two diverged.

        Idempotency guard: when the base file already carries every non-blank key from the current
        .env.mssql overlay, with both datastore discriminators set to mssql, the base file is
        returned unchanged instead of composing a derived-of-derived file. Reading the required
        key set from the overlay keeps this proof current when the overlay gains a new engine-owned
        setting. The unchanged return additionally requires the original file to be semantically
        identical to the prospective composition - every overlay-owned key rendering the same effective
        value - because returning the original otherwise hands back an artifact that was never
        validated: a complete file whose connection string precedes the seam alias it references freezes
        an empty database, while the composition relocates both into safe order. If
        DATABASE_TEMPLATE_PACKAGE still carries a stale PostgreSql engine token, a corrected derived file
        is materialized rather than mutating the caller's source file.

        A partial hand-authored MSSQL env is completed from the overlay. Non-blank custom MSSQL
        credentials, database names, and ports are preserved. Connection strings are preserved only
        when they contain a SQL Server data-source keyword; PostgreSQL-shaped values inherited from
        a partially edited base file are replaced by the MSSQL overlay. A caller-authored CMS MSSQL
        connection string must resolve to MSSQL_DB_NAME so CMS and self-contained OpenIddict cannot
        silently target different databases; a mismatch fails before any derived file is written -
        unless the file declares the separate CMS database topology (marker or reserved
        'edfi_configurationservice' target; see the -SkipMssqlCmsDatabaseValidation note), which is
        outside that shared-mode invariant by definition. DMS_DATASTORE and DMS_CONFIG_DATASTORE
        are always forced to mssql.

    .PARAMETER DatabaseEngine
        "postgresql" (default; no-op) or "mssql".

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file. Must exist.

    .PARAMETER DockerComposeRoot
        Directory holding .env.mssql and the .derived output. Defaults to this module's
        directory (eng/docker-compose).

    .PARAMETER SkipMssqlCmsDatabaseValidation
        Skips the legacy CMS/OpenIddict shared-database invariant. Two caller classes pass $true:
        the dedicated database-only diagnostic startup and teardown, where neither CMS nor
        OpenIddict is initialized so there is nothing to validate; and the CMS-participating
        start-script branches and the wrapper's pre-resolution chain, where the check is superseded
        rather than absent - they run the stricter, topology-aware
        Confirm-CmsDatabaseTopologyAgreement immediately afterwards (DMS-1270). Every other caller
        (the configure, provision, and E2E environment resolutions) leaves it off, keeping the
        legacy check in force for shared-mode files.

        The invariant is also skipped automatically, regardless of this switch, when the base env file
        declares the separate CMS database topology (DMS-1270). That check asserts a *shared-mode*
        invariant - CMS and OpenIddict must both target MSSQL_DB_NAME - which is by definition false
        for a file declaring a dedicated CMS database, whatever the file's provenance. Two content
        signals declare it: the marker (DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true), and a CMS
        connection string whose every recognized database-name segment resolves to the reserved
        dedicated name 'edfi_configurationservice'. Both are raw topology declarations read from the
        file's own content, not authenticity signals: a hand-authored or copied file carrying either
        is exempted exactly like one this design's own migration wrote, and that is the intended
        semantics - the file has declared itself outside the shared-mode contract, and the paths
        that actually operate CMS validate it with the topology-aware
        Confirm-CmsDatabaseTopologyAgreement instead. The reserved-name signal is what lets the
        documented standalone continuation (a -SeparateConfigDatabase start phase followed by direct
        configure/provision invocations against the same caller-authored source file) proceed: the
        marker exists only in the start script's derived file, never in the caller's source file.

        This matters for every caller that passes no switch here - the configure and provision
        phases, build-dms.ps1's E2E environment resolutions, and provision-e2e-database.ps1 - all of
        which own the DMS datastore and take no part in the CMS seam. Without the exemption they
        would reject the very configuration a preceding -SeparateConfigDatabase start phase
        established. It is read raw (never through Compose precedence), so an ambient shell variable
        cannot switch the invariant off for a file that does not itself carry the marker.
    #>
    param(
        [string]$DatabaseEngine = "postgresql",
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot,
        [switch]$SkipMssqlCmsDatabaseValidation
    )

    if ($DatabaseEngine -ne "mssql") {
        return $BaseEnvironmentFile
    }

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    $derivedName = "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).mssql"
    $derivedPath = Join-Path (Join-Path $DockerComposeRoot ".derived") $derivedName

    $overlayPath = Join-Path $DockerComposeRoot ".env.mssql"
    if (-not (Test-Path -LiteralPath $overlayPath -PathType Leaf)) {
        throw "Resolve-DatabaseEngineEnvironmentFile: no MSSQL engine overlay found (expected '$overlayPath')."
    }

    # Every decision below reads BOTH files through the sequential model, so `export KEY=...`, an
    # assignment with whitespace around '=', and duplicate declarations all mean here exactly what they
    # mean to Docker Compose. The overlay inventory in particular used the legacy parser, which stores
    # `export KEY=...` under an `export `-prefixed name: an overlay declared that way was mis-keyed, the
    # completeness proof could never be satisfied for it, and a re-entered file kept re-deriving.
    $baseEvaluation = Resolve-DotenvFileSequentially -Path $BaseEnvironmentFile
    $overlayEvaluation = Resolve-DotenvFileSequentially -Path $overlayPath
    $overlayValueKeys = [System.Collections.Generic.List[string]]::new()
    foreach ($declaration in $overlayEvaluation.Declarations) {
        if (-not $overlayValueKeys.Contains($declaration.Key)) { $overlayValueKeys.Add($declaration.Key) }
    }
    $templatePackage = [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name "DATABASE_TEMPLATE_PACKAGE")
    $correctedTemplatePackage = Convert-TemplatePackageToken -PackageId $templatePackage -Engine "MsSql"
    $baseDeclaresMssql =
        [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name "DMS_DATASTORE") -eq "mssql" -or
        [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name "DMS_CONFIG_DATASTORE") -eq "mssql"

    # A separate-mode file is out of the shared-mode invariant's scope by construction; see the
    # -SkipMssqlCmsDatabaseValidation note above. Read the marker from its own raw declaration through
    # the shared assignment model and never from the ambient environment: the exported and quoted marker
    # spellings are supported everywhere else in this design, and an unrelated ambient variable of the
    # marker's name must not be able to switch a safety check off.
    $baseMarkerDeclaration = Get-DotenvLastDeclaration -Evaluation $baseEvaluation -Name "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
    $baseMarkerValue =
        if ($null -eq $baseMarkerDeclaration) { "false" }
        else { [string](ConvertFrom-ComposeEnvironmentValue -Value $baseMarkerDeclaration.RawValue) }
    $baseDeclaresSeparateTopology = [string]::Equals($baseMarkerValue, "true", [System.StringComparison]::Ordinal)

    # The exact lines composition would write, built once and used for validation AND for the write, so
    # the two can never describe different artifacts. Connection-string preservation is optimistic here
    # and withdrawn below if the preserved value does not resolve to an MSSQL-shaped connection string.
    $composedLines = Get-MssqlComposedEnvContent `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -OverlayEnvironmentFile $overlayPath `
        -BaseDeclaresMssql:$baseDeclaresMssql `
        -TemplatePackageOverride $(if ($correctedTemplatePackage -ne $templatePackage) { $correctedTemplatePackage } else { $null })
    $composedEvaluation = Resolve-DotenvFileSequentially -Line $composedLines

    # THE AUTHORITY MODEL. For an MSSQL run the FINAL Compose-effective environment - after composition
    # and after ambient precedence - is the only thing that decides validity, and every transformation is
    # followed by evaluating the exact candidate artifact. Two consequences drive the shape below:
    #
    #   A file rewrite can repair a FILE-AUTHORED value. It can never repair an AMBIENT override, because
    #   ambient wins over every declaration in the file being written. Excluding a key from preservation
    #   and recomposing in that case changes nothing about the effective value, so it must not be
    #   attempted - it produced a derived file whose admin/CMS connection string was still
    #   PostgreSQL-shaped on an MSSQL run.
    #
    #   Whatever the repairs achieve, the result is proven before anything is returned or written: an
    #   unconditional postcondition requires EVERY overlay-owned connection-string key to be MSSQL-shaped
    #   in the final effective environment, or the function fails.
    #
    # Preservation decisions are per KEY, never per category: the admin and CMS strings are independent
    # settings, and an all-or-nothing rule keyed on the CMS string either wrote a PostgreSQL admin string
    # into an MSSQL environment or discarded a valid customized admin one.
    $connectionStringKeys = @($overlayValueKeys | Where-Object { $_ -match 'CONNECTION_STRING' })
    $unshapedConnectionStringKeys = [System.Collections.Generic.List[string]]::new()
    foreach ($connectionStringKey in $connectionStringKeys) {
        $effective = [string](Get-SequentialEffectiveValue -Evaluation $composedEvaluation -Name $connectionStringKey)
        if (Test-MssqlConnectionStringValue -ConnectionString $effective) { continue }

        # Only a file-authored value is repairable by re-composing the file.
        if ($null -eq [System.Environment]::GetEnvironmentVariable($connectionStringKey)) {
            $unshapedConnectionStringKeys.Add($connectionStringKey)
        }
    }

    if ($unshapedConnectionStringKeys.Count -gt 0) {
        $composedLines = Get-MssqlComposedEnvContent `
            -BaseEnvironmentFile $BaseEnvironmentFile `
            -OverlayEnvironmentFile $overlayPath `
            -BaseDeclaresMssql:$baseDeclaresMssql `
            -ExcludeKey @($unshapedConnectionStringKeys) `
            -TemplatePackageOverride $(if ($correctedTemplatePackage -ne $templatePackage) { $correctedTemplatePackage } else { $null })
        $composedEvaluation = Resolve-DotenvFileSequentially -Line $composedLines
    }

    # UNCONDITIONAL POSTCONDITION over every overlay-owned connection-string key, run after all repairs
    # and before anything is returned or written. This is the single place that decides an MSSQL run is
    # usable, so no branch can bypass it: not the already-composed early return, not a skipped CMS
    # invariant, not a separate-topology file. Where the offending value came from the ambient
    # environment, no rewrite of any file could fix it, and the diagnostic says so instead of writing a
    # derived file that changes nothing.
    #
    # The message names ONLY the key. Connection strings carry credentials and this reaches terminals and
    # CI logs, so no value, database name, or username is rendered.
    foreach ($connectionStringKey in $connectionStringKeys) {
        $finalEffective = [string](Get-SequentialEffectiveValue -Evaluation $composedEvaluation -Name $connectionStringKey)
        if (Test-MssqlConnectionStringValue -ConnectionString $finalEffective) { continue }

        if ($null -ne [System.Environment]::GetEnvironmentVariable($connectionStringKey)) {
            throw "MSSQL engine composition cannot proceed: the ambient environment sets '$connectionStringKey' to a value that is not a SQL Server connection string. Docker Compose gives an ambient value precedence over every declaration in the environment file, so the .env.mssql overlay cannot repair it. Unset or correct '$connectionStringKey' in your shell, then re-run. (The value is withheld because a connection string contains credentials.)"
        }

        throw "MSSQL engine composition cannot proceed: '$connectionStringKey' does not resolve to a SQL Server connection string in the composed environment for '$BaseEnvironmentFile'. Correct that key, or remove it so the .env.mssql overlay supplies the SQL Server default. (The value is withheld because a connection string contains credentials.)"
    }

    $composedCmsConnectionString = [string](Get-SequentialEffectiveValue `
        -Evaluation $composedEvaluation `
        -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING")

    if ($baseDeclaresMssql -and -not $SkipMssqlCmsDatabaseValidation -and -not $baseDeclaresSeparateTopology) {
        # Validation judges the CONNECTION STRING THE COMPOSED FILE ACTUALLY RENDERS. Judging a
        # separately-modelled environment instead let three shapes diverge from the written file: a base
        # value depending on an overlay default froze empty, an `export `-spelled declaration survived
        # alongside the overlay's own, and an outer-quoted or whole-reference value was dropped by a
        # raw-text shape check after passing the resolved gate.
        $resolvedCmsConnectionString = $composedCmsConnectionString

        if (Test-MssqlConnectionStringValue -ConnectionString $resolvedCmsConnectionString) {

            # A CMS connection string targeting the reserved dedicated database name is ALSO a
            # separate-topology declaration - content, not provenance, the same doctrine as the
            # marker above. The documented standalone continuation (start-local-dms.ps1 -InfraOnly
            # -SeparateConfigDatabase, then configure-local-data-store.ps1 / provision-dms-schema.ps1
            # against the SAME caller-authored source file) reaches this gate with no switch and no
            # marker (the marker lives only in the start script's derived file), and the reserved
            # name is the only signal left. Only an unambiguous declaration skips the invariant:
            # every recognized database-name segment must resolve to 'edfi_configurationservice'; a
            # mixed or unparseable connection string still runs the invariant and fails loudly there.
            #
            # Resolution uses the SAME Compose-equivalent semantics as the start path, not the legacy
            # single-${NAME} grammar. Those two disagreed, and the disagreement split a run in half: a
            # caller-authored
            #   Database=${DMS_CONFIG_DATABASE_NAME:-${MSSQL_DB_NAME}}
            # - an operator form the checked-in .yml fallbacks themselves use - passed the start
            # phase's topology validation and then threw "unsupported environment expression" here, in
            # the very manual phases the start script's terminal guidance points to. Both the signal and
            # the invariant read the database segments off the single already-resolved value above.
            $candidateDatabaseNames = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString $resolvedCmsConnectionString)
            $targetsDedicatedCmsDatabase =
                $candidateDatabaseNames.Count -gt 0 -and
                @($candidateDatabaseNames | Where-Object {
                    -not [string]::Equals($_, "edfi_configurationservice", [System.StringComparison]::OrdinalIgnoreCase)
                }).Count -eq 0

            if (-not $targetsDedicatedCmsDatabase) {
                $expectedSharedDatabaseName = [string](Get-SequentialEffectiveValue `
                    -Evaluation $composedEvaluation `
                    -Name "MSSQL_DB_NAME")
                Assert-MssqlCmsDatabaseIsShared `
                    -ResolvedConnectionString $resolvedCmsConnectionString `
                    -ExpectedDatabaseName $expectedSharedDatabaseName
            }
        }
    }

    # A fixed three-key signal can become stale when .env.mssql gains another required setting. Prove
    # that every current overlay key exists and is non-blank in the base file's own SEQUENTIAL
    # evaluation before treating it as an already-composed handoff from an earlier phase - and for a
    # connection string, that its RESOLVED value is MSSQL-shaped, matching the decision the composition
    # itself makes.
    $overlayAlreadyComposed =
        [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name "DMS_DATASTORE") -eq "mssql" -and
        [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name "DMS_CONFIG_DATASTORE") -eq "mssql"
    if ($overlayAlreadyComposed) {
        foreach ($overlayKeyName in $overlayValueKeys) {
            $baseValue = [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name $overlayKeyName)
            $isConnectionString = $overlayKeyName -match 'CONNECTION_STRING'
            if (
                [string]::IsNullOrWhiteSpace($baseValue) -or
                ($isConnectionString -and -not (Test-MssqlConnectionStringValue -ConnectionString $baseValue))
            ) {
                $overlayAlreadyComposed = $false
                break
            }
        }
    }

    # Returning the ORIGINAL file unchanged means returning an artifact that was never validated - the
    # validation above judged the prospective composition. Those two can differ even for a "complete"
    # file: declare DMS_CONFIG_DATABASE_CONNECTION_STRING before DMS_CONFIG_DATABASE_NAME and the
    # original freezes an empty database, while the composition relocates both into the overlay's safe
    # order and renders correctly. The completeness loop above would still accept the original, because
    # its raw text contains 'Server='.
    #
    # So the unchanged return additionally requires the original to be SEMANTICALLY IDENTICAL to the
    # validated composition: every overlay-owned key must render the same effective value in both. When
    # it does not, the file falls through to the write below and is repaired rather than handed back
    # broken.
    if ($overlayAlreadyComposed) {
        foreach ($overlayKeyName in $overlayValueKeys) {
            $originalValue = [string](Get-SequentialEffectiveValue -Evaluation $baseEvaluation -Name $overlayKeyName)
            $composedValue = [string](Get-SequentialEffectiveValue -Evaluation $composedEvaluation -Name $overlayKeyName)
            if (-not [string]::Equals($originalValue, $composedValue, [System.StringComparison]::Ordinal)) {
                $overlayAlreadyComposed = $false
                break
            }
        }
    }

    if ($overlayAlreadyComposed) {
        if ($correctedTemplatePackage -eq $templatePackage) {
            return $BaseEnvironmentFile
        }

        Write-DerivedEnvFile `
            -BaseEnvironmentFile $BaseEnvironmentFile `
            -TargetPath $derivedPath `
            -KeyOverrides @{ DATABASE_TEMPLATE_PACKAGE = $correctedTemplatePackage }

        return $derivedPath
    }

    # Write exactly the lines that were validated above. Re-composing here through a different code
    # path (base+overlay merge, then a second pass of key overrides decided on raw text) is what let the
    # written file differ from the validated one.
    $targetDirectory = Split-Path -Parent $derivedPath
    if (-not [string]::IsNullOrWhiteSpace($targetDirectory) -and -not (Test-Path -LiteralPath $targetDirectory)) {
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $derivedPath,
        (($composedLines -join "`n").TrimEnd("`n") + "`n"),
        [System.Text.UTF8Encoding]::new($false))

    return $derivedPath
}

function ConvertTo-DotenvSafeEnvValue {
    <#
    .SYNOPSIS
        Returns a dotenv/Compose-safe serialization of a single concrete value: bare when the value
        needs no protection, single-quoted (with the embedded-apostrophe form backslash-escaped, not
        doubled) when it contains a space, a '#', a '$', or opens with a quote character that would
        otherwise be misread as delimiting the value.

    .DESCRIPTION
        Only for concrete, already-resolved values this design writes itself (the internal topology
        marker and DMS_CONFIG_DATABASE_NAME) - never for a caller-authored connection string, whose
        existing quoting this design always preserves exactly as authored. Single-quoting is safe
        here specifically because these two values are never themselves a ${VAR} reference this
        design introduces (the marker is always "true"/"false"; DMS_CONFIG_DATABASE_NAME's shared-
        mode value is the ambient-aware-resolved concrete database name, not an alias) - unlike the
        connection string's ${DMS_CONFIG_DATABASE_NAME} reference, which single-quoting would freeze.

        A concrete value containing a literal '$' must be quoted too (Round 10 Blocker 2):
        Resolve-ComposeEnvReference's interpolation regex matches both ${NAME} and a bare $NAME (no
        braces required), so an unquoted value like tenant$db, once written back to a derived file
        and re-read, would have $db misinterpreted as a reference to an unset variable named "db" and
        silently collapse to just "tenant". Quoting unconditionally on any '$' (rather than only when
        it is followed by an identifier-shaped reference) is the simpler, strictly safer rule -
        over-quoting a harmless '$' is free; under-quoting a dangerous one is not.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value.Length -eq 0) {
        return $Value
    }

    $needsQuoting = $Value.Contains(" ") -or $Value.Contains("#") -or $Value.Contains('$') -or $Value[0] -in @("'", '"')
    if (-not $needsQuoting) {
        return $Value
    }

    return "'" + $Value.Replace("'", "\'") + "'"
}

function Find-ConnectionStringLegacyTokenSpan {
    <#
    .SYNOPSIS
        Locates the exact source span of $LegacyToken within a connection string's database-segment
        value (a recognized Database / Initial Catalog key), or returns $null when no such segment's
        value is exactly $LegacyToken.

    .DESCRIPTION
        A hand-written quote-aware scanner, not a regex over the raw text: the connection string is
        split into top-level key=value segments by tracking single/double-quote state (a quote
        character toggles a quoted region; a doubled quote character inside a quoted region is an
        escaped literal quote, not a closing delimiter), so a ';' or '=' appearing inside a quoted
        value - a password, for instance - is never mistaken for a segment delimiter or key/value
        separator. Round 9 Blocker 1: a plain regex lookbehind/lookahead has no concept of quoting, so
        the same literal token text embedded in an unrelated quoted segment (e.g.
        Password="keep;Database=${POSTGRES_DB_NAME};inside-password") was incorrectly matched and
        rewritten.

        Only an unquoted database-segment value is matched: the checked-in templates never quote it,
        and a caller-authored quoted value is conservatively left untouched rather than guessing at
        escape-unescaping - not a functional regression, since a quoted database value was never part
        of this migration's supported shape.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ConnectionString,

        [Parameter(Mandatory)]
        [string]$LegacyToken
    )

    $length = $ConnectionString.Length
    $segmentStart = 0
    $quoteChar = $null
    $index = 0

    while ($index -le $length) {
        $atEnd = $index -eq $length
        $ch = if ($atEnd) { [char]0 } else { $ConnectionString[$index] }

        if (-not $atEnd -and $null -ne $quoteChar) {
            if ($ch -eq $quoteChar) {
                if ($index + 1 -lt $length -and $ConnectionString[$index + 1] -eq $quoteChar) {
                    $index += 2
                    continue
                }
                $quoteChar = $null
            }
            $index++
            continue
        }

        if (-not $atEnd -and ($ch -eq '"' -or $ch -eq "'")) {
            $quoteChar = $ch
            $index++
            continue
        }

        if ($atEnd -or $ch -eq ';') {
            $segmentText = $ConnectionString.Substring($segmentStart, $index - $segmentStart)
            $equalsIndex = $segmentText.IndexOf('=')
            if ($equalsIndex -ge 0) {
                $key = $segmentText.Substring(0, $equalsIndex).Trim()
                if ($key -imatch '^(database|initial\s*catalog)$') {
                    $rawValue = $segmentText.Substring($equalsIndex + 1)
                    $trimmedValue = $rawValue.Trim()
                    if ([string]::Equals($trimmedValue, $LegacyToken, [System.StringComparison]::Ordinal)) {
                        $valueLeadingWhitespace = $rawValue.Length - $rawValue.TrimStart().Length
                        $absoluteStart = $segmentStart + $equalsIndex + 1 + $valueLeadingWhitespace
                        return [pscustomobject]@{ Start = $absoluteStart; Length = $LegacyToken.Length }
                    }
                }
            }
            $segmentStart = $index + 1
        }

        $index++
    }

    return $null
}

function Get-DotenvClosingQuoteIndex {
    <#
    .SYNOPSIS
        Returns the index of the closing dotenv-level quote character that wraps $RawValue's whole
        value, or -1 when $RawValue is not dotenv-quoted at all (or its trailing content after the
        closing quote is neither empty nor a "#"-led inline comment, meaning the leading quote
        character is not actually a wrapper).

    .DESCRIPTION
        Get-EnvValue returns the raw dotenv value verbatim - including any outer quote wrapper AND
        any trailing inline comment after it (ReadValuesFromEnvFile does not strip either). Mirrors
        ConvertFrom-ComposeEnvironmentValue's own closing-quote-detection algorithm exactly (a
        backslash-escape-aware scan for the matching quote character, then requiring the trailing
        content after it to be empty or start with "#") so this migration's wrapper detection agrees
        with how Compose itself would actually parse the same value - including a valid trailing
        " # comment" after the closing quote (Round 11 Blocker 2), which a simpler
        "last character equals the opening quote" check mistakes for proof the value is not quoted at
        all, since the actual last character is part of the comment instead.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$RawValue
    )

    if ($RawValue.Length -lt 1 -or $RawValue[0] -notin @("'", '"')) {
        return -1
    }

    $quoteChar = $RawValue[0]
    $escaped = $false
    for ($index = 1; $index -lt $RawValue.Length; $index++) {
        $character = $RawValue[$index]
        if ($character -eq "\" -and -not $escaped) {
            $escaped = $true
            continue
        }
        if ($character -eq $quoteChar -and -not $escaped) {
            $trailingContent = $RawValue.Substring($index + 1).Trim()
            if ([string]::IsNullOrEmpty($trailingContent) -or $trailingContent.StartsWith("#")) {
                return $index
            }
            return -1
        }
        $escaped = $false
    }

    return -1
}

function Move-EnvFileKeyBeforeAnotherKey {
    <#
    .SYNOPSIS
        Reorders a .env file in place so $KeyToMove's line appears immediately before $BeforeKey's
        line, if it does not already precede it. No-ops if either key is absent from the file, or if
        the ordering already holds.

    .DESCRIPTION
        Docker Compose's --env-file interpolation is order-dependent, like shell `source` semantics -
        confirmed empirically against a real Docker Compose invocation: a ${VAR} reference resolves
        only against variables defined earlier in the same file; a forward reference (the referenced
        key's own definition appears later) resolves to empty, not to its later-defined value.

        Write-DerivedEnvFile replaces an existing key's line in place, preserving its original
        position, but appends a genuinely new key after whatever the base file already contained. When
        DMS_CONFIG_DATABASE_NAME is newly introduced into a base file that already defines
        DMS_CONFIG_DATABASE_CONNECTION_STRING (todays checked-in templates all still lack the seam key,
        so this is not a rare case - it is the ordinary shape the migration exists to handle), the new
        key always lands after the existing connection-string line regardless of KeyOverrides
        iteration order, since the existing line's position is untouched by the append. Left uncorrected,
        a migrated ${DMS_CONFIG_DATABASE_NAME} reference in the connection string would resolve to
        empty at real Compose render time, not to the intended database name - this function repairs
        that ordering after the fact, rather than changing Write-DerivedEnvFile's own general-purpose,
        widely-shared append/replace contract.
    #>
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$KeyToMove,
        [Parameter(Mandatory)] [string]$BeforeKey
    )

    $lines = [System.IO.File]::ReadAllLines($Path)
    $moveIndex = -1
    $beforeIndex = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        # One shared assignment grammar for detection, replacement, and movement. A narrower grammar
        # here than the detector's is what previously turned this into a silent no-op: a valid
        # "KEY = value" line was routed to repair, never matched, and the file was handed to Compose
        # still rendering that segment empty.
        if ($moveIndex -lt 0 -and (Test-DotenvAssignmentLine -Line $lines[$i] -Key $KeyToMove)) {
            $moveIndex = $i
        }
        if ($beforeIndex -lt 0 -and (Test-DotenvAssignmentLine -Line $lines[$i] -Key $BeforeKey)) {
            $beforeIndex = $i
        }
    }

    if ($moveIndex -lt 0) {
        # Every caller asks to move a key it has already observed in the file, so not finding it means
        # the grammars have drifted apart again. Fail loudly rather than silently declining the repair.
        throw "Move-EnvFileKeyBeforeAnotherKey: '$KeyToMove' is not declared in '$Path', so it cannot be moved."
    }

    if ($beforeIndex -lt 0 -or $moveIndex -lt $beforeIndex) {
        # Nothing to order against, or already ordered correctly.
        return
    }

    $reordered = [System.Collections.Generic.List[string]]::new($lines)
    $lineToMove = $reordered[$moveIndex]
    $reordered.RemoveAt($moveIndex)
    # $beforeIndex is unaffected by removing an entry after it, so it still names the correct target.
    $reordered.Insert($beforeIndex, $lineToMove)

    $content = ($reordered -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Path, $content, [System.Text.UTF8Encoding]::new($false))
}

function Get-ComposeServiceIdentityValue {
    <#
    .SYNOPSIS
        The literal identity declared by a `container_name:` / `hostname:` line, or $null when the value is
        not a token this function will admit as a network identity.

    .DESCRIPTION
        Applied in order: strip an inline comment, trim, drop one matching pair of surrounding quotes, then
        admit only an identifier-shaped token.

        The final identifier check is authoritative, and deliberately STRICTER than Compose - `docker
        compose config` does not validate `container_name` content at all, accepting values such as
        "bad name" and "db#1". This value decides which endpoint the Configuration Service may talk to, so
        anything that is not a plain hostname-shaped token is not admitted. That single rule is what makes
        several shapes narrow the accepted set rather than widen it:

          - an interpolated value ("${DB_HOST:-x}") contains characters outside the set;
          - a comment strip that mangled a quoted value ('"a # b"' -> '"a') fails the check, so mangled
            text can never become an accepted partial value;
          - a name with a space, or an empty value, fails the check.

    .OUTPUTS
        [string] the admitted identity, or $null.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$RawValue
    )

    # An inline comment starts at the first '#' that is preceded by whitespace, or at the start of the
    # value. YAML requires that separating whitespace, which is why a '#' inside a token (db#1) is left
    # alone here and rejected by the identifier check below instead.
    $text = $RawValue
    $commentMatch = [regex]::Match($text, '(^|[ \t])#')
    if ($commentMatch.Success) { $text = $text.Substring(0, $commentMatch.Index) }

    $text = $text.Trim()
    if ($text.Length -ge 2) {
        $quote = $text[0]
        if (($quote -eq '"' -or $quote -eq "'") -and $text[$text.Length - 1] -eq $quote) {
            $text = $text.Substring(1, $text.Length - 2)
        }
    }

    if ($text -match '^[A-Za-z0-9][A-Za-z0-9_.-]*$') { return $text }
    return $null
}

function Get-ComposeDatabaseServiceHostAlias {
    <#
    .SYNOPSIS
        Every name the composed database service answers to on the shared Compose network, read from the
        engine's own compose file.

    .DESCRIPTION
        On a user-defined Docker network a service is reachable by its service key, by its
        `container_name`, and by its `hostname`. Both postgresql.yml and mssql.yml deliberately name the
        service `db` - mssql.yml's own header says so, to make it a drop-in swap for postgresql.yml - while
        setting `container_name`/`hostname` to `dms-postgresql` / `dms-mssql`. All of those address the
        same database, so a CMS connection string may legitimately use any of them.

        The list is DERIVED, not hard-coded, so renaming the service, container, or hostname in a compose
        file cannot leave endpoint validation asserting a name the stack no longer answers to.

        This is a targeted read of a known file shape, NOT a general YAML parser. Because the accepted-host
        set is safety-relevant, the read is an explicit state machine with enumerated fail-closed outcomes
        rather than a set of accept/skip rules - three successive reviews each found a different input shape
        that slipped through the rule-based version, twice returning a wrong host.

        States, and the only transitions between them:

          SeekServices      -- `^services:[ ]*(#.*)?$` at column 0 --> SeekFirstService
                               end of file                         --> throw (no services mapping)

          SeekFirstService  -- the FIRST meaningful line here is authoritative and is consumed exactly
                               once. There is deliberately NO path back into this state, which is what
                               guarantees a later sibling can never be adopted as the database service:
                                 indent 0 or end of file           --> throw (no service entry)
                                 `^(?<indent> +)(?<name>[A-Za-z0-9_.-]+):[ ]*(#.*)?$` --> InService
                                 anything else                     --> throw (unsupported header)

          InService         -- indent <= the service key's indent   --> Complete (sibling service, or a
                                                                       top-level key after `services:`)
                               the first deeper line establishes the direct-child indent; only lines AT
                               that indent are considered, so keys inside a nested mapping
                               (environment:, healthcheck:, labels:, ...) are not identities
                               `container_name:` / `hostname:` at that indent --> Get-ComposeServiceIdentityValue

        Blank and comment-only lines never establish state: they do not satisfy "the first entry under
        services:" and they do not establish the child indent. Indentation is counted in SPACES only; a
        tab-indented compose file is rejected by Compose itself, so treating a tab as non-indentation
        routes such a file deterministically to a fail-closed outcome instead of to a guess.

        Unsupported first-service headers - anchors, aliases, merge keys, quoted keys, inline mappings,
        sequence entries - all fail closed, even though Compose accepts them, because the alternative is a
        validator that silently stops protecting the invariant it exists for. Every diagnostic names the
        compose file, the unsupported-header one also names the offending line number, and none echoes line
        content - so a compose line carrying an interpolated reference to a secret cannot be disclosed by a
        parse failure.

    .OUTPUTS
        [string[]] one or more distinct host names, in file order, service key first.
    #>
    param(
        [Parameter(Mandatory)] [ValidateSet("postgresql", "mssql")] [string]$DatabaseEngine,
        [string]$DockerComposeRoot
    )

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) { $DockerComposeRoot = $PSScriptRoot }
    $composeFile = Join-Path $DockerComposeRoot "$DatabaseEngine.yml"

    # With no compose file to read, fall back to the canonical container name ONLY - never to a guessed
    # service key. Absent input must not widen what endpoint validation accepts; it may only narrow it back
    # to the historical behavior. In production the compose files sit beside this module, so this path is
    # reached by isolated harnesses that stage the module without the .yml files.
    if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) {
        return @("dms-$DatabaseEngine")
    }

    $aliases = [System.Collections.Generic.List[string]]::new()
    $state = "SeekServices"
    $serviceIndent = -1
    $childIndent = -1
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadAllLines($composeFile)) {
        $lineNumber++

        # Blank and comment-only lines never establish state: they neither satisfy "the first entry under
        # services:" nor establish the direct-child indent.
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^ *#') { continue }

        # Spaces only. A tab in the indentation makes this 0, which routes the line to a fail-closed
        # outcome rather than to a guess - correct, because Compose rejects a tab-indented file outright.
        $indent = [regex]::Match($line, '^ *').Value.Length

        if ($state -eq "SeekServices") {
            if ($line -match '^services:[ ]*(#.*)?$') { $state = "SeekFirstService" }
            continue
        }

        if ($state -eq "SeekFirstService") {
            # Authoritative and consumed exactly once. Either this line IS the database service or the
            # file is not in a shape this function reads; the state changes either way, so no later
            # sibling can be adopted. Scanning forward from here is what let `db: # primary`,
            # `db: &database`, `"db":`, and `db: {...}` hand the accepted-host set to a DIFFERENT
            # container - the correct identities absent and a wrong one accepted.
            if ($indent -eq 0) {
                throw "Get-ComposeDatabaseServiceHostAlias: 'services:' in '$composeFile' declares no service entry."
            }

            $serviceMatch = [regex]::Match($line, '^(?<indent> +)(?<name>[A-Za-z0-9_.-]+):[ ]*(#.*)?$')
            if (-not $serviceMatch.Success) {
                throw "Get-ComposeDatabaseServiceHostAlias: the first service entry in '$composeFile' at line $lineNumber does not use the supported 'name:' header form; anchors, aliases, quoted keys, and inline mappings are not supported here."
            }

            $serviceIndent = $indent
            $aliases.Add($serviceMatch.Groups['name'].Value)
            $state = "InService"
            continue
        }

        # Anything back at or above the service key's own level ends this service's block: the next
        # service, or a top-level key following the services: mapping. Purely indentation-based, because a
        # rule that also required the line to LOOK like a bare key let a differently-shaped line at that
        # level fall through and the walk continue into the next service.
        if ($indent -le $serviceIndent) { break }

        # Only DIRECT children of the service are network identities. The service's children all share
        # one indent, established by the first of them; anything deeper belongs to a nested mapping such
        # as environment:, healthcheck:, or labels:, where a key spelled "hostname" is an environment
        # variable, a probe argument, or a label - not a name the container answers to. Matching at any
        # depth would let
        #
        #     environment:
        #       hostname: unrelated-host
        #
        # widen the accepted endpoint set to an unrelated host. The rule is "at the child indent", not
        # "before the first nested block", so a service-level alias declared after one is still read.
        if ($childIndent -lt 0) { $childIndent = $indent }
        if ($indent -ne $childIndent) { continue }

        $aliasMatch = [regex]::Match($line, '^ +(?:container_name|hostname):[ ]*(?<raw>.*)$')
        if (-not $aliasMatch.Success) { continue }

        $value = Get-ComposeServiceIdentityValue -RawValue $aliasMatch.Groups['raw'].Value
        if ($null -eq $value) { continue }
        if (-not $aliases.Contains($value)) { $aliases.Add($value) }
    }

    if ($state -eq "SeekServices") {
        throw "Get-ComposeDatabaseServiceHostAlias: no top-level 'services:' mapping found in '$composeFile'."
    }
    if ($state -eq "SeekFirstService") {
        throw "Get-ComposeDatabaseServiceHostAlias: 'services:' in '$composeFile' declares no service entry."
    }

    # Plain @() rather than the ",@()" no-unroll idiom used elsewhere in this module: the caller wraps the
    # result in @() itself, and the two together nest the array inside a one-element array that
    # stringifies as "db dms-postgresql" - which then matches no host at all.
    return @($aliases)
}

function Get-ConnectionStringSegmentDifference {
    <#
    .SYNOPSIS
        Names the connection-string segment keys whose values differ between two connection strings,
        WITHOUT revealing either value.

    .DESCRIPTION
        A connection string carries a database password, so a diagnostic that renders one leaks a
        credential into terminals and CI logs. Comparing segment keys keeps the message actionable -
        "password" or "database" is what a reader needs - while disclosing nothing. Segment values are
        compared only to decide equality; they are never returned. Keys are reported sorted so the
        message is stable. Both engines' segment syntaxes parse through DbConnectionStringBuilder, and
        an unparseable string degrades to a generic answer rather than falling back to raw text.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Expected,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Actual
    )

    $parse = {
        param([string]$value)
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        try { $builder.set_ConnectionString($value) } catch { return $null }
        $map = @{}
        foreach ($key in $builder.get_Keys()) { $map[[string]$key] = [string]$builder.get_Item($key) }
        return $map
    }

    $expectedSegments = & $parse $Expected
    $actualSegments = & $parse $Actual
    if ($null -eq $expectedSegments -or $null -eq $actualSegments) {
        return "(the rendered value could not be parsed as a connection string)"
    }

    $differing = [System.Collections.Generic.List[string]]::new()
    foreach ($key in @($expectedSegments.Keys) + @($actualSegments.Keys)) {
        if ($differing.Contains($key)) { continue }
        $expectedValue = if ($expectedSegments.ContainsKey($key)) { $expectedSegments[$key] } else { $null }
        $actualValue = if ($actualSegments.ContainsKey($key)) { $actualSegments[$key] } else { $null }
        if (-not [string]::Equals($expectedValue, $actualValue, [System.StringComparison]::Ordinal)) {
            $differing.Add($key)
        }
    }

    if ($differing.Count -eq 0) { return "(none identified)" }
    return (($differing | Sort-Object) -join ', ')
}

function Get-DotenvLastDeclaration {
    <#
    .SYNOPSIS
        The last declaration of a key in a sequential evaluation, or $null when the key is not
        declared. The LAST one is what the compose file's own final environment sees.
    #>
    param(
        [Parameter(Mandatory)] $Evaluation,
        [Parameter(Mandatory)] [string]$Name
    )

    # Ordinal: a dotenv identifier is case-sensitive on the Linux CI and runtime path, and PowerShell's
    # own -eq is case-insensitive.
    $declarationsForKey = @($Evaluation.Declarations | Where-Object { [string]::Equals($_.Key, $Name, [System.StringComparison]::Ordinal) })
    if ($declarationsForKey.Count -eq 0) { return $null }
    return $declarationsForKey[-1]
}

function Get-SequentialEffectiveValue {
    <#
    .SYNOPSIS
        The effective value of a key as the compose file would see it: ambient if set, else the last
        declaration's resolved value, else $null.

    .PARAMETER DefaultValue
        When supplied, returned in place of an absent or blank result - matching
        Get-ComposeResolvedEnvValue's documented fallback behavior.
    #>
    param(
        [Parameter(Mandatory)] $Evaluation,
        [Parameter(Mandatory)] [string]$Name,
        [string]$DefaultValue
    )

    $ambient = [System.Environment]::GetEnvironmentVariable($Name)
    $value =
        if ($null -ne $ambient) { $ambient }
        elseif ($Evaluation.Effective.ContainsKey($Name)) { [string]$Evaluation.Effective[$Name] }
        else { $null }

    if ($PSBoundParameters.ContainsKey('DefaultValue') -and [string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }
    return $value
}

function Test-DotenvReferenceResolvable {
    <#
    .SYNOPSIS
        True when a reference to Name, appearing on the line at BeforeLineIndex, resolves to something
        rather than to nothing.

    .DESCRIPTION
        Mirrors Compose's sequential rule: an ambient value always resolves; otherwise the name must
        have a declaration STRICTLY BEFORE the referencing line. A declaration at or after that line
        is a forward reference, which Compose renders as empty.
    #>
    param(
        [Parameter(Mandatory)] $Evaluation,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [int]$BeforeLineIndex
    )

    if ($null -ne [System.Environment]::GetEnvironmentVariable($Name)) { return $true }
    foreach ($declaration in $Evaluation.Declarations) {
        if ([string]::Equals($declaration.Key, $Name, [System.StringComparison]::Ordinal) -and
            $declaration.LineIndex -lt $BeforeLineIndex) {
            return $true
        }
    }
    return $false
}

function Get-DotenvSequentialLookup {
    <#
    .SYNOPSIS
        Builds a terminal-value lookup delegate over a sequential evaluation, for resolving a value in
        the same environment Compose would use.

    .DESCRIPTION
        Precedence matches Compose: ambient first, then any caller Override, then the evaluation's
        values. With -BeforeLineIndex the lookup uses the value in effect AT that line (the most recent
        preceding declaration); without it, the file's final effective values are used. Values are
        returned as already-resolved terminals, so a value Compose froze as a literal '${NAME}' stays
        literal instead of being expanded a second time.
    #>
    param(
        [Parameter(Mandatory)] $Evaluation,
        [hashtable]$Override = @{},
        [int]$BeforeLineIndex = -1
    )

    $declarations = $Evaluation.Declarations
    $effective = $Evaluation.Effective
    # Copy the caller's overrides into an ORDINAL dictionary. A PowerShell hashtable matches keys
    # case-insensitively, so a case-variant reference could otherwise pick up the topology alias
    # override that belongs to a different identifier.
    $overrides = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($key in $Override.Keys) { $overrides[[string]$key] = [string]$Override[$key] }
    $limit = $BeforeLineIndex

    return {
        param([string]$name)

        $ambient = [System.Environment]::GetEnvironmentVariable($name)
        if ($null -ne $ambient) { return $ambient }
        if ($overrides.ContainsKey($name)) { return [string]$overrides[$name] }

        if ($limit -ge 0) {
            $frozen = $null
            $found = $false
            foreach ($declaration in $declarations) {
                if ([string]::Equals($declaration.Key, $name, [System.StringComparison]::Ordinal) -and
                    $declaration.LineIndex -lt $limit) {
                    $frozen = $declaration.ResolvedValue
                    $found = $true
                }
            }
            if ($found) { return [string]$frozen }
            return $null
        }

        if ($effective.ContainsKey($name)) { return [string]$effective[$name] }
        return $null
    }.GetNewClosure()
}

function Get-DotenvDependencyClosure {
    <#
    .SYNOPSIS
        The set of keys a group of root keys transitively depends on, including the roots themselves.

    .DESCRIPTION
        Walks each key's last declaration's evaluated-reference trace. Because the traces are recorded
        at resolution time, an escaped '$$' literal and an operator word that did not fire contribute
        nothing - so the closure is the set of keys that genuinely affect the roots' values in this
        environment.

        Traversal STOPS at any name the ambient environment supplied. Compose used the ambient value,
        so that name's file declarations never contributed and neither do the names they reference;
        descending into them would attribute file-authored problems to a value the file did not
        provide.
    #>
    param(
        [Parameter(Mandatory)] $Evaluation,
        [Parameter(Mandatory)] [string[]]$RootKey
    )

    $closure = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Queue[string]]::new()
    foreach ($key in $RootKey) { $pending.Enqueue($key) }

    while ($pending.Count -gt 0) {
        $key = $pending.Dequeue()
        # List<string>.Contains is ordinal, matching the case sensitivity of a dotenv identifier.
        if ($closure.Contains($key)) { continue }
        $closure.Add($key)

        if ($null -ne [System.Environment]::GetEnvironmentVariable($key)) { continue }

        $declaration = Get-DotenvLastDeclaration -Evaluation $Evaluation -Name $key
        if ($null -eq $declaration) { continue }
        foreach ($referencedName in $declaration.References) {
            if (-not $closure.Contains($referencedName)) { $pending.Enqueue($referencedName) }
        }
    }

    return @($closure)
}

function Resolve-CmsDatabaseTopologyEnvironmentFile {
    <#
    .SYNOPSIS
        Returns the effective environment file path after applying the CMS database topology
        contract (DMS-1270): shared mode leaves the CMS database aliased to the selected DMS
        datastore name; separate mode redirects it to the dedicated edfi_configurationservice
        database.

    .DESCRIPTION
        Must be called after Resolve-DatabaseEngineEnvironmentFile has already composed any engine
        overlay for the same -DatabaseEngine, so this function's own writes are never replaced by
        that overlay's merge: a key the overlay does not define passes through unaffected
        regardless of when it was written, but DMS_CONFIG_DATABASE_CONNECTION_STRING is a key the
        overlay does define for mssql, so writing the migrated value only after the overlay has
        already run is what keeps it from being silently overwritten.

        Writes (or leaves alone) two values, unconditionally recomputed every call from the current
        -SeparateConfigDatabase switch value - never "write once if absent" - so a
        shared -> separate -> shared transition across successive invocations against the same base
        file correctly reverts on the next shared-mode call rather than leaving a stale override:

          - DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE: an internal bookkeeping marker, never consumed
            by any Compose YAML. Always read back via a raw (non-Compose-precedence) lookup so it
            cannot itself be affected by an ambient override of the same name.
          - DMS_CONFIG_DATABASE_NAME: the Compose-facing seam. Shared mode resolves it through the
            same Compose-precedence resolver (Get-ComposeResolvedEnvValue) the validator uses, so an
            ambient POSTGRES_DB_NAME / MSSQL_DB_NAME override - which genuinely moves the running
            database container - is reflected here too, not just at validation time; separate mode
            sets the fixed literal "edfi_configurationservice".

        After writing, DMS_CONFIG_DATABASE_NAME's line is guaranteed to precede
        DMS_CONFIG_DATABASE_CONNECTION_STRING's line in the derived file (Move-EnvFileKeyBeforeAnotherKey),
        because Docker Compose's --env-file interpolation is order-dependent: a ${VAR} reference
        resolves only against variables defined earlier in the same file, so a migrated connection
        string referencing ${DMS_CONFIG_DATABASE_NAME} would otherwise resolve to empty whenever that
        key is newly introduced into a base file that already defines the connection string - exactly
        today's checked-in templates' shape, confirmed against a real `docker compose config` render.

        Both values are serialized dotenv-safely (ConvertTo-DotenvSafeEnvValue): quoted only when the
        concrete value actually needs it (a space, '#', a '$', or a leading quote character), matching
        the approved design - never single-quoted for reasons that would suppress interpolation, since
        neither value is itself a ${VAR} reference this design writes.

        When switching to separate mode, if the connection string's database-segment value specifically
        - the value of a recognized database-name key (Database / Initial Catalog), not any other part
        of the string - is, in its raw (unresolved) form, exactly the legacy template token
        ("${POSTGRES_DB_NAME}" or "${MSSQL_DB_NAME}", matching this story's own prior default template,
        before DMS_CONFIG_DATABASE_NAME existed), that exact segment is rewritten to
        "${DMS_CONFIG_DATABASE_NAME}" so a pre-existing developer .env file reaches separate mode
        without hand-editing. Matching is anchored to the database-segment's key=value boundary via a
        quote-aware scanner (Find-ConnectionStringLegacyTokenSpan), not a blind substring or regex
        search across the whole string, so the same literal text appearing inside an unrelated quoted
        segment - a password containing a literal ';' - is never mistaken for a real segment boundary
        and never touched. A dotenv-level outer quote wrapper around the whole connection-string value
        (e.g. "host=...;database=${TOKEN};" as one double-quoted dotenv value) is detected and stripped
        before the scanner runs, then the found span is mapped back and spliced into the original,
        still-wrapped string, so the outer wrapper is preserved exactly while only the inner token
        changes. The rewrite is also never
        a match on what the reference currently resolves to, which could coincide with a value a
        genuinely different, custom reference also happens to produce. Any connection string not
        carrying that exact token in that exact position is left completely untouched, so it either
        already agrees with the separate-mode target or fails validation clearly, per the
        caller-authored-string contract.

        The rewritten connection string itself is never wrapped in single quotes: a single-quoted
        value is a Docker Compose literal with no ${VAR} interpolation at all (see
        database-safety.psm1's Resolve-ComposeEnvRawValue), which would freeze the very reference this
        function exists to introduce or preserve.

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file. Should already be the output of
        Resolve-DatabaseEngineEnvironmentFile for the same -DatabaseEngine.

    .PARAMETER DatabaseEngine
        "postgresql" or "mssql". Selects which datastore-name variable (POSTGRES_DB_NAME /
        MSSQL_DB_NAME) shared mode aliases to, and which legacy template token the migration looks
        for.

    .PARAMETER SeparateConfigDatabase
        Selects separate mode. Omit for shared mode.

    .PARAMETER DockerComposeRoot
        Directory holding the .derived output. Defaults to this module's directory.

    .OUTPUTS
        [string] The effective environment file path: BaseEnvironmentFile unchanged when nothing
        needs to change, or a new derived file path otherwise.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bootstrap helper, no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [ValidateSet("postgresql", "mssql")] [string]$DatabaseEngine,
        [switch]$SeparateConfigDatabase,
        [string]$DockerComposeRoot
    )

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    # Sequential evaluation is the authority for every value decision below: it is what Docker Compose
    # actually does with an --env-file, honoring declaration order, duplicates, and ambient precedence.
    # This function deliberately no longer reads through ReadValuesFromEnvFile at all - that parser
    # collapses duplicates to the last value and stores an `export `-prefixed key under the wrong name,
    # so a value it reported could differ from the one Compose renders.
    $sequential = Resolve-DotenvFileSequentially -Path $BaseEnvironmentFile
    $datastoreNameKey = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    $legacyToken = '${' + $datastoreNameKey + '}'

    $intendedMarker = if ($SeparateConfigDatabase) { "true" } else { "false" }

    # Ambient-aware: an ambient POSTGRES_DB_NAME / MSSQL_DB_NAME override genuinely moves the running
    # database container, so the CMS seam this function materializes must follow it too - matching
    # Confirm-CmsDatabaseTopologyAgreement's own resolution of the same key. Taken from the sequential
    # evaluation so a datastore name Compose freezes differently is not silently accepted.
    $intendedDatabaseName =
        if ($SeparateConfigDatabase) {
            "edfi_configurationservice"
        }
        else {
            [string](Get-SequentialEffectiveValue -Evaluation $sequential -Name $datastoreNameKey)
        }
    if ([string]::IsNullOrWhiteSpace($intendedDatabaseName)) {
        throw "Resolve-CmsDatabaseTopologyEnvironmentFile: could not resolve a non-blank database name for '$datastoreNameKey'."
    }

    # The RAW connection string comes from the shared assignment model, not the legacy parser, so a
    # supported spelling the evaluator understands is also visible to the legacy-token migration below.
    # Sourcing it from ReadValuesFromEnvFile made `export DMS_CONFIG_DATABASE_CONNECTION_STRING=...`
    # invisible here (its key was stored as "export DMS_CONFIG_..."), so separate mode skipped the
    # migration and failed later instead of taking the supported repair path. RawValue is the verbatim
    # text after '=', preserving the authored quoting and any trailing comment span.
    $currentConnectionStringDeclaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
    $currentConnectionString = if ($null -eq $currentConnectionStringDeclaration) { "" } else { [string]$currentConnectionStringDeclaration.RawValue }
    $intendedConnectionString = $currentConnectionString
    if ($SeparateConfigDatabase) {
        # Get-EnvValue returns the raw dotenv value verbatim, including any outer dotenv-level quote
        # wrapper (e.g. "host=...;database=${TOKEN};" as a whole) AND any trailing inline comment after
        # it. That outer quote character must be stripped before the connection-string-level scanner
        # runs, or its leading quote is mistaken for an ADO.NET value-quote, swallowing every real ';'
        # inside as "quoted" and finding no segments at all (Round 10 Blocker 1). A connection-string
        # value never legitimately starts with a quote character itself - an ADO.NET key always starts
        # with an identifier - so a leading quote unambiguously signals a dotenv-level wrapper, not
        # connection-string content. Get-DotenvClosingQuoteIndex locates the true closing quote the
        # same escape/comment-aware way ConvertFrom-ComposeEnvironmentValue does, so a valid trailing
        # " # comment" after the closing quote (Round 11 Blocker 2) does not defeat detection. The
        # wrapper (and any trailing comment) is preserved exactly: only the inner span is scanned, and
        # the replacement is spliced back into the original, still-wrapped string.
        # The raw value is the verbatim text after '=', so with a valid `KEY = "..."` (whitespace
        # around '=' is accepted by Compose) it BEGINS WITH WHITESPACE and the wrapper quote is not at
        # index 0. Get-DotenvClosingQuoteIndex requires the quote first, so the scan runs on the
        # leading-whitespace-trimmed span while the trimmed length is carried as a source offset. Every
        # splice below still targets the ORIGINAL raw text, so the leading whitespace, the quote
        # wrapper, and any trailing comment all survive byte for byte. Detecting the quote only at raw
        # index zero let `export KEY = "host=...;database=${TOKEN};"` through unwrapped, and the
        # connection-string scanner then mistook the wrapper quote for an ADO.NET value quote and found
        # no segments to migrate.
        $leadingWhitespaceLength = $currentConnectionString.Length - $currentConnectionString.TrimStart().Length
        $unpaddedConnectionString = $currentConnectionString.Substring($leadingWhitespaceLength)

        $closingQuoteIndex = Get-DotenvClosingQuoteIndex -RawValue $unpaddedConnectionString
        $dotenvWrapped = $closingQuoteIndex -ge 0
        $searchText = if ($dotenvWrapped) { $unpaddedConnectionString.Substring(1, $closingQuoteIndex - 1) } else { $unpaddedConnectionString }
        $searchOffset = $leadingWhitespaceLength + $(if ($dotenvWrapped) { 1 } else { 0 })

        # Quote-aware: a plain substring/regex search would mistake a ';' or '=' inside an unrelated
        # quoted segment (a password) for a real segment boundary. See Find-ConnectionStringLegacyTokenSpan.
        $legacyTokenSpan = Find-ConnectionStringLegacyTokenSpan -ConnectionString $searchText -LegacyToken $legacyToken
        if ($null -ne $legacyTokenSpan) {
            $absoluteStart = $legacyTokenSpan.Start + $searchOffset
            $intendedConnectionString =
                $currentConnectionString.Substring(0, $absoluteStart) +
                '${DMS_CONFIG_DATABASE_NAME}' +
                $currentConnectionString.Substring($absoluteStart + $legacyTokenSpan.Length)
        }
    }

    # Marker read through the shared assignment model too, for the same reason as the connection
    # string. Read raw from the file's own declaration (never ambient) - the marker is this design's
    # internal topology record, and an unrelated shell variable of the same name must not change which
    # mode a run believes it is in.
    $currentMarkerDeclaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
    $currentMarker =
        if ($null -eq $currentMarkerDeclaration) { "false" }
        else { [string](ConvertFrom-ComposeEnvironmentValue -Value $currentMarkerDeclaration.RawValue) }
    if ([string]::IsNullOrEmpty($currentMarker)) { $currentMarker = "false" }

    # The current DMS_CONFIG_DATABASE_NAME comes from the SEQUENTIAL evaluation, not a hashtable
    # lookup, so an already-correct alias-shaped file is recognized as needing no update while an
    # ambient datastore-name override is active, AND a file whose alias Compose actually freezes to a
    # different value is recognized as needing one.
    $currentDatabaseName = [string](Get-SequentialEffectiveValue -Evaluation $sequential -Name "DMS_CONFIG_DATABASE_NAME")

    $markerChanged = -not [string]::Equals($currentMarker, $intendedMarker, [System.StringComparison]::Ordinal)
    $nameChanged = -not [string]::Equals($currentDatabaseName, $intendedDatabaseName, [System.StringComparison]::Ordinal)
    $connectionStringChanged = -not [string]::Equals($currentConnectionString, $intendedConnectionString, [System.StringComparison]::Ordinal)

    # Docker Compose resolves an --env-file sequentially: a reference sees the ambient value if set,
    # else the most recent PRECEDING declaration, else nothing. A hashtable-based check cannot express
    # that, so it can approve a file Compose renders differently. Classification below works from the
    # sequential evaluation's per-declaration reference traces, which report the names each value
    # genuinely depended on at resolution time - escapes and unfired operator words excluded.
    #
    # Three input classes, with the required outcome for each:
    #   duplicate declaration of a seam-relevant key -> reject (reordering one occurrence would change
    #       the frozen value of every line between the two declarations)
    #   transitive forward dependency                -> reject with the chain (a multi-line reorder
    #       would change other lines' frozen values)
    #   simple forward reference                     -> repair, then prove the repaired file
    # Both rejections happen before anything is written.
    $seamKeys = @('DMS_CONFIG_DATABASE_CONNECTION_STRING', 'DMS_CONFIG_DATABASE_NAME', $datastoreNameKey)
    $closure = Get-DotenvDependencyClosure -Evaluation $sequential -RootKey $seamKeys

    foreach ($closureKey in $closure) {
        # An ambient value wins over every declaration of the same name, so file duplicates of an
        # ambient-overridden key are inert and must not fail the run.
        if ($null -ne [System.Environment]::GetEnvironmentVariable($closureKey)) { continue }

        # Ordinal membership: PowerShell's -contains is case-insensitive, which would conflate two
        # genuinely distinct dotenv identifiers.
        $isDuplicate = @($sequential.DuplicateKeys | Where-Object { [string]::Equals($_, $closureKey, [System.StringComparison]::Ordinal) }).Count -gt 0
        if ($isDuplicate) {
            $lineNumbers = @($sequential.Declarations |
                Where-Object { [string]::Equals($_.Key, $closureKey, [System.StringComparison]::Ordinal) } |
                ForEach-Object { $_.LineIndex + 1 })
            throw "CMS database topology: '$closureKey' is declared more than once in '$BaseEnvironmentFile' (lines $($lineNumbers -join ', ')), and the CMS database seam depends on it. Docker Compose resolves an --env-file sequentially, so lines between the declarations see the earlier value while the compose file itself sees the last one - the two can disagree. Remove the duplicate declaration and keep a single definition."
        }
    }

    $aliasDeclaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name 'DMS_CONFIG_DATABASE_NAME'
    $connectionDeclaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name 'DMS_CONFIG_DATABASE_CONNECTION_STRING'
    # Ordinal: a case-variant of a seam key is a different identifier and must not be treated as a
    # seam root (which would exempt it from the transitive check below).
    $seamRootLineIndexes = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
    if ($null -ne $aliasDeclaration) { $seamRootLineIndexes['DMS_CONFIG_DATABASE_NAME'] = $aliasDeclaration.LineIndex }
    if ($null -ne $connectionDeclaration) { $seamRootLineIndexes['DMS_CONFIG_DATABASE_CONNECTION_STRING'] = $connectionDeclaration.LineIndex }

    # A forward reference inside a DEPENDENCY of the seam (rather than inside a seam key itself) is a
    # transitive chain: the dependency froze to the wrong value before the seam line was even reached.
    foreach ($closureKey in $closure) {
        if ($seamRootLineIndexes.ContainsKey($closureKey)) { continue }
        # Ambient supplied this value, so Compose never evaluated the file's declaration of it. A
        # forward reference inside that unused declaration is not a defect in what the run will render.
        if ($null -ne [System.Environment]::GetEnvironmentVariable($closureKey)) { continue }
        $declaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name $closureKey
        if ($null -eq $declaration) { continue }
        foreach ($referencedName in $declaration.References) {
            if (Test-DotenvReferenceResolvable -Evaluation $sequential -Name $referencedName -BeforeLineIndex $declaration.LineIndex) { continue }
            throw "CMS database topology: DMS_CONFIG_DATABASE_CONNECTION_STRING depends on '$closureKey', whose own value references '$referencedName' before it is declared in '$BaseEnvironmentFile' (dependency chain: DMS_CONFIG_DATABASE_CONNECTION_STRING -> $closureKey -> $referencedName). Docker Compose resolves an --env-file sequentially, so '$closureKey' freezes with an empty '$referencedName' and the connection string inherits that. Declare '$referencedName' above '$closureKey'."
        }
    }

    # Simple forward references: a seam key referencing a name that is declared only later. These are
    # repairable - the alias is serialized as a resolved literal, and each other referenced key is
    # moved above the connection string.
    $declarationOrderBroken = $false
    $connectionStringForwardReferencedKeys = [System.Collections.Generic.List[string]]::new()

    if ($null -ne $aliasDeclaration) {
        foreach ($referencedName in $aliasDeclaration.References) {
            if (-not (Test-DotenvReferenceResolvable -Evaluation $sequential -Name $referencedName -BeforeLineIndex $aliasDeclaration.LineIndex)) {
                $declarationOrderBroken = $true
                break
            }
        }
    }

    if ($null -ne $connectionDeclaration) {
        # The references are taken from the INTENDED connection string, because in separate mode the
        # legacy token has already been rewritten to the alias and it is the intended value that will
        # be written and rendered.
        $intendedReferences = [System.Collections.Generic.List[string]]::new()
        $null = Resolve-ComposeEnvRawValue `
            -EnvironmentValues @{} `
            -RawValue $intendedConnectionString `
            -NameLookup (Get-DotenvSequentialLookup -Evaluation $sequential -BeforeLineIndex $connectionDeclaration.LineIndex) `
            -ReferenceTrace $intendedReferences

        foreach ($referencedName in $intendedReferences) {
            # Ordinal: only the exact alias identifier is healed structurally. A case-variant is a
            # different variable and must go through the ordinary forward-reference handling below.
            if ([string]::Equals($referencedName, 'DMS_CONFIG_DATABASE_NAME', [System.StringComparison]::Ordinal)) {
                # Healed structurally: the writer serializes the alias as a resolved literal and the
                # Move below places it ahead of the connection string.
                if ($null -ne $aliasDeclaration -and $connectionDeclaration.LineIndex -lt $aliasDeclaration.LineIndex) {
                    $declarationOrderBroken = $true
                }
                continue
            }
            if (Test-DotenvReferenceResolvable -Evaluation $sequential -Name $referencedName -BeforeLineIndex $connectionDeclaration.LineIndex) { continue }

            # Only a key that IS declared later can be moved. A name declared nowhere and absent from
            # the ambient environment is a genuinely undefined key, which the topology validator
            # reports on resolution rather than something to reorder.
            $referencedDeclaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name $referencedName
            if ($null -eq $referencedDeclaration) { continue }

            # Resolvable in place is not the same as safe to relocate. Moving this key above the
            # connection string also moves it above anything IT depends on, so a key with active
            # references of its own cannot be repaired by reordering even when its own dependencies are
            # correctly ordered where it currently sits. The references are the resolution-time trace,
            # so an escaped '$$' literal (a password like pa$$word) reports nothing and stays movable.
            if ($referencedDeclaration.References.Count -gt 0) {
                throw "CMS database topology: DMS_CONFIG_DATABASE_CONNECTION_STRING references '$referencedName', which is declared after it in '$BaseEnvironmentFile' and itself references $(($referencedDeclaration.References | ForEach-Object { "'$_'" }) -join ', '), so it cannot be moved above the connection string without breaking its own resolution order. Docker Compose resolves an --env-file sequentially; declare '$referencedName' (and the variables it references) above the connection string."
            }

            $declarationOrderBroken = $true
            if (-not $connectionStringForwardReferencedKeys.Contains($referencedName)) {
                $connectionStringForwardReferencedKeys.Add($referencedName)
            }
        }
    }

    if (-not $markerChanged -and -not $nameChanged -and -not $connectionStringChanged -and -not $declarationOrderBroken) {
        return $BaseEnvironmentFile
    }

    # The postcondition target, computed BEFORE writing: the complete connection string this run
    # intends Compose to render, with the topology-adjusted alias and ambient precedence applied. A
    # narrower check (host/database/port agreement) would pass a repaired file whose password segment
    # still renders empty, which is exactly the failure the silent no-op used to produce.
    # An ambient DMS_CONFIG_DATABASE_CONNECTION_STRING wins over the file entirely, and Compose hands
    # it to the container verbatim. The target must therefore be that ambient value, or a valid ambient
    # override would be reported as a repair failure against the file-authored string it replaced.
    $ambientConnectionString = [System.Environment]::GetEnvironmentVariable("DMS_CONFIG_DATABASE_CONNECTION_STRING")
    $intendedEffectiveConnectionString =
        if ($null -ne $ambientConnectionString) {
            $ambientConnectionString
        }
        elseif (-not [string]::IsNullOrEmpty($intendedConnectionString)) {
            Resolve-ComposeEnvRawValue `
                -EnvironmentValues @{} `
                -RawValue $intendedConnectionString `
                -NameLookup (Get-DotenvSequentialLookup `
                    -Evaluation $sequential `
                    -Override @{
                        DMS_CONFIG_DATABASE_NAME              = $intendedDatabaseName
                        DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = $intendedMarker
                    })
        }
        else { $null }

    # If $BaseEnvironmentFile is already one of this function's own prior derived outputs (a caller
    # re-deriving from a previous call's result instead of the original base file), target that same
    # path rather than compounding an ever-growing ".topology.topology..." suffix chain. Reading
    # $BaseEnvironmentFile fully before writing $derivedPath (inside Write-DerivedEnvFile) makes
    # writing back to the same path safe, mirroring the existing
    # "-BaseEnvironmentFile $composedPath -TargetPath $composedPath" pattern already used above.
    $baseFileName = [System.IO.Path]::GetFileName($BaseEnvironmentFile)
    $derivedName = if ($baseFileName.EndsWith(".topology")) { $baseFileName } else { "$baseFileName.topology" }
    $derivedPath = Join-Path (Join-Path $DockerComposeRoot ".derived") $derivedName

    # Dotenv-safe serialization for the two concrete values this function writes itself - never for
    # the connection string, whose existing quoting (or lack of it) is always preserved exactly as
    # authored.
    $keyOverrides = @{
        DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = ConvertTo-DotenvSafeEnvValue -Value $intendedMarker
        DMS_CONFIG_DATABASE_NAME               = ConvertTo-DotenvSafeEnvValue -Value $intendedDatabaseName
    }
    if ($connectionStringChanged) {
        $keyOverrides["DMS_CONFIG_DATABASE_CONNECTION_STRING"] = $intendedConnectionString
    }

    Write-DerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -TargetPath $derivedPath `
        -KeyOverrides $keyOverrides

    # Docker Compose's --env-file interpolation is order-dependent: DMS_CONFIG_DATABASE_NAME must be
    # defined before any line that references it via ${DMS_CONFIG_DATABASE_NAME}, or that reference
    # resolves to empty at real Compose render time. See Move-EnvFileKeyBeforeAnotherKey.
    Move-EnvFileKeyBeforeAnotherKey -Path $derivedPath -KeyToMove "DMS_CONFIG_DATABASE_NAME" -BeforeKey "DMS_CONFIG_DATABASE_CONNECTION_STRING"

    # Heal the connection string's OTHER forward references the same way: each referenced key found
    # declared below the connection string is moved ahead of it in the derived file. A key whose own
    # value has unresolved dependencies was already rejected above as a transitive chain, so every key
    # reaching this point is safe to relocate.
    foreach ($referencedKey in $connectionStringForwardReferencedKeys) {
        Move-EnvFileKeyBeforeAnotherKey -Path $derivedPath -KeyToMove $referencedKey -BeforeKey "DMS_CONFIG_DATABASE_CONNECTION_STRING"
    }

    # Postcondition: re-evaluate the file that was actually written, the same sequential way Compose
    # will, and require the COMPLETE connection string to match the target computed before writing.
    # Without this, a repair that failed to move a line still returned a path, and the caller handed
    # Compose a file whose credential or database segment rendered empty.
    if ($null -ne $intendedEffectiveConnectionString) {
        $derivedEvaluation = Resolve-DotenvFileSequentially -Path $derivedPath
        $derivedEffectiveConnectionString = [string](Get-SequentialEffectiveValue -Evaluation $derivedEvaluation -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING")
        if (-not [string]::Equals($derivedEffectiveConnectionString, $intendedEffectiveConnectionString, [System.StringComparison]::Ordinal)) {
            # NEVER render either connection string: both carry a database password, and this message
            # reaches terminals and CI logs. Name only the segments that disagree, which is what makes
            # the failure actionable anyway.
            $differingSegments = Get-ConnectionStringSegmentDifference `
                -Expected $intendedEffectiveConnectionString `
                -Actual $derivedEffectiveConnectionString
            throw "CMS database topology: the derived environment file '$derivedPath' does not render the intended CMS connection string. Segment(s) that disagree: $differingSegments. Values are withheld because the connection string contains credentials. This is a repair failure, not a configuration error - please report it with the source environment file."
        }
    }

    return $derivedPath
}

function Confirm-CmsDatabaseTopologyAgreement {
    <#
    .SYNOPSIS
        Throws unless the CMS database connection string agrees with the effective CMS database
        topology contract (DMS-1270): the database name, host, and port that
        DMS_CONFIG_DATABASE_CONNECTION_STRING actually resolves to - as Docker Compose would
        resolve it, honoring an ambient shell override - must match the seam this invocation
        established.

    .DESCRIPTION
        The expected database name is always computed independently: the fixed literal
        "edfi_configurationservice" in separate mode, or the Compose-precedence-resolved
        POSTGRES_DB_NAME / MSSQL_DB_NAME in shared mode - never by reading DMS_CONFIG_DATABASE_NAME
        back, because that value is itself exposed to the same ambient-override risk this check
        exists to catch. Mode is read from the internal topology marker via a raw (non-Compose-
        precedence) lookup, so an unrelated ambient environment variable of the same name as the
        marker cannot influence which branch applies.

        The actual connection string is resolved with full Docker Compose precedence (ambient wins
        over the file; ${VAR} references followed). For PostgreSQL, an entirely absent connection
        string (both ambient and file) falls back to a concrete default constructed by
        Get-CmsDatabaseTopologyDefaultConnectionString - never a template string, because
        Get-ComposeResolvedEnvValue returns its own DefaultValue argument verbatim without expanding
        any ${...} inside it. Both .yml fallbacks honor the topology seam in their database segment
        (${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}}), so that default is correct for shared and
        separate mode alike. For MSSQL there is no default to construct at all: the fallbacks' host,
        port, and username are PostgreSQL-shaped and Compose interpolation cannot branch on the
        engine, so a guessed SQL Server default would accept a connection Compose itself would never
        render. That case cannot arise in practice (the .env.mssql overlay always supplies the key),
        and this function fails clearly if it ever does.

        Every recognized database-name key present (Database, Initial Catalog) must individually
        agree with the expected name - the same all-candidates-must-agree pattern
        Assert-MssqlCmsDatabaseIsShared already uses, not a "pick one" or "reject if more than one
        is present" rule; a connection string carrying two agreeing aliases is accepted. The same
        rule applies to every recognized host-key alias present, and host-key recognition is
        engine-specific (Get-EndpointFromResolvedConnectionString): an MSSQL-only alias is not
        recognized for a PostgreSQL validation and vice versa, so a connection string authored for
        the wrong engine cannot pass by accident. A connection string presenting no recognized host
        key for the given engine at all fails closed. A present host key with no explicit port
        (neither a host,port compound nor, for PostgreSQL, a standalone "port" key) defaults to the
        engine's documented internal port (1433 MSSQL, 5432 PostgreSQL).

        Finally, if the ambient process/shell environment sets DMS_CONFIG_DATABASE_NAME directly
        (read raw, not through Get-ComposeResolvedEnvValue, which would trivially equal itself), it
        must also agree with the expected name - a caller-set ambient override that disagrees with
        the intended topology is rejected rather than silently allowed to reach Compose.

    .PARAMETER EnvironmentFile
        Absolute path to the effective environment file (after Resolve-DatabaseEngineEnvironmentFile
        and Resolve-CmsDatabaseTopologyEnvironmentFile have both already run).

    .PARAMETER DatabaseEngine
        "postgresql" or "mssql".
    #>
    param(
        [Parameter(Mandatory)] [string]$EnvironmentFile,
        [Parameter(Mandatory)] [ValidateSet("postgresql", "mssql")] [string]$DatabaseEngine
    )

    # Sequential evaluation, because that is what Docker Compose does with an --env-file. Resolving
    # against a complete hashtable instead let this validator pass a file where the datastore database
    # and the CMS database genuinely disagreed: with the datastore name declared twice, the hashtable
    # saw only the last value while Compose froze the first one into the connection string. Like the
    # resolver, this function no longer reads through ReadValuesFromEnvFile at all.
    $sequential = Resolve-DotenvFileSequentially -Path $EnvironmentFile
    $datastoreNameKey = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    # Deliberately a single name, and NOT the accepted-host set computed further down: this one
    # reconstructs the connection string Compose's own inline fallback renders when the file declares
    # none, so it must be the exact host that fallback names. A test pins it to a real Compose render.
    $inlineFallbackHost = if ($DatabaseEngine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }
    $expectedPort = if ($DatabaseEngine -eq "mssql") { "1433" } else { "5432" }
    $nameComparison = if ($DatabaseEngine -eq "mssql") { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }

    # The marker comes from its own raw declaration through the shared assignment model, then through
    # Compose's value semantics. Read raw and never from the ambient environment: the marker is this
    # design's internal topology record, so an unrelated shell variable of the same name must not change
    # which mode this validator believes it is checking.
    #
    # Sourcing it from ReadValuesFromEnvFile made this the last seam dependency on the legacy parser,
    # and the two disagreed: for `export DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"` the resolver
    # recognized separate mode and could early-return the file unchanged, while this validator saw
    # neither the key (stored under an `export `-prefixed name) nor the value (quotes left it as
    # "true"), read shared mode, and rejected a correct separate-mode file.
    #
    # The comparison is ordinal: the marker is written by this design as exactly "true" or "false", so a
    # hand-edited case-variant is not a topology declaration and must not silently redirect CMS.
    $markerDeclaration = Get-DotenvLastDeclaration -Evaluation $sequential -Name "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
    $markerValue =
        if ($null -eq $markerDeclaration) { "false" }
        else { [string](ConvertFrom-ComposeEnvironmentValue -Value $markerDeclaration.RawValue) }
    $isSeparate = [string]::Equals($markerValue, "true", [System.StringComparison]::Ordinal)

    $expectedDatabaseName =
        if ($isSeparate) {
            "edfi_configurationservice"
        }
        else {
            [string](Get-SequentialEffectiveValue -Evaluation $sequential -Name $datastoreNameKey)
        }
    if ([string]::IsNullOrWhiteSpace($expectedDatabaseName)) {
        throw "Confirm-CmsDatabaseTopologyAgreement: could not resolve a non-blank expected database name for '$datastoreNameKey'."
    }

    if ($isSeparate) {
        # Separate mode's whole promise is two physically distinct databases. A datastore name that
        # itself resolves to the dedicated CMS name would pass every equality check below while both
        # services silently share one database, so distinctness is proven explicitly. Resolved with
        # the same Compose precedence as everything else here, so an ambient datastore-name override
        # that collides is caught too.
        $datastoreDatabaseName = [string](Get-SequentialEffectiveValue -Evaluation $sequential -Name $datastoreNameKey)
        if (-not [string]::IsNullOrWhiteSpace($datastoreDatabaseName) -and
            [string]::Equals($datastoreDatabaseName, $expectedDatabaseName, $nameComparison)) {
            throw "CMS database topology mismatch: -SeparateConfigDatabase requires the Configuration Service database and the DMS datastore to be physically distinct, but '$datastoreNameKey' also resolves to '$datastoreDatabaseName'. Rename the datastore database or use shared mode."
        }
    }

    $actualConnectionString =
        if ($DatabaseEngine -eq "mssql") {
            # Fail clearly for an entirely absent connection string rather than validate against a
            # value Compose would never render: both .yml fallbacks are PostgreSQL-shaped by
            # construction (Compose interpolation cannot branch on the engine), so there is no MSSQL
            # default to compare against - see the .DESCRIPTION note above.
            $explicitConnectionString = [string](Get-SequentialEffectiveValue -Evaluation $sequential -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING")
            if ([string]::IsNullOrWhiteSpace($explicitConnectionString)) {
                throw "Confirm-CmsDatabaseTopologyAgreement: DMS_CONFIG_DATABASE_CONNECTION_STRING is required for MSSQL and cannot be entirely absent. The Compose inline fallback is PostgreSQL-shaped, so no SQL Server default exists to validate against; the .env.mssql overlay normally supplies this key, so check for a corrupted or manually-edited environment file."
            }
            $explicitConnectionString
        }
        else {
            $postgresPassword = [string](Get-SequentialEffectiveValue -Evaluation $sequential -Name "POSTGRES_PASSWORD")
            $defaultConnectionString = Get-CmsDatabaseTopologyDefaultConnectionString `
                -ExpectedHost $inlineFallbackHost `
                -ExpectedPort $expectedPort `
                -ExpectedDatabaseName $expectedDatabaseName `
                -PostgresPassword $postgresPassword
            Get-SequentialEffectiveValue -Evaluation $sequential -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING" -DefaultValue $defaultConnectionString
        }

    $actualDatabaseNames = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString $actualConnectionString)
    if ($actualDatabaseNames.Count -eq 0) {
        throw "Confirm-CmsDatabaseTopologyAgreement: DMS_CONFIG_DATABASE_CONNECTION_STRING must include Database or Initial Catalog and target '$expectedDatabaseName'."
    }
    foreach ($actualDatabaseName in $actualDatabaseNames) {
        if (-not [string]::Equals($actualDatabaseName, $expectedDatabaseName, $nameComparison)) {
            throw "CMS database topology mismatch: DMS_CONFIG_DATABASE_CONNECTION_STRING targets database '$actualDatabaseName', but the effective topology contract requires '$expectedDatabaseName'. Align the connection string or the -SeparateConfigDatabase selection."
        }
    }

    $actualEndpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString $actualConnectionString -DatabaseEngine $DatabaseEngine)
    if ($actualEndpoints.Count -eq 0) {
        throw "Confirm-CmsDatabaseTopologyAgreement: DMS_CONFIG_DATABASE_CONNECTION_STRING must include a host key recognized for '$DatabaseEngine' so the CMS database endpoint can be verified."
    }
    # The database service is reachable under more than one name on the shared Compose network, and all of
    # them denote the SAME container: the service key under `services:` (deliberately `db` in both
    # postgresql.yml and mssql.yml so either file is a drop-in swap), plus that service's
    # `container_name:` and `hostname:`. Accepting only `dms-<engine>` rejected a connection string using
    # the service name, which is a legitimate way to address exactly this database.
    #
    # The set is derived from the compose file itself rather than listed here, so renaming the service,
    # container, or hostname cannot leave this validation silently out of step with the stack it validates.
    # Host-side names (localhost, 127.0.0.1) are deliberately NOT accepted: this connection string is the
    # container's, and accepting them would mask a genuinely wrong endpoint.
    $acceptedHosts = @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine $DatabaseEngine -DockerComposeRoot $PSScriptRoot)
    foreach ($actualEndpoint in $actualEndpoints) {
        $hostIsAccepted = @($acceptedHosts | Where-Object {
            [string]::Equals($actualEndpoint.Host, $_, [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        if (-not $hostIsAccepted) {
            throw "CMS database topology mismatch: DMS_CONFIG_DATABASE_CONNECTION_STRING targets host '$($actualEndpoint.Host)', but the effective topology contract requires the composed database service, addressable as $(($acceptedHosts | ForEach-Object { "'$_'" }) -join ' or ')."
        }
        $actualPort = if ([string]::IsNullOrWhiteSpace($actualEndpoint.Port)) { $expectedPort } else { $actualEndpoint.Port }
        if (-not [string]::Equals($actualPort, $expectedPort, [System.StringComparison]::Ordinal)) {
            throw "CMS database topology mismatch: DMS_CONFIG_DATABASE_CONNECTION_STRING targets port '$actualPort', but the effective topology contract requires '$expectedPort'."
        }
    }

    $ambientDatabaseName = [System.Environment]::GetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME")
    if (-not [string]::IsNullOrWhiteSpace($ambientDatabaseName) -and -not [string]::Equals($ambientDatabaseName, $expectedDatabaseName, $nameComparison)) {
        throw "CMS database topology mismatch: an ambient DMS_CONFIG_DATABASE_NAME='$ambientDatabaseName' conflicts with the effective topology contract, which requires '$expectedDatabaseName'. Unset it or align it before running."
    }
}
