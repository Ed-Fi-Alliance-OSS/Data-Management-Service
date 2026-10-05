// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The DMS-1440 spec §5.3 Discovery client. It reads <c>GET {DmsBaseUrl}/{tenant}</c> (multi-tenant) or
/// <c>GET {DmsBaseUrl}/</c> (single-tenant) with no credentials through the named projection client, which follows no
/// redirect, and caches the parsed document per tenant for <c>DiscoveryCacheSeconds</c>. Failed reads are not cached.
/// A template that fails containment drops the cached document, so the next read fetches Discovery again. The client
/// logs nothing itself; its failures carry only the status and the sanitized problem fields.
/// </summary>
public sealed class DmsDiscoveryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<DmsEducationOrganizationProjectionSettings> options,
    TimeProvider timeProvider
) : IDmsDiscoveryClient
{
    /// <summary>The largest Discovery document read, in bytes; a longer one is <c>DiscoveryInvalid</c>.</summary>
    public const int MaxDocumentBytes = 1_048_576;

    private const char PathSeparator = '/';

    private readonly DmsEducationOrganizationProjectionSettings _settings = options.Value;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>The members of a Discovery document the reader uses, with the chosen contract version.</summary>
    private sealed record Document(
        string TokenUrlTemplate,
        string ProjectionUrlTemplate,
        string ContractVersion
    );

    private sealed record CacheEntry(Document Document, DateTimeOffset ExpiresAt);

    public async Task<DmsDiscoveryResolution> ResolveAsync(
        string? tenantName,
        IReadOnlyDictionary<string, string> dataStoreContexts,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        // Caller cancellation, then the read deadline, decide before anything else, a cached document included.
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (now >= readDeadline)
        {
            return Failed(Code.Timeout);
        }

        if (
            string.IsNullOrWhiteSpace(_settings.DmsBaseUrl)
            || !Uri.TryCreate(_settings.DmsBaseUrl, UriKind.Absolute, out Uri? baseUrl)
        )
        {
            return Failed(Code.NotConfigured);
        }

        // An empty, "." or ".." tenant would leave or climb out of the base path once escaped, and a malformed
        // UTF-16 tenant would be escaped with U+FFFD in place of its lone surrogate.
        if (
            tenantName is not null
            && (
                tenantName is "" or "." or ".."
                || !ProjectionUrlTemplateResolver.IsWellFormedUtf16(tenantName)
            )
        )
        {
            return Failed(Code.TargetNotRoutable);
        }

        string cacheKey = tenantName ?? string.Empty;
        if (!_cache.TryGetValue(cacheKey, out CacheEntry? entry) || entry.ExpiresAt <= now)
        {
            (Document? document, EducationOrganizationProjectionFailure? failure) = await FetchAsync(
                baseUrl,
                tenantName,
                now,
                readDeadline,
                cancellationToken
            );
            if (failure is not null)
            {
                return new DmsDiscoveryResolution.Failed(failure);
            }

            entry = new CacheEntry(
                document!,
                timeProvider.GetUtcNow().AddSeconds(_settings.DiscoveryCacheSeconds)
            );
            // With DiscoveryCacheSeconds 0 the entry is already expired, so the next read fetches again.
            _cache[cacheKey] = entry;
        }

        ProjectionUrlResolution token = ProjectionUrlTemplateResolver.Resolve(
            entry.Document.TokenUrlTemplate,
            dataStoreContexts,
            baseUrl
        );
        ProjectionUrlResolution projection = ProjectionUrlTemplateResolver.Resolve(
            entry.Document.ProjectionUrlTemplate,
            dataStoreContexts,
            baseUrl
        );

        switch (token, projection)
        {
            case (ProjectionUrlResolution.Resolved tokenUrl, ProjectionUrlResolution.Resolved projectionUrl):
                return new DmsDiscoveryResolution.Resolved(
                    tokenUrl.Url,
                    projectionUrl.Url,
                    entry.Document.ContractVersion
                );

            case (ProjectionUrlResolution.Rejected { Code: Code.DiscoveryInvalid }, _)
            or (_, ProjectionUrlResolution.Rejected { Code: Code.DiscoveryInvalid }):
                // Remove only the entry used, not one another read has stored since.
                _cache.TryRemove(KeyValuePair.Create(cacheKey, entry));
                return Failed(Code.DiscoveryInvalid);

            default:
                return Failed(Code.TargetNotRoutable);
        }
    }

    public void Invalidate(string? tenantName) => _cache.TryRemove(tenantName ?? string.Empty, out _);

    /// <summary>The Discovery document URL: the base path without its trailing slash, then "/" and the escaped tenant.</summary>
    internal static Uri DocumentUrl(Uri baseUrl, string? tenantName)
    {
        string root = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd(PathSeparator);
        return new Uri(
            tenantName is null
                ? root + PathSeparator
                : root + PathSeparator + Uri.EscapeDataString(tenantName)
        );
    }

    /// <summary>
    /// One Discovery request, bounded by <c>DiscoveryTimeoutSeconds</c> and the read deadline. However the request
    /// ends, normally or with an exception, caller cancellation is checked first and the timeout second, so an outcome
    /// that arrives after either is never classified, cached or returned: cancellation throws with the caller's token
    /// and an expired timeout is <c>Timeout</c>, even when a transport exception followed it.
    /// </summary>
    private async Task<(Document?, EducationOrganizationProjectionFailure?)> FetchAsync(
        Uri baseUrl,
        string? tenantName,
        DateTimeOffset start,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        // start is before readDeadline (ResolveAsync checked), so the request has time left.
        TimeSpan timeout = TimeSpan.FromSeconds(_settings.DiscoveryTimeoutSeconds);
        DateTimeOffset requestDeadline = start + timeout < readDeadline ? start + timeout : readDeadline;

        using CancellationTokenSource timeoutSource = new(requestDeadline - start, timeProvider);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

        // The timer covers a timeout that fires before the clock reads the due time; the clock covers one whose timer
        // has not run yet when the request ends.
        bool TimedOut() =>
            timeoutSource.IsCancellationRequested || timeProvider.GetUtcNow() >= requestDeadline;

        (Document?, EducationOrganizationProjectionFailure?) outcome;
        try
        {
            outcome = await SendAndReadAsync(baseUrl, tenantName, linkedSource.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
            when (TimedOut()
                && (
                    exception is OperationCanceledException
                    || ProjectionFailureClassifier.IsTransportFailure(exception)
                )
            )
        {
            return (null, Failure(Code.Timeout));
        }
        catch (Exception exception)
            when (exception is OperationCanceledException
                || ProjectionFailureClassifier.IsTransportFailure(exception)
            )
        {
            return (null, Failure(Code.NetworkError));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return TimedOut() ? (null, Failure(Code.Timeout)) : outcome;
    }

    /// <summary>Sends the Discovery request and reads and classifies its response.</summary>
    private async Task<(Document?, EducationOrganizationProjectionFailure?)> SendAndReadAsync(
        Uri baseUrl,
        string? tenantName,
        CancellationToken cancellationToken
    )
    {
        using HttpClient client = httpClientFactory.CreateClient(
            DmsEducationOrganizationProjectionHttpClient.Name
        );
        using HttpRequestMessage request = new(HttpMethod.Get, DocumentUrl(baseUrl, tenantName));
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
                    ProjectionFailureClassifier.ClassifyDiscoveryStatus(response.StatusCode, problem.Type),
                    status,
                    problem
                )
            );
        }

        byte[]? body = await ProjectionHttpContent.ReadBoundedAsync(
            response.Content,
            MaxDocumentBytes,
            cancellationToken
        );
        if (body is null)
        {
            return (null, Failure(Code.DiscoveryInvalid, status));
        }

        (Document? document, Code? code) = ParseDocument(body, _settings.EffectiveContractVersions);
        return code is null ? (document, null) : (null, Failure(code.Value, status));
    }

    /// <summary>
    /// Tolerant parsing (unknown members ignored): <c>urls</c> must be an object with a non-empty string
    /// <c>oauth</c>; a missing <c>urls.educationOrganizationProjection</c> or top-level
    /// <c>educationOrganizationProjection</c> is <c>Unsupported</c>; otherwise the URL must be a non-empty string and
    /// <c>contractVersions</c> a non-empty array of strings, or the document is <c>DiscoveryInvalid</c>. The version
    /// chosen is the last entry of <see cref="DmsEducationOrganizationProjectionSettings.SupportedContractVersions"/>
    /// (ordered oldest first) that both this service offers and Discovery lists; none is <c>UnsupportedContract</c>.
    /// </summary>
    private static (Document?, Code?) ParseDocument(byte[] body, IReadOnlyList<string> offeredVersions)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;

            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("urls", out JsonElement urls)
                || urls.ValueKind != JsonValueKind.Object
                || NonEmptyString(urls, "oauth") is not { } tokenUrlTemplate
            )
            {
                return (null, Code.DiscoveryInvalid);
            }

            if (
                !urls.TryGetProperty("educationOrganizationProjection", out _)
                || !root.TryGetProperty("educationOrganizationProjection", out JsonElement projection)
            )
            {
                return (null, Code.Unsupported);
            }

            if (
                NonEmptyString(urls, "educationOrganizationProjection") is not { } projectionUrlTemplate
                || projection.ValueKind != JsonValueKind.Object
                || !projection.TryGetProperty("contractVersions", out JsonElement versions)
                || versions.ValueKind != JsonValueKind.Array
                || versions.GetArrayLength() == 0
                || versions.EnumerateArray().Any(version => version.ValueKind != JsonValueKind.String)
            )
            {
                return (null, Code.DiscoveryInvalid);
            }

            HashSet<string> listed = new(
                versions.EnumerateArray().Select(version => version.GetString()!),
                StringComparer.Ordinal
            );
            string? chosen =
                DmsEducationOrganizationProjectionSettings.SupportedContractVersions.LastOrDefault(version =>
                    listed.Contains(version) && offeredVersions.Contains(version, StringComparer.Ordinal)
                );

            return chosen is null
                ? (null, Code.UnsupportedContract)
                : (new Document(tokenUrlTemplate, projectionUrlTemplate, chosen), null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Malformed JSON, or invalid UTF-8 inside a string read with GetString.
            return (null, Code.DiscoveryInvalid);
        }
    }

    private static string? NonEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static EducationOrganizationProjectionFailure Failure(
        Code code,
        int? httpStatus = null,
        ProblemFields problem = default
    ) =>
        EducationOrganizationProjectionFailure.Create(
            code,
            EducationOrganizationProjectionStage.Discovery,
            httpStatus,
            problem.Type,
            problem.CorrelationId
        );

    private static DmsDiscoveryResolution.Failed Failed(Code code) => new(Failure(code));
}
