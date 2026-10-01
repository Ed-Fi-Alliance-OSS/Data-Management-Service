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

    # Idle gap between consecutive rounds (none after the last). Rounds of a warm burst
    # last well under a second, while livemetrics counters are 1 s samples and pg_stat_io
    # flushes about once per second; the gap keeps each round's samples and events
    # separable. 0 (the default) keeps rounds back-to-back as in E1.
    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(0, 300)]
    [int] $InterRoundDelaySeconds = 0,

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

    # Coordinated evidence mode: after any -Cold restart, start the diagnostics samplers
    # against the (new) CMS process, wait until every sampler is producing data, run the
    # burst, then stop the samplers and FAIL the run unless the runtime captures cover
    # the entire burst window. Requires the local-config-diagnostics.yml sidecar.
    [Parameter(ParameterSetName = 'Burst')]
    [switch] $WithSamplers,

    # Sampler window; must exceed the expected total burst duration or the coverage
    # check fails (deliberately: incomplete evidence is reported, not masked).
    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(10, 3600)]
    [int] $SamplerDurationSeconds = 180,

    [Parameter(ParameterSetName = 'Burst')]
    [string] $MonitorBaseUrl = 'http://localhost:52323',

    # Managed-stack captures (dotnet-monitor /stacks) at these offsets, in seconds from
    # the start of each round listed in -StackCaptureRounds. Requires -WithSamplers (the
    # sampler set owns the resolved monitor pid). Captures run sequentially on a thread
    # job, so a slow capture delays the next one; the requested and completed times of
    # each capture are recorded in the round summary. The next round starts only after
    # the capture job finishes, so captures never overlap another round.
    [Parameter(ParameterSetName = 'Burst')]
    [double[]] $StackCaptureOffsetsSeconds = @(),

    [Parameter(ParameterSetName = 'Burst')]
    [int[]] $StackCaptureRounds = @(1),

    # Containers the docker-stats sampler watches; override for stacks whose names differ
    # (e.g. adding the DMS container for E7 runs).
    [Parameter(ParameterSetName = 'Burst')]
    [string[]] $SamplerStatsContainers = @('ed-fi-api-config-service', 'dms-postgresql'),

    # Injected dependency outage (step 4.1-O), run on a thread job beside the rounds:
    #   PausePostgres - `docker pause` of -FaultPgContainerName at -FaultOffsetSeconds into
    #                   round -FaultRound, `docker unpause` -FaultDurationSeconds later.
    #   LockKeyTable  - one session holds LOCK TABLE dmscs."OpenIddictKey" ACCESS EXCLUSIVE
    #                   for -FaultDurationSeconds (a key-store-only outage; token-status and
    #                   profile reads are unaffected). -FaultRound 0 takes the lock after
    #                   the token mint and BEFORE any -Cold restart, and the harness waits
    #                   until the lock is granted, so the restarted process starts without
    #                   a readable key store.
    # The fault's host-clock times are recorded in the summary. Cleanup is unconditional:
    # the finally block unpauses a paused container and terminates the lock session.
    [Parameter(ParameterSetName = 'Burst')]
    [ValidateSet('None', 'PausePostgres', 'LockKeyTable')]
    [string] $FaultKind = 'None',

    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(0, 100)]
    [int] $FaultRound = 1,

    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(0, 600)]
    [double] $FaultOffsetSeconds = 0,

    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(1, 600)]
    [int] $FaultDurationSeconds = 20,

    [Parameter(ParameterSetName = 'Burst')]
    [string] $FaultPgContainerName = 'dms-postgresql',

    [Parameter(ParameterSetName = 'Burst')]
    [string] $FaultDatabase = 'edfi_datamanagementservice',

    # When > 0, a thread job requests GET /.well-known/jwks.json every this many
    # milliseconds from before round 1 until after the last round, and writes
    # <runId>-jwks.csv (status, key count, Retry-After, content type, duration).
    [Parameter(ParameterSetName = 'Burst')]
    [ValidateRange(0, 60000)]
    [int] $JwksProbeIntervalMilliseconds = 0,

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
        // Recorded for non-200 responses only: they classify a dependency 503
        // (Retry-After, problem+json, no challenge) against an ordinary 401 challenge.
        public string RetryAfter;
        public string ContentType;
        public string WwwAuthenticate;
    }

    public static class BurstRunner
    {
        // The client is created once per harness invocation and shared across rounds,
        // mirroring DMS's long-lived HttpClient: warm rounds reuse pooled connections
        // instead of paying connection setup again every round. HttpClient.Timeout is
        // disabled: with HttpCompletionOption.ResponseHeadersRead it stops covering the
        // request once headers arrive, so the deadline is instead a per-request
        // CancellationTokenSource in RunAsync that spans send AND body read.
        public static HttpClient CreateClient(string token)
        {
            var client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
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
            bool captureBodies,
            int requestTimeoutSeconds)
        {
            return RunAsync(client, baseUrl, profileIds, totalRequests, maxConcurrency, captureBodies, requestTimeoutSeconds)
                .GetAwaiter().GetResult();
        }

        private static async Task<List<RequestResult>> RunAsync(
            HttpClient client,
            string baseUrl,
            int[] profileIds,
            int totalRequests,
            int maxConcurrency,
            bool captureBodies,
            int requestTimeoutSeconds)
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
                    // One deadline per request, started at admission, covering the send
                    // and the body read: HttpClient.Timeout would stop at the response
                    // headers with ResponseHeadersRead, letting a stalled body hold a
                    // semaphore slot indefinitely.
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeoutSeconds)))
                    {
                        try
                        {
                            using (var response = await client
                                .GetAsync(baseUrl + "/v3/profiles/" + result.ProfileId, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                                .ConfigureAwait(false))
                            {
                                string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                                result.BodyCompletedUtc = DateTime.UtcNow;
                                result.StatusCode = (int)response.StatusCode;
                                // Non-200 bodies are always kept: they carry the correlation id.
                                result.Body = (captureBodies || result.StatusCode != 200) ? body : null;
                                if (result.StatusCode != 200)
                                {
                                    result.RetryAfter = response.Headers.RetryAfter != null ? response.Headers.RetryAfter.ToString() : null;
                                    result.ContentType = response.Content.Headers.ContentType != null ? response.Content.Headers.ContentType.MediaType : null;
                                    result.WwwAuthenticate = response.Headers.WwwAuthenticate.Count > 0 ? response.Headers.WwwAuthenticate.ToString() : null;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            result.BodyCompletedUtc = DateTime.UtcNow;
                            result.StatusCode = 0;
                            if (ex is OperationCanceledException && deadline.IsCancellationRequested)
                            {
                                result.Error = "RequestDeadlineExceeded: no response-body completion within "
                                    + requestTimeoutSeconds + " s of admission";
                            }
                            else
                            {
                                var baseline = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                                string message = baseline.Message ?? string.Empty;
                                int newline = message.IndexOfAny(new[] { '\r', '\n' });
                                if (newline >= 0)
                                {
                                    message = message.Substring(0, newline);
                                }
                                result.Error = baseline.GetType().Name + ": " + message;
                            }
                        }
                        finally
                        {
                            gate.Release();
                        }
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

$faultLockApplicationName = 'dms1556-fault-lock'

function Invoke-FaultJob {
    # Starts the injected outage on a thread job: it waits until -DueUtc, applies the
    # fault, holds it for -FaultDurationSeconds, removes it, and returns one record with
    # the host-clock instants of each step.
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory)][DateTime] $DueUtc)

    return Start-ThreadJob -ScriptBlock {
        $kind = $using:FaultKind
        $container = $using:FaultPgContainerName
        $database = $using:FaultDatabase
        $duration = $using:FaultDurationSeconds
        $applicationName = $using:faultLockApplicationName
        $due = [DateTime]::new(([DateTime]$using:DueUtc).Ticks, [DateTimeKind]::Utc)
        $record = [ordered]@{
            kind = $kind; container = $container; durationSeconds = $duration; dueUtc = $due.ToString('o')
            applyRequestedUtc = $null; appliedUtc = $null; removeRequestedUtc = $null; removedUtc = $null
            output = $null; error = $null
        }
        $waitMs = [int](($due - [DateTime]::UtcNow).TotalMilliseconds)
        if ($waitMs -gt 0) { Start-Sleep -Milliseconds $waitMs }
        try {
            if ($kind -eq 'PausePostgres') {
                $record.applyRequestedUtc = [DateTime]::UtcNow.ToString('o')
                $null = docker pause $container 2>&1
                if ($LASTEXITCODE -ne 0) { throw "docker pause $container failed ($LASTEXITCODE)." }
                $record.appliedUtc = [DateTime]::UtcNow.ToString('o')
                Start-Sleep -Seconds $duration
                $record.removeRequestedUtc = [DateTime]::UtcNow.ToString('o')
                $null = docker unpause $container 2>&1
                if ($LASTEXITCODE -ne 0) { throw "docker unpause $container failed ($LASTEXITCODE)." }
                $record.removedUtc = [DateTime]::UtcNow.ToString('o')
            }
            else {
                # One session: lock, report the server clock, hold, release by rollback.
                $sql = @"
BEGIN;
LOCK TABLE dmscs."OpenIddictKey" IN ACCESS EXCLUSIVE MODE;
SELECT 'locked ' || clock_timestamp();
SELECT pg_sleep($duration);
SELECT 'released ' || clock_timestamp();
ROLLBACK;
"@
                $record.applyRequestedUtc = [DateTime]::UtcNow.ToString('o')
                $output = $sql | docker exec -i $container psql -U postgres -d "dbname=$database application_name=$applicationName" -At -v ON_ERROR_STOP=1 2>&1
                $record.removedUtc = [DateTime]::UtcNow.ToString('o')
                $record.output = @($output | ForEach-Object { [string]$_ } | Where-Object { $_ -and $_ -notin @('BEGIN', 'LOCK TABLE', 'ROLLBACK') })
                if ($LASTEXITCODE -ne 0) { throw "Key-table lock session failed ($LASTEXITCODE): $($record.output -join ' / ')" }
            }
        }
        catch {
            $record.error = $_.Exception.Message
        }
        [pscustomobject]$record
    }
}

function Wait-KeyTableLockGranted {
    # The lock session is asynchronous; a cold restart must not begin before the lock is
    # actually held, or the startup load could read the keys first.
    [CmdletBinding()]
    [OutputType([string])]
    param([int] $TimeoutSeconds = 30)

    $query = "SELECT count(*) FROM pg_locks l JOIN pg_class c ON c.oid = l.relation JOIN pg_stat_activity a ON a.pid = l.pid WHERE c.relname = 'OpenIddictKey' AND l.mode = 'AccessExclusiveLock' AND l.granted AND a.application_name = '$faultLockApplicationName'"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $granted = docker exec $FaultPgContainerName psql -U postgres -d $FaultDatabase -Atc $query 2>$null
        if ($LASTEXITCODE -eq 0 -and [int]$granted -ge 1) { return [DateTime]::UtcNow.ToString('o') }
        Start-Sleep -Milliseconds 200
    }
    throw "The key-table lock was not granted within $TimeoutSeconds s."
}

function Clear-InjectedFault {
    # Unconditional cleanup; never throws, so it cannot mask the error that led here.
    [CmdletBinding()]
    [OutputType([string])]
    param()

    try {
        if ($FaultKind -eq 'PausePostgres') {
            $paused = docker inspect -f '{{.State.Paused}}' $FaultPgContainerName 2>$null
            if ($paused -eq 'true') {
                $null = docker unpause $FaultPgContainerName 2>&1
                return "unpaused $FaultPgContainerName in cleanup"
            }
        }
        elseif ($FaultKind -eq 'LockKeyTable') {
            $terminated = docker exec $FaultPgContainerName psql -U postgres -d postgres -Atc "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = '$faultLockApplicationName'" 2>$null
            if ([int]$terminated -gt 0) { return "terminated $terminated lock session(s) in cleanup" }
        }
    }
    catch {
        return "cleanup failed: $($_.Exception.Message)"
    }
    return $null
}

function Invoke-JwksProbeJob {
    # Polls the JWKS endpoint until $Control.stop is set; one record per request.
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory)][hashtable] $Control)

    return Start-ThreadJob -ScriptBlock {
        $control = $using:Control
        $url = "$($using:BaseUrl)/.well-known/jwks.json"
        $intervalMs = $using:JwksProbeIntervalMilliseconds
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        try {
            while (-not $control.stop) {
                $started = [DateTime]::UtcNow
                $status = 0; $keyCount = ''; $retryAfter = ''; $contentType = ''; $probeError = ''
                try {
                    $response = $client.GetAsync($url).GetAwaiter().GetResult()
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $status = [int]$response.StatusCode
                    if ($response.Headers.RetryAfter) { $retryAfter = $response.Headers.RetryAfter.ToString() }
                    if ($response.Content.Headers.ContentType) { $contentType = $response.Content.Headers.ContentType.MediaType }
                    if ($status -eq 200) {
                        try { $keyCount = @(($body | ConvertFrom-Json).keys).Count } catch { $probeError = 'unparseable 200 body' }
                    }
                    $response.Dispose()
                }
                catch {
                    $probeError = $_.Exception.GetBaseException().Message
                }
                $completed = [DateTime]::UtcNow
                [pscustomobject]@{
                    requestedUtc = $started.ToString('o'); completedUtc = $completed.ToString('o')
                    durationMs = [Math]::Round(($completed - $started).TotalMilliseconds, 1)
                    statusCode = $status; keyCount = $keyCount; retryAfter = $retryAfter; contentType = $contentType; error = $probeError
                }
                $sleepMs = $intervalMs - [int]($completed - $started).TotalMilliseconds
                if ($sleepMs -gt 0) { Start-Sleep -Milliseconds $sleepMs }
            }
        }
        finally {
            $client.Dispose()
        }
    }
}

function Invoke-Burst {
    [CmdletBinding()]
    param()

    if ($FaultKind -eq 'PausePostgres' -and $FaultRound -lt 1) {
        throw '-FaultRound 0 (before the cold restart) applies only to LockKeyTable: a paused PostgreSQL blocks CMS startup.'
    }

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

    Add-BurstRunnerType

    # The token is minted BEFORE any cold restart and reused afterwards, mirroring the
    # intended workload: DMS bursts with a cached token, so token issuance (a signing-key
    # read plus token-state writes) must not warm CMS's database connections between the
    # restart and the measured burst. The token is database-backed and survives the
    # restart. This preparation order is recorded in the summary.
    $tokenMintedUtc = [DateTime]::UtcNow.ToString('o')
    $token = Get-AccessToken
    $client = [Dms1556.BurstRunner]::CreateClient($token)

    $faultJob = $null
    $faultRecord = $null
    $lockGrantedUtc = $null
    $jwksJob = $null
    $jwksControl = [hashtable]::Synchronized(@{ stop = $false })
    # The outer try owns the injected fault from the moment it can exist: whatever fails
    # afterwards, the finally removes it (unpause / terminate the lock session).
    try {
    if ($FaultKind -ne 'None' -and $FaultRound -eq 0) {
        $faultJob = Invoke-FaultJob -DueUtc ([DateTime]::UtcNow)
        $lockGrantedUtc = Wait-KeyTableLockGranted
        Write-Output "Key-table lock granted at $lockGrantedUtc (before any restart)."
    }

    $coldPreparation = $null
    if ($Cold) {
        Write-Output "Cold run: restarting $CmsContainerName (token already minted)..."
        $restartedUtc = [DateTime]::UtcNow.ToString('o')
        docker restart $CmsContainerName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "docker restart $CmsContainerName failed with exit code $LASTEXITCODE."
        }
        # Unauthenticated readiness only: /health does not touch the token or key paths.
        Wait-CmsHealthy
        Write-Output 'CMS healthy after restart.'
        $coldPreparation = [pscustomobject]@{
            order          = 'token-minted-before-restart'
            tokenMintedUtc = $tokenMintedUtc
            restartedUtc   = $restartedUtc
            healthyUtc     = [DateTime]::UtcNow.ToString('o')
        }
    }

    $samplerState = $null
    try {
    # Samplers start only after the restart so they discover and bind to the NEW CMS
    # process, and the burst starts only once every sampler is verifiably producing data.
    # They start inside this try so the finally below owns their cleanup on ANY failure
    # from readiness onward.
    if ($WithSamplers) {
        Import-Module (Join-Path $PSScriptRoot 'dms-1556-samplers.psm1') -Force
        $samplerState = Start-DmsSamplerSet -Label $runId -OutputDirectory $OutputDirectory `
            -DurationSeconds $SamplerDurationSeconds -MonitorBaseUrl $MonitorBaseUrl -RequireMonitor `
            -StatsContainers $SamplerStatsContainers
        Wait-DmsSamplerSetReady -State $samplerState
        Write-Output 'Samplers ready (all captures producing data).'
    }

    $roundSummaries = [System.Collections.Generic.List[object]]::new()
    if ($JwksProbeIntervalMilliseconds -gt 0) {
        $jwksJob = Invoke-JwksProbeJob -Control $jwksControl
    }
    $burstWindowStartUtc = [DateTime]::UtcNow
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Output "Round ${round}/${Rounds}: $TotalRequests requests, concurrency $MaxConcurrency..."
        $burstStartUtc = [DateTime]::UtcNow
        if ($FaultKind -ne 'None' -and $FaultRound -ge 1 -and $round -eq $FaultRound) {
            $faultJob = Invoke-FaultJob -DueUtc $burstStartUtc.AddSeconds($FaultOffsetSeconds)
        }
        $stackJob = $null
        if ($StackCaptureOffsetsSeconds.Count -gt 0 -and $round -in $StackCaptureRounds) {
            if ($null -eq $samplerState -or $null -eq $samplerState.MonitorPid) {
                throw '-StackCaptureOffsetsSeconds requires -WithSamplers with a reachable dotnet-monitor sidecar.'
            }
            $stackMonitorPid = $samplerState.MonitorPid
            $stackStartTicks = $burstStartUtc.Ticks
            $stackPrefix = Join-Path $OutputDirectory "$runId-round$round-stacks"
            $stackJob = Start-ThreadJob -ScriptBlock {
                $monitorUrl = $using:MonitorBaseUrl
                $monitorPid = $using:stackMonitorPid
                $startTicks = $using:stackStartTicks
                $prefix = $using:stackPrefix
                foreach ($offset in $using:StackCaptureOffsetsSeconds) {
                    $due = [DateTime]::new($startTicks + [TimeSpan]::FromSeconds($offset).Ticks, [DateTimeKind]::Utc)
                    $waitMs = [int](($due - [DateTime]::UtcNow).TotalMilliseconds)
                    if ($waitMs -gt 0) { Start-Sleep -Milliseconds $waitMs }
                    $file = '{0}-t{1}.txt' -f $prefix, $offset
                    $requestedUtc = [DateTime]::UtcNow
                    $captureError = $null
                    try {
                        Invoke-WebRequest -Uri "$monitorUrl/stacks?pid=$monitorPid" -Headers @{ Accept = 'text/plain' } `
                            -TimeoutSec 60 -OutFile $file
                    }
                    catch {
                        $captureError = $_.Exception.Message
                    }
                    [pscustomobject]@{
                        offsetSeconds = $offset
                        requestedUtc  = $requestedUtc.ToString('o')
                        completedUtc  = [DateTime]::UtcNow.ToString('o')
                        file          = Split-Path -Leaf $file
                        error         = $captureError
                    }
                }
            }
        }
        $results = [Dms1556.BurstRunner]::Run(
            $client, $BaseUrl, $profileIds, $TotalRequests, $MaxConcurrency, [bool]$ValidateBodies, $RequestTimeoutSeconds)
        $burstEndUtc = [DateTime]::UtcNow
        $stackCaptures = @()
        if ($stackJob) {
            $null = Wait-Job -Job $stackJob -Timeout (($StackCaptureOffsetsSeconds | Measure-Object -Maximum).Maximum + 120)
            $stackCaptures = @(Receive-Job -Job $stackJob -ErrorAction SilentlyContinue)
            Remove-Job -Job $stackJob -Force
        }

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
                        # Ordinal, not PowerShell -eq: -eq compares strings
                        # case-insensitively, which would accept a definition that
                        # differs from the manifest only in case.
                        $parsed = $r.Body | ConvertFrom-Json
                        $isValid = ([int]$parsed.id -eq [int]$expected.id) -and
                            [string]::Equals([string]$parsed.name, [string]$expected.name, [StringComparison]::Ordinal) -and
                            [string]::Equals([string]$parsed.definition, [string]$expected.definition, [StringComparison]::Ordinal)
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
                    retryAfter       = if ($r.RetryAfter) { $r.RetryAfter } else { '' }
                    contentType      = if ($r.ContentType) { $r.ContentType } else { '' }
                    wwwAuthenticate  = if ($r.WwwAuthenticate) { $r.WwwAuthenticate } else { '' }
                })
        }

        $csvPath = Join-Path $OutputDirectory "$runId-round$round.csv"
        $rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8

        $statusHistogram = @{}
        foreach ($group in ($results | Group-Object -Property StatusCode)) {
            $statusHistogram[[string]$group.Name] = $group.Count
        }
        $durations = [double[]]@($results | ForEach-Object { $_.DurationMs } | Sort-Object)
        # Time-to-first-failure is when the first failure was OBSERVED (its completion),
        # not when that request was admitted: a request admitted at t=0 that fails after
        # 15 s is a failure at ~15 s, not at ~0.
        $failures = @($results | Where-Object { $_.StatusCode -ne 200 } | Sort-Object -Property BodyCompletedUtc)
        $timeToFirstFailureMs = if ($failures.Count -gt 0) {
            [Math]::Round(($failures[0].BodyCompletedUtc - $burstStartUtc).TotalMilliseconds, 1)
        }
        else { $null }

        $roundSummary = [pscustomobject]@{
            round                = $round
            startUtc             = $burstStartUtc.ToString('o')
            endUtc               = $burstEndUtc.ToString('o')
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
            stackCaptures        = $stackCaptures
        }
        $roundSummaries.Add($roundSummary)
        Write-Output ("  statuses: {0}; p50/p95/p99 ms: {1}/{2}/{3}; peak overlap: {4}" -f `
            (($statusHistogram.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '),
            $roundSummary.p50Ms, $roundSummary.p95Ms, $roundSummary.p99Ms, $roundSummary.measuredPeakOverlap)

        if ($InterRoundDelaySeconds -gt 0 -and $round -lt $Rounds) {
            Start-Sleep -Seconds $InterRoundDelaySeconds
        }
    }

    $burstWindowEndUtc = [DateTime]::UtcNow

    if ($faultJob) {
        $null = Wait-Job -Job $faultJob -Timeout ($FaultOffsetSeconds + $FaultDurationSeconds + 60)
        $faultRecord = @(Receive-Job -Job $faultJob -ErrorAction SilentlyContinue) | Select-Object -Last 1
        Remove-Job -Job $faultJob -Force
        $faultJob = $null
    }
    $jwksFile = $null
    $jwksCount = 0
    if ($jwksJob) {
        $jwksControl.stop = $true
        $null = Wait-Job -Job $jwksJob -Timeout 60
        $jwksRows = @(Receive-Job -Job $jwksJob -ErrorAction SilentlyContinue)
        Remove-Job -Job $jwksJob -Force
        $jwksJob = $null
        $jwksCount = $jwksRows.Count
        $jwksFile = Join-Path $OutputDirectory "$runId-jwks.csv"
        $jwksRows | Select-Object requestedUtc, completedUtc, durationMs, statusCode, keyCount, retryAfter, contentType, error |
            Export-Csv -LiteralPath $jwksFile -NoTypeInformation -Encoding utf8
    }

    # Stop the samplers only after the burst window closes, then verify their captures
    # actually span it: file existence alone says nothing about coverage. The short
    # settle lets every capture (slowest tick ~1 s) record past the burst end; the stop
    # then skips the remainder of the sampler window - the coverage validation judges
    # the captures by their actual timestamps, so nothing is taken on trust.
    $samplerReport = $null
    if ($samplerState) {
        Start-Sleep -Seconds 5
        $samplerReport = Stop-DmsSamplerSet -State $samplerState -SkipWindowWait `
            -BurstStartUtc $burstWindowStartUtc -BurstEndUtc $burstWindowEndUtc
    }

    $summary = [pscustomobject]@{
        runId           = $runId
        workload        = $RunLabel
        baseUrl         = $BaseUrl
        totalRequests   = $TotalRequests
        maxConcurrency  = $MaxConcurrency
        rounds          = $Rounds
        interRoundDelaySeconds = $InterRoundDelaySeconds
        cold            = [bool]$Cold
        coldPreparation = $coldPreparation
        validateBodies  = [bool]$ValidateBodies
        manifest        = $ManifestPath
        seededProfiles  = $manifestProfiles.Count
        startedUtc      = $runStartedUtc
        burstWindow     = [pscustomobject]@{
            startUtc = $burstWindowStartUtc.ToString('o')
            endUtc   = $burstWindowEndUtc.ToString('o')
        }
        cmsEnvironment  = Get-CmsEnvironmentRecord
        fault           = if ($FaultKind -eq 'None') { $null } else {
            [pscustomobject]@{
                kind           = $FaultKind
                round          = $FaultRound
                offsetSeconds  = $FaultOffsetSeconds
                lockGrantedUtc = $lockGrantedUtc
                record         = $faultRecord
            }
        }
        jwksProbe       = if ($JwksProbeIntervalMilliseconds -le 0) { $null } else {
            [pscustomobject]@{
                intervalMilliseconds = $JwksProbeIntervalMilliseconds
                file                 = if ($jwksFile) { Split-Path -Leaf $jwksFile } else { $null }
                requests             = $jwksCount
            }
        }
        roundSummaries  = $roundSummaries
        samplerReport   = $samplerReport
    }
    $summaryPath = Join-Path $OutputDirectory "$runId-summary.json"
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Output "Summary written to $summaryPath"

    # Fail AFTER the summary is written so the partial evidence is retained, but fail:
    # a run whose required runtime captures do not cover the burst window is not evidence.
    if ($samplerReport -and @($samplerReport.requiredFailures).Count -gt 0) {
        throw "Required sampler captures failed: $(@($samplerReport.requiredFailures) -join '; ')"
    }
    }
    finally {
        $client.Dispose()
        if ($null -ne $samplerState) {
            # Idempotent emergency cleanup: a no-op after a successful Stop-DmsSamplerSet,
            # otherwise it stops any still-owned sampler jobs. It never throws, so it
            # cannot mask the error that brought us here.
            Remove-DmsSamplerSet -State $samplerState
        }
    }
    }
    finally {
        $jwksControl.stop = $true
        foreach ($job in @($jwksJob, $faultJob) | Where-Object { $_ }) {
            Stop-Job -Job $job -ErrorAction SilentlyContinue
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        }
        if ($FaultKind -ne 'None') {
            $cleanup = Clear-InjectedFault
            if ($cleanup) { Write-Warning $cleanup }
        }
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Seed') {
    Invoke-ProfileSeeding
}
else {
    Invoke-Burst
}
