// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The DMS-1440 spec §5.4 token provider. It posts <c>grant_type=client_credentials</c> to the resolved token URL
/// through the projection client with HTTP Basic credentials: the tenant's <c>TenantCredentials</c> entry (tenant
/// matched exactly, else ignoring case), or the shared <c>Credentials</c> when the tenant has none and in single-tenant
/// mode. Each value is percent-encoded before Base64 (RFC 6749 §2.3.1), as the CMS token endpoint decodes them.
/// Tokens are cached per tenant and client id, not per URL, until their expiry minus
/// <c>TokenExpirySafetyMarginSeconds</c> counted from when the request started; a token whose lifetime does not exceed
/// the margin is used once and not cached. Concurrent callers for one tenant and client wait for a single request and
/// then use its token; a failed request is not cached, so the next waiter makes its own. The provider logs nothing
/// itself; its failures carry only the status and the sanitized problem fields, never a credential or token.
/// </summary>
public sealed class ProjectionServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<DmsEducationOrganizationProjectionSettings> options,
    TimeProvider timeProvider
) : IProjectionServiceTokenProvider
{
    /// <summary>The largest token response read, in bytes; a longer one is <c>MalformedResponse</c>.</summary>
    public const int MaxTokenResponseBytes = 1_048_576;

    private readonly DmsEducationOrganizationProjectionSettings _settings = options.Value;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<CacheKey, SemaphoreSlim> _gates = new();

    /// <summary>Tenant (empty in single-tenant mode) and client id, both compared ordinally.</summary>
    private readonly record struct CacheKey(string Tenant, string ClientId);

    private sealed record CacheEntry(ProjectionServiceToken Token, DateTimeOffset UsableUntil);

    private sealed record Credentials(string ClientId, string ClientSecret);

    /// <summary>A parsed token response.</summary>
    private sealed record IssuedToken(ProjectionServiceToken Token, long ExpiresInSeconds);

    public async Task<ProjectionServiceTokenResult> GetTokenAsync(
        string? tenantName,
        Uri tokenUrl,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        // Caller cancellation, then the read deadline, decide before anything else, a cached token included.
        if (
            CancelledOrPastDeadline(readDeadline, cancellationToken, out DateTimeOffset now) is
            { } pastDeadline
        )
        {
            return pastDeadline;
        }

        if (SelectCredentials(tenantName) is not { } credentials)
        {
            return Failed(Code.NotConfigured);
        }

        CacheKey key = new(tenantName ?? string.Empty, credentials.ClientId);
        if (Cached(key, now) is { } cached)
        {
            return new ProjectionServiceTokenResult.Issued(cached);
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await WaitForTurnAsync(gate, now, readDeadline, cancellationToken))
        {
            return Failed(Code.Timeout);
        }

        try
        {
            // The wait may have ended as the caller cancelled or the deadline passed; both still decide first.
            if (
                CancelledOrPastDeadline(readDeadline, cancellationToken, out now) is { } pastDeadlineAfterWait
            )
            {
                return pastDeadlineAfterWait;
            }

            if (Cached(key, now) is { } issuedWhileWaiting)
            {
                return new ProjectionServiceTokenResult.Issued(issuedWhileWaiting);
            }

            DateTimeOffset start = now;
            BoundedRequestResult<(IssuedToken?, EducationOrganizationProjectionFailure?)> result =
                await BoundedProjectionRequest.RunAsync(
                    token => RequestAsync(tokenUrl, credentials, token),
                    TimeSpan.FromSeconds(_settings.TokenRequestTimeoutSeconds),
                    start,
                    readDeadline,
                    timeProvider,
                    cancellationToken
                );
            if (result.Interruption is { } interruption)
            {
                return Failed(interruption);
            }

            (IssuedToken? issued, EducationOrganizationProjectionFailure? failure) = result.Outcome;
            if (failure is not null)
            {
                return new ProjectionServiceTokenResult.Failed(failure);
            }

            // Lifetimes beyond int.MaxValue seconds (about 68 years) are treated as that, so the sum cannot overflow.
            long lifetime =
                Math.Min(issued!.ExpiresInSeconds, int.MaxValue) - _settings.TokenExpirySafetyMarginSeconds;
            // A lifetime that does not exceed the margin stores an entry that is already unusable.
            _cache[key] = new CacheEntry(issued.Token, start.AddSeconds(lifetime));
            return new ProjectionServiceTokenResult.Issued(issued.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string? tenantName, ProjectionServiceToken token)
    {
        if (SelectCredentials(tenantName) is not { } credentials)
        {
            return;
        }

        CacheKey key = new(tenantName ?? string.Empty, credentials.ClientId);
        if (_cache.TryGetValue(key, out CacheEntry? entry) && ReferenceEquals(entry.Token, token))
        {
            // Remove only that entry, not one a concurrent read has stored since.
            _cache.TryRemove(KeyValuePair.Create(key, entry));
        }
    }

    /// <summary>The HTTP Basic parameter: Base64 of the percent-encoded client id, ":" and the percent-encoded secret.</summary>
    internal static string BasicParameter(string clientId, string clientSecret) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(clientSecret))
        );

    /// <summary>
    /// The tenant's own entry when it has one (complete or not: an incomplete entry is never replaced by the shared
    /// credential), else the shared credential; <c>null</c> when the chosen credential is incomplete or absent.
    /// </summary>
    private Credentials? SelectCredentials(string? tenantName)
    {
        DmsEducationOrganizationProjectionCredentials? selected = tenantName is null
            ? _settings.Credentials
            : TenantEntry(tenantName) ?? _settings.Credentials;

        return
            selected is { ClientId: { } clientId, ClientSecret: { } clientSecret }
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(clientSecret)
            ? new Credentials(clientId, clientSecret)
            : null;
    }

    private DmsEducationOrganizationProjectionCredentials? TenantEntry(string tenantName)
    {
        if (
            _settings.TenantCredentials.TryGetValue(
                tenantName,
                out DmsEducationOrganizationProjectionCredentials? exact
            )
        )
        {
            return exact;
        }
        foreach (
            (
                string tenant,
                DmsEducationOrganizationProjectionCredentials entry
            ) in _settings.TenantCredentials
        )
        {
            if (string.Equals(tenant, tenantName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    private ProjectionServiceToken? Cached(CacheKey key, DateTimeOffset now) =>
        _cache.TryGetValue(key, out CacheEntry? entry) && now < entry.UsableUntil ? entry.Token : null;

    /// <summary>
    /// Throws when the caller has cancelled; otherwise a <c>Timeout</c> failure when the read deadline has been
    /// reached, else <c>null</c> with the current time.
    /// </summary>
    private ProjectionServiceTokenResult.Failed? CancelledOrPastDeadline(
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken,
        out DateTimeOffset now
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        now = timeProvider.GetUtcNow();
        return now >= readDeadline ? Failed(Code.Timeout) : null;
    }

    /// <summary>
    /// Waits for <paramref name="gate"/> until the read deadline; <c>false</c> when the deadline came first. Caller
    /// cancellation throws with the caller's token, even when the deadline passed too.
    /// </summary>
    private async Task<bool> WaitForTurnAsync(
        SemaphoreSlim gate,
        DateTimeOffset now,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource deadlineSource = new(readDeadline - now, timeProvider);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineSource.Token
        );
        try
        {
            await gate.WaitAsync(linkedSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Sends the token request and reads and classifies its response.</summary>
    private async Task<(IssuedToken?, EducationOrganizationProjectionFailure?)> RequestAsync(
        Uri tokenUrl,
        Credentials credentials,
        CancellationToken cancellationToken
    )
    {
        using HttpClient client = httpClientFactory.CreateClient(
            DmsEducationOrganizationProjectionHttpClient.Name
        );
        using HttpRequestMessage request = new(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            BasicParameter(credentials.ClientId, credentials.ClientSecret)
        );
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        int status = (int)response.StatusCode;

        if (response.StatusCode != HttpStatusCode.OK)
        {
            ProblemFields problem = await ProjectionHttpContent.ReadProblemAsync(response, cancellationToken);
            return (
                null,
                Failure(
                    ProjectionFailureClassifier.ClassifyTokenStatus(response.StatusCode, problem.Type),
                    status,
                    problem
                )
            );
        }

        byte[]? body = await ProjectionHttpContent.ReadBoundedAsync(
            response.Content,
            MaxTokenResponseBytes,
            cancellationToken
        );
        IssuedToken? issued = body is null ? null : ParseToken(body);
        return issued is null ? (null, Failure(Code.MalformedResponse, status)) : (issued, null);
    }

    /// <summary>
    /// Tolerant parsing (unknown members ignored) of a token response: <c>access_token</c> a non-empty RFC 6750
    /// <c>b64token</c>, <c>token_type</c> <c>bearer</c> in any letter case, <c>expires_in</c> a positive integer.
    /// Anything else is <c>null</c>.
    /// </summary>
    private static IssuedToken? ParseToken(byte[] body)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            if (
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out JsonElement accessToken)
                && accessToken.ValueKind == JsonValueKind.String
                && accessToken.GetString() is { } token
                && IsB64Token(token)
                && root.TryGetProperty("token_type", out JsonElement tokenType)
                && tokenType.ValueKind == JsonValueKind.String
                && string.Equals(tokenType.GetString(), "bearer", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("expires_in", out JsonElement expiresIn)
                && expiresIn.ValueKind == JsonValueKind.Number
                && expiresIn.TryGetInt64(out long seconds)
                && seconds > 0
            )
            {
                return new IssuedToken(new ProjectionServiceToken(token), seconds);
            }
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Malformed JSON, or invalid UTF-8 inside a string read with GetString.
            return null;
        }
    }

    /// <summary>RFC 6750 §2.1 <c>b64token</c>: one or more of <c>A-Z a-z 0-9 - . _ ~ + /</c>, then any <c>=</c>.</summary>
    private static bool IsB64Token(string token)
    {
        int index = 0;
        while (
            index < token.Length
            && (
                char.IsAsciiLetterOrDigit(token[index])
                || token[index] is '-' or '.' or '_' or '~' or '+' or '/'
            )
        )
        {
            index++;
        }
        if (index == 0)
        {
            return false;
        }
        while (index < token.Length && token[index] == '=')
        {
            index++;
        }
        return index == token.Length;
    }

    private static EducationOrganizationProjectionFailure Failure(
        Code code,
        int? httpStatus = null,
        ProblemFields problem = default
    ) =>
        EducationOrganizationProjectionFailure.Create(
            code,
            EducationOrganizationProjectionStage.Token,
            httpStatus,
            problem.Type,
            problem.CorrelationId
        );

    private static ProjectionServiceTokenResult.Failed Failed(Code code) => new(Failure(code));
}
