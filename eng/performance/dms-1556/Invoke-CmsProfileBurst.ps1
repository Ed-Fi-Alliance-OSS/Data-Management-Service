# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 investigation harness: seeds CMS profiles and replays the DMS catalog-load burst.

.DESCRIPTION
Two modes:

  -Seed: idempotently creates -ProfileCount profiles named DMS1556-Profile-### against a
  running CMS and records each profile's id, definition, and definition SHA-256 in a
  manifest JSON used later for body validation.

  Burst (default): mints one client-credentials token (mirroring DMS, which reuses one
  cached token for the whole fan-out), then issues -TotalRequests GET /v3/profiles/{id}
  requests through a SemaphoreSlim(-MaxConcurrency) admission gate with Task.WhenAll.
  Request i targets ids[i mod seededCount], so ids repeat when TotalRequests exceeds the
  seeded count. Per Q18, a request's in-flight interval runs from admission through the
  semaphore to response-body completion; the summary reports the measured peak overlap
  (maximum simultaneous in-flight intervals) alongside the status histogram, latency
  percentiles, and time-to-first-failure. Output: one CSV per round and one summary JSON
  per run, under -OutputDirectory (gitignored).

Workload labels per Q18: 87/87 -> catalog-87x87, 256/128 -> stress-256x128, anything
else -> workload-TxN. Keep the two Q18 workloads separately identified in evidence.

.EXAMPLE
./Invoke-CmsProfileBurst.ps1 -Seed -ProfileCount 87

.EXAMPLE
./Invoke-CmsProfileBurst.ps1 -TotalRequests 87 -MaxConcurrency 87 -Rounds 5 -Cold -ValidateBodies
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read by the nested functions through script scope; the analyzer does not track that usage.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Local investigation harness: the client secret is read from a parameter defaulting to the .env.e2e development value and sent as an OAuth form field; SecureString adds no protection across that boundary.')]
[CmdletBinding(DefaultParameterSetName = 'Burst')]
param(
    # CMS base URL including the path base (DMS_CONFIG_PATH_BASE) when one is configured.
    # 127.0.0.1, not localhost: on this Windows host the dual-stack fallback adds ~2 s to
    # every new connection to a 127.0.0.1-bound port, which would contaminate latency
    # evidence with a client-side artifact (verified during the 0.1 smoke).
    [string] $BaseUrl = 'http://127.0.0.1:8081/config',

    # Client credentials for /connect/token. Defaults match eng/docker-compose/.env.e2e.
    [string] $ClientId = 'DmsConfigurationService',
    [string] $ClientSecret = 'ValidClientSecret1234567890!Abcd',
    [string] $Scope = 'edfi_admin_api/full_access',

    [Parameter(ParameterSetName = 'Seed', Mandatory)]
    [switch] $Seed,

    [Parameter(ParameterSetName = 'Seed')]
    [ValidateRange(1, 10000)]
    [int] $ProfileCount = 87,

    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(1, 100000)]
    [int] $TotalRequests = 87,

    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(1, 4096)]
    [int] $MaxConcurrency = 87,

    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(1, 100)]
    [int] $Rounds = 1,

    # Restart the CMS container before round 1 and wait for /health, so round 1 hits a
    # cold process. Later rounds in the same invocation are warm.
    [Parameter(ParameterSetName = 'Burst')]
    [switch] $Cold,

    # Validate each 200 body's id/name/definition against the seed manifest.
    [Parameter(ParameterSetName = 'Burst')]
    [switch] $ValidateBodies,

    [Parameter(ParameterSetName = 'Burst')]
    [string] $CmsContainerName = 'ed-fi-api-config-service',

    # Per-request HttpClient timeout. Generous by design: the harness must observe slow
    # responses and server-side timeouts (~15 s SASL window), not clip them client-side.
    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(5, 600)]
    [int] $RequestTimeoutSeconds = 120,

    # Overrides the Q18-derived workload label used in output file names and the summary.
    [Parameter(ParameterSetName = 'Burst')]
    [string] $RunLabel,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),

    [string] $ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ManifestPath) {
    $ManifestPath = Join-Path $OutputDirectory 'profile-manifest.json'
}
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory

$profileDefinitionTemplate = '<Profile name="{0}"><Resource name="School"></Resource></Profile>'

function Get-AccessToken {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $body = @{
        grant_type    = 'client_credentials'
        client_id     = $ClientId
        client_secret = $ClientSecret
        scope         = $Scope
    }
    $response = Invoke-RestMethod -Method Post -Uri "$BaseUrl/connect/token" -Body $body -ContentType 'application/x-www-form-urlencoded'
    if (-not $response.access_token) {
        throw "Token endpoint returned no access_token."
    }
    return [string]$response.access_token
}

function Get-AuthorizationHeader {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)][string] $Token)

    return @{ Authorization = "Bearer $Token" }
}

function Get-DefinitionHash {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string] $Definition)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Definition)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($hash).ToLowerInvariant()
}

function Find-ProfileIdByName {
    # Resolves an existing profile's id by paging GET /v3/profiles. Used only when a POST
    # reports a duplicate name that the manifest does not know about.
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][hashtable] $Headers,
        [Parameter(Mandatory)][string] $Name
    )

    $offset = 0
    $limit = 25
    while ($true) {
        $page = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v3/profiles?offset=$offset&limit=$limit" -Headers $Headers
        $items = @($page)
        foreach ($item in $items) {
            if ($item.name -eq $Name) {
                return [int]$item.id
            }
        }
        if ($items.Count -lt $limit) {
            return $null
        }
        $offset += $limit
    }
}

function Invoke-ProfileSeeding {
    [CmdletBinding()]
    param()

    Write-Output "Seeding $ProfileCount profiles against $BaseUrl"
    $token = Get-AccessToken
    $headers = Get-AuthorizationHeader -Token $token

    $existing = @{}
    if (Test-Path -LiteralPath $ManifestPath) {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        foreach ($entry in $manifest.profiles) {
            $existing[[string]$entry.name] = $entry
        }
        Write-Output "Loaded existing manifest with $($existing.Count) entries."
    }

    $profiles = [System.Collections.Generic.List[object]]::new()
    $created = 0
    $reused = 0
    for ($i = 1; $i -le $ProfileCount; $i++) {
        $name = 'DMS1556-Profile-{0:d3}' -f $i
        $definition = $profileDefinitionTemplate -f $name
        $id = $null

        if ($existing.ContainsKey($name)) {
            $candidateId = [int]$existing[$name].id
            try {
                $current = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v3/profiles/$candidateId" -Headers $headers
                if ($current.name -eq $name) {
                    $id = $candidateId
                    $reused++
                }
            }
            catch {
                # Fall through and re-create: the manifest entry is stale (e.g. volumes were reset).
                Write-Verbose "Manifest entry for '$name' (id $candidateId) is stale; re-creating."
            }
        }

        if ($null -eq $id) {
            $bodyJson = @{ name = $name; definition = $definition } | ConvertTo-Json -Compress
            try {
                $response = Invoke-WebRequest -Method Post -Uri "$BaseUrl/v3/profiles" -Headers $headers -Body $bodyJson -ContentType 'application/json'
                $location = [string]$response.Headers['Location']
                if ($location -notmatch '/v3/profiles/(\d+)\s*$') {
                    throw "Created profile '$name' but could not parse id from Location '$location'."
                }
                $id = [int]$Matches[1]
                $created++
            }
            catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                if ($_.Exception.Response.StatusCode.value__ -ne 409) {
                    throw
                }
                $id = Find-ProfileIdByName -Headers $headers -Name $name
                if ($null -eq $id) {
                    throw "Profile '$name' already exists but was not found by paging GET /v3/profiles."
                }
                $reused++
            }
        }

        $profiles.Add([pscustomobject]@{
                name           = $name
                id             = $id
                definition     = $definition
                definitionHash = Get-DefinitionHash -Definition $definition
            })
    }

    $manifestOut = [pscustomobject]@{
        baseUrl      = $BaseUrl
        seededAtUtc  = [DateTime]::UtcNow.ToString('o')
        profileCount = $ProfileCount
        profiles     = $profiles
    }
    $manifestOut | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
    Write-Output "Manifest written to $ManifestPath ($created created, $reused reused)."
}

$burstRunnerSource = @'
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Dms1556
{
    public sealed class RequestResult
    {
        public int Index;
        public int ProfileId;
        public DateTime AdmittedUtc;
        public DateTime BodyCompletedUtc;
        public double DurationMs;
        public int StatusCode; // 0 => transport error, see Error
        public string Body;
        public string Error;
    }

    public static class BurstRunner
    {
        // The client is created once per harness invocation and shared across rounds,
        // mirroring DMS's long-lived HttpClient: warm rounds reuse pooled connections
        // instead of paying connection setup again every round.
        public static HttpClient CreateClient(string token, int requestTimeoutSeconds)
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        // Mirrors the DMS catalog fan-out: one shared HttpClient, Task.WhenAll, and a
        // SemaphoreSlim admission gate. Timestamps are taken immediately after semaphore
        // admission and immediately after the response body is fully read (Q18).
        public static List<RequestResult> Run(
            HttpClient client,
            string baseUrl,
            int[] profileIds,
            int totalRequests,
            int maxConcurrency,
            bool captureBodies)
        {
            return RunAsync(client, baseUrl, profileIds, totalRequests, maxConcurrency, captureBodies)
                .GetAwaiter().GetResult();
        }

        private static async Task<List<RequestResult>> RunAsync(
            HttpClient client,
            string baseUrl,
            int[] profileIds,
            int totalRequests,
            int maxConcurrency,
            bool captureBodies)
        {
            var gate = new SemaphoreSlim(maxConcurrency);
            var results = new RequestResult[totalRequests];
            var tasks = new List<Task>(totalRequests);

            for (int i = 0; i < totalRequests; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    var result = new RequestResult
                    {
                        Index = index,
                        ProfileId = profileIds[index % profileIds.Length]
                    };
                    result.AdmittedUtc = DateTime.UtcNow;
                    try
                    {
                        using (var response = await client
                            .GetAsync(baseUrl + "/v3/profiles/" + result.ProfileId, HttpCompletionOption.ResponseHeadersRead)
                            .ConfigureAwait(false))
                        {
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            result.BodyCompletedUtc = DateTime.UtcNow;
                            result.StatusCode = (int)response.StatusCode;
                            // Non-200 bodies are always kept: they carry the correlation id.
                            result.Body = (captureBodies || result.StatusCode != 200) ? body : null;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.BodyCompletedUtc = DateTime.UtcNow;
                        result.StatusCode = 0;
                        var baseline = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                        string message = baseline.Message ?? string.Empty;
                        int newline = message.IndexOfAny(new[] { '\r', '\n' });
                        if (newline >= 0)
                        {
                            message = message.Substring(0, newline);
                        }
                        result.Error = baseline.GetType().Name + ": " + message;
                    }
                    finally
                    {
                        gate.Release();
                    }
                    result.DurationMs = (result.BodyCompletedUtc - result.AdmittedUtc).TotalMilliseconds;
                    results[index] = result;
                }));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new List<RequestResult>(results);
        }
    }
}
'@

function Add-BurstRunnerType {
    [CmdletBinding()]
    param()

    if (-not ('Dms1556.BurstRunner' -as [type])) {
        Add-Type -TypeDefinition $burstRunnerSource -ReferencedAssemblies @(
            'System.Collections'
            'System.Net.Http'
            'System.Net.Primitives'
            'System.Threading'
        )
    }
}

function Get-PeakOverlap {
    # Maximum number of simultaneously in-flight requests, where in-flight runs from
    # semaphore admission through response-body completion (Q18).
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]] $Results)

    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($r in $Results) {
        $events.Add([pscustomobject]@{ Ticks = $r.AdmittedUtc.Ticks; Delta = 1 })
        $events.Add([pscustomobject]@{ Ticks = $r.BodyCompletedUtc.Ticks; Delta = -1 })
    }
    # Completions sort before admissions at identical timestamps: the interval is half-open.
    $sorted = $events | Sort-Object -Property Ticks, Delta
    $running = 0
    $peak = 0
    foreach ($e in $sorted) {
        $running += $e.Delta
        if ($running -gt $peak) { $peak = $running }
    }
    return $peak
}

function Get-Percentile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][double[]] $SortedValues,
        [Parameter(Mandatory)][double] $Percentile
    )

    # Nearest-rank on an ascending-sorted array.
    $rank = [Math]::Ceiling($Percentile / 100.0 * $SortedValues.Count)
    $index = [Math]::Max(0, [Math]::Min($SortedValues.Count - 1, $rank - 1))
    return [Math]::Round($SortedValues[$index], 1)
}

function Get-TraceIdFromBody {
    [CmdletBinding()]
    [OutputType([string])]
    param([string] $Body)

    if ([string]::IsNullOrEmpty($Body)) { return '' }
    if ($Body -match '"correlationId"\s*:\s*"([^"]+)"') { return $Matches[1] }
    return ''
}

function Wait-CmsHealthy {
    [CmdletBinding()]
    param([int] $TimeoutSeconds = 180)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Method Get -Uri "$BaseUrl/health" -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
            Write-Verbose "CMS not healthy yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 2
    }
    throw "CMS did not answer 200 on $BaseUrl/health within $TimeoutSeconds seconds."
}

function Get-CmsEnvironmentRecord {
    # Best-effort capture of the thread-pool-relevant container environment. The
    # authoritative effective minimum comes from the dump (Q17), not from here.
    [CmdletBinding()]
    param()

    try {
        $envLines = docker inspect $CmsContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        $interesting = @($envLines | Where-Object { $_ -match '^(DOTNET_ThreadPool|DOTNET_PROCESSOR_COUNT|DOTNET_Diagnostic|ASPNETCORE_HTTP_PORTS)' })
        return [pscustomobject]@{
            containerName = $CmsContainerName
            environment   = $interesting
        }
    }
    catch {
        return $null
    }
}

function Invoke-Burst {
    [CmdletBinding()]
    param()

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Manifest not found at $ManifestPath. Run with -Seed first."
    }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $manifestProfiles = @($manifest.profiles)
    if ($manifestProfiles.Count -eq 0) {
        throw "Manifest at $ManifestPath contains no profiles."
    }
    $profileIds = [int[]]@($manifestProfiles | ForEach-Object { $_.id })
    $expectedByIndex = @{}
    for ($i = 0; $i -lt $manifestProfiles.Count; $i++) {
        $expectedByIndex[$i] = $manifestProfiles[$i]
    }

    if (-not $RunLabel) {
        $RunLabel = if ($TotalRequests -eq 87 -and $MaxConcurrency -eq 87) { 'catalog-87x87' }
        elseif ($TotalRequests -eq 256 -and $MaxConcurrency -eq 128) { 'stress-256x128' }
        else { "workload-${TotalRequests}x${MaxConcurrency}" }
    }
    $runId = '{0}-{1}' -f $RunLabel, ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    $runStartedUtc = [DateTime]::UtcNow.ToString('o')

    if ($Cold) {
        Write-Output "Cold run: restarting $CmsContainerName..."
        docker restart $CmsContainerName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "docker restart $CmsContainerName failed with exit code $LASTEXITCODE."
        }
        Wait-CmsHealthy
        Write-Output 'CMS healthy after restart.'
    }

    Add-BurstRunnerType
    $token = Get-AccessToken
    $client = [Dms1556.BurstRunner]::CreateClient($token, $RequestTimeoutSeconds)

    try {
    $roundSummaries = [System.Collections.Generic.List[object]]::new()
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Output "Round ${round}/${Rounds}: $TotalRequests requests, concurrency $MaxConcurrency..."
        $burstStartUtc = [DateTime]::UtcNow
        $results = [Dms1556.BurstRunner]::Run(
            $client, $BaseUrl, $profileIds, $TotalRequests, $MaxConcurrency, [bool]$ValidateBodies)

        $rows = [System.Collections.Generic.List[object]]::new()
        $bodyValidCount = 0
        $bodyInvalidCount = 0
        foreach ($r in $results) {
            $bodyValid = ''
            if ($ValidateBodies) {
                $expected = $expectedByIndex[$r.Index % $manifestProfiles.Count]
                $isValid = $false
                if ($r.StatusCode -eq 200 -and $r.Body) {
                    try {
                        $parsed = $r.Body | ConvertFrom-Json
                        $isValid = ([int]$parsed.id -eq [int]$expected.id) -and
                            ($parsed.name -eq $expected.name) -and
                            ($parsed.definition -eq $expected.definition)
                    }
                    catch {
                        $isValid = $false
                    }
                }
                $bodyValid = $isValid
                if ($isValid) { $bodyValidCount++ } else { $bodyInvalidCount++ }
            }
            $rows.Add([pscustomobject]@{
                    round            = $round
                    index            = $r.Index
                    profileId        = $r.ProfileId
                    admittedUtc      = $r.AdmittedUtc.ToString('o')
                    bodyCompletedUtc = $r.BodyCompletedUtc.ToString('o')
                    durationMs       = [Math]::Round($r.DurationMs, 1)
                    statusCode       = $r.StatusCode
                    bodyValid        = $bodyValid
                    traceId          = if ($r.StatusCode -ne 200) { Get-TraceIdFromBody -Body $r.Body } else { '' }
                    error            = if ($r.Error) { $r.Error } else { '' }
                })
        }

        $csvPath = Join-Path $OutputDirectory "$runId-round$round.csv"
        $rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8

        $statusHistogram = @{}
        foreach ($group in ($results | Group-Object -Property StatusCode)) {
            $statusHistogram[[string]$group.Name] = $group.Count
        }
        $durations = [double[]]@($results | ForEach-Object { $_.DurationMs } | Sort-Object)
        $failures = @($results | Where-Object { $_.StatusCode -ne 200 } | Sort-Object -Property AdmittedUtc)
        $timeToFirstFailureMs = if ($failures.Count -gt 0) {
            [Math]::Round(($failures[0].AdmittedUtc - $burstStartUtc).TotalMilliseconds, 1)
        }
        else { $null }

        $roundSummary = [pscustomobject]@{
            round                = $round
            requests             = $results.Count
            statusHistogram      = $statusHistogram
            p50Ms                = Get-Percentile -SortedValues $durations -Percentile 50
            p95Ms                = Get-Percentile -SortedValues $durations -Percentile 95
            p99Ms                = Get-Percentile -SortedValues $durations -Percentile 99
            maxMs                = [Math]::Round($durations[-1], 1)
            timeToFirstFailureMs = $timeToFirstFailureMs
            measuredPeakOverlap  = Get-PeakOverlap -Results $results
            bodyValidCount       = if ($ValidateBodies) { $bodyValidCount } else { $null }
            bodyInvalidCount     = if ($ValidateBodies) { $bodyInvalidCount } else { $null }
            csv                  = (Split-Path -Leaf $csvPath)
        }
        $roundSummaries.Add($roundSummary)
        Write-Output ("  statuses: {0}; p50/p95/p99 ms: {1}/{2}/{3}; peak overlap: {4}" -f `
            (($statusHistogram.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '),
            $roundSummary.p50Ms, $roundSummary.p95Ms, $roundSummary.p99Ms, $roundSummary.measuredPeakOverlap)
    }

    $summary = [pscustomobject]@{
        runId           = $runId
        workload        = $RunLabel
        baseUrl         = $BaseUrl
        totalRequests   = $TotalRequests
        maxConcurrency  = $MaxConcurrency
        rounds          = $Rounds
        cold            = [bool]$Cold
        validateBodies  = [bool]$ValidateBodies
        manifest        = $ManifestPath
        seededProfiles  = $manifestProfiles.Count
        startedUtc      = $runStartedUtc
        cmsEnvironment  = Get-CmsEnvironmentRecord
        roundSummaries  = $roundSummaries
    }
    $summaryPath = Join-Path $OutputDirectory "$runId-summary.json"
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Output "Summary written to $summaryPath"
    }
    finally {
        $client.Dispose()
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Seed') {
    Invoke-ProfileSeeding
}
else {
    Invoke-Burst
}
