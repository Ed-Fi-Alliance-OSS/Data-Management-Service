// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The DMS-1440 spec §5.4 complete-read loop. It resolves the store through Discovery, then reads pages from the first
/// one, following <c>nextCursor</c> until it is <c>null</c>, with a bearer token from the token provider. One deadline,
/// <c>TotalReadTimeoutSeconds</c> from the start, bounds Discovery, every token request, every page and every restart;
/// each request is also bounded by its own stage timeout. A 401 page drops the token and repeats that page once with a
/// new one. A 409 <c>projection-changed</c>, or a 400 <c>invalid-cursor</c> answering a cursor, restarts the read from
/// the first page while restarts remain. <c>MaxPages</c>, <c>MaxItems</c> and the seen cursors are per attempt. Any
/// failure ends the read with no items; a failure coded <c>DiscoveryInvalid</c>, <c>TargetNotFound</c> or
/// <c>Unauthorized</c>, at any stage, also drops the tenant's cached Discovery document. The reader logs one record per
/// read that returns: Information for success, Warning for failure.
/// </summary>
public sealed class EducationOrganizationProjectionReader(
    IDmsDiscoveryClient discoveryClient,
    IProjectionServiceTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    IOptions<DmsEducationOrganizationProjectionSettings> options,
    TimeProvider timeProvider,
    ILogger<EducationOrganizationProjectionReader> logger
) : IEducationOrganizationProjectionReader
{
    private static readonly HashSet<string> _discriminators = new(StringComparer.Ordinal)
    {
        "edfi.StateEducationAgency",
        "edfi.EducationServiceCenter",
        "edfi.LocalEducationAgency",
        "edfi.School",
    };

    private readonly DmsEducationOrganizationProjectionSettings _settings = options.Value;

    /// <summary>A page response: the status, its problem fields, and for a 200 the body (<c>null</c> when over the cap).</summary>
    private sealed record PageResponse(HttpStatusCode Status, ProblemFields Problem, byte[]? Body);

    /// <summary>
    /// How one attempt ended: the whole set, or a failure with the pages read and whether a restart may follow.
    /// </summary>
    private readonly record struct AttemptResult(
        List<EducationOrganizationProjectionItem>? Items,
        int Pages,
        EducationOrganizationProjectionFailure? Failure,
        bool Restartable
    );

    public async Task<EducationOrganizationProjectionReadResult> ReadAllAsync(
        EducationOrganizationProjectionReadRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        long started = timeProvider.GetTimestamp();
        DateTimeOffset readDeadline = timeProvider.GetUtcNow().AddSeconds(_settings.TotalReadTimeoutSeconds);

        EducationOrganizationProjectionReadResult result = await ReadAsync(
            request,
            readDeadline,
            cancellationToken
        );

        if (
            result is EducationOrganizationProjectionReadResult.Failure
            {
                Detail.Code: Code.DiscoveryInvalid or Code.TargetNotFound or Code.Unauthorized,
            }
        )
        {
            discoveryClient.Invalidate(request.TenantName);
        }

        Log(request, result, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    /// <summary>The page URL: the resolved projection URL with the four query parameters.</summary>
    internal static Uri PageUrl(
        Uri projectionUrl,
        int dataStoreId,
        int pageSize,
        string contractVersion,
        string? cursor
    )
    {
        string query =
            "dataStoreId="
            + dataStoreId.ToString(CultureInfo.InvariantCulture)
            + "&limit="
            + pageSize.ToString(CultureInfo.InvariantCulture)
            + "&contractVersion="
            + Uri.EscapeDataString(contractVersion)
            + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
        // Discovery templates carry no query, so the resolved URL has none to keep.
        return new Uri(projectionUrl.GetLeftPart(UriPartial.Path) + "?" + query);
    }

    private async Task<EducationOrganizationProjectionReadResult> ReadAsync(
        EducationOrganizationProjectionReadRequest request,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        DmsDiscoveryResolution resolution = await discoveryClient.ResolveAsync(
            request.TenantName,
            request.DataStoreContexts,
            readDeadline,
            cancellationToken
        );
        if (resolution is DmsDiscoveryResolution.Failed discoveryFailed)
        {
            return new EducationOrganizationProjectionReadResult.Failure(discoveryFailed.Failure);
        }
        var target = (DmsDiscoveryResolution.Resolved)resolution;

        int restarts = 0;
        while (true)
        {
            AttemptResult attempt = await ReadAttemptAsync(request, target, readDeadline, cancellationToken);
            if (attempt.Items is not null)
            {
                return new EducationOrganizationProjectionReadResult.Success(
                    attempt.Items,
                    target.ContractVersion,
                    attempt.Pages,
                    restarts
                );
            }
            if (attempt.Restartable && restarts < _settings.MaxWalkRestarts)
            {
                restarts++;
                continue;
            }
            return new EducationOrganizationProjectionReadResult.Failure(
                attempt.Failure! with
                {
                    PagesRead = attempt.Pages,
                    Restarts = restarts,
                }
            );
        }
    }

    /// <summary>One attempt: every page from the first, with the §5.4 checks in order.</summary>
    private async Task<AttemptResult> ReadAttemptAsync(
        EducationOrganizationProjectionReadRequest request,
        DmsDiscoveryResolution.Resolved target,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        List<EducationOrganizationProjectionItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? cursor = null;
        int pages = 0;
        long? previousId = null;

        AttemptResult Failed(Code code, int? httpStatus = null, ProblemFields problem = default) =>
            new(null, pages, PageFailure(code, httpStatus, problem), Restartable: false);

        while (true)
        {
            (PageResponse? response, EducationOrganizationProjectionFailure? failure) = await FetchPageAsync(
                request,
                target,
                cursor,
                readDeadline,
                cancellationToken
            );
            if (failure is not null)
            {
                return new AttemptResult(null, pages, failure, Restartable: false);
            }

            int status = (int)response!.Status;
            if (response.Status != HttpStatusCode.OK)
            {
                Code code = ProjectionFailureClassifier.ClassifyPageStatus(
                    response.Status,
                    response.Problem.Type
                );
                bool restartable =
                    code == Code.ProjectionChanged
                    || (
                        cursor is not null
                        && ProjectionFailureClassifier.IsInvalidCursor(response.Status, response.Problem.Type)
                    );
                return new AttemptResult(
                    null,
                    pages,
                    PageFailure(code, status, response.Problem),
                    restartable
                );
            }

            if (response.Body is null)
            {
                return Failed(Code.LimitExceeded, status);
            }

            if (
                ProjectionResponseParser.Parse(response.Body) is not { } page
                || !string.Equals(page.ContractVersion, target.ContractVersion, StringComparison.Ordinal)
                || page.DataStoreId != request.DataStoreId
            )
            {
                return Failed(Code.MalformedResponse, status);
            }

            pages++;
            if (pages > _settings.MaxPages)
            {
                return Failed(Code.LimitExceeded, status);
            }

            // Only the first page of an empty set has no items, and it ends the set.
            if (page.Items.Count == 0 && (pages > 1 || page.NextCursor is not null))
            {
                return Failed(Code.MalformedResponse, status);
            }

            foreach (EducationOrganizationProjectionItem item in page.Items)
            {
                if (
                    !_discriminators.Contains(item.Discriminator)
                    || item.NameOfInstitution.Length == 0
                    || item.EducationOrganizationId <= previousId
                    || item.ParentId == item.EducationOrganizationId
                )
                {
                    return Failed(Code.DataInvalid, status);
                }
                previousId = item.EducationOrganizationId;
            }

            if (items.Count + page.Items.Count > _settings.MaxItems)
            {
                return Failed(Code.LimitExceeded, status);
            }
            items.AddRange(page.Items);

            if (page.NextCursor is null)
            {
                return HasUnresolvedParent(items)
                    ? Failed(Code.DataInvalid, status)
                    : new AttemptResult(items, pages, null, Restartable: false);
            }

            // seen holds every cursor sent in this attempt, the current one included.
            if (!seen.Add(page.NextCursor))
            {
                return Failed(Code.MalformedResponse, status);
            }
            cursor = page.NextCursor;
        }
    }

    /// <summary>Whether a parent id names no item of the set.</summary>
    private static bool HasUnresolvedParent(List<EducationOrganizationProjectionItem> items)
    {
        HashSet<long> ids = [.. items.Select(item => item.EducationOrganizationId)];
        return items.Exists(item => item.ParentId is { } parentId && !ids.Contains(parentId));
    }

    /// <summary>
    /// One page with a token, and once more with a new token when DMS answers 401. A Token-stage failure, an
    /// interruption, or the read deadline reached before the request ends the fetch with a failure.
    /// </summary>
    private async Task<(PageResponse?, EducationOrganizationProjectionFailure?)> FetchPageAsync(
        EducationOrganizationProjectionReadRequest request,
        DmsDiscoveryResolution.Resolved target,
        string? cursor,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    )
    {
        Uri pageUrl = PageUrl(
            target.ProjectionUrl,
            request.DataStoreId,
            _settings.PageSize,
            target.ContractVersion,
            cursor
        );

        bool refreshed = false;
        while (true)
        {
            ProjectionServiceTokenResult tokenResult = await tokenProvider.GetTokenAsync(
                request.TenantName,
                target.TokenUrl,
                readDeadline,
                cancellationToken
            );
            if (tokenResult is ProjectionServiceTokenResult.Failed tokenFailed)
            {
                return (null, tokenFailed.Failure);
            }
            ProjectionServiceToken token = ((ProjectionServiceTokenResult.Issued)tokenResult).Token;

            // The token request may have used the time left; the page request needs some.
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset start = timeProvider.GetUtcNow();
            if (start >= readDeadline)
            {
                return (null, PageFailure(Code.Timeout));
            }

            BoundedRequestResult<PageResponse> result = await BoundedProjectionRequest.RunAsync(
                requestToken => SendAsync(pageUrl, token, requestToken),
                TimeSpan.FromSeconds(_settings.PageRequestTimeoutSeconds),
                start,
                readDeadline,
                timeProvider,
                cancellationToken
            );
            if (result.Interruption is { } interruption)
            {
                return (null, PageFailure(interruption));
            }

            if (result.Outcome.Status == HttpStatusCode.Unauthorized)
            {
                // DMS refused this token: never offer it again, and try the page once more with a new one.
                tokenProvider.Invalidate(request.TenantName, token);
                if (!refreshed)
                {
                    refreshed = true;
                    continue;
                }
            }
            return (result.Outcome, null);
        }
    }

    /// <summary>Sends a page request and reads its response: the problem fields, or a 200 body up to the cap.</summary>
    private async Task<PageResponse> SendAsync(
        Uri pageUrl,
        ProjectionServiceToken token,
        CancellationToken cancellationToken
    )
    {
        using HttpClient client = httpClientFactory.CreateClient(
            DmsEducationOrganizationProjectionHttpClient.Name
        );
        using HttpRequestMessage request = new(HttpMethod.Get, pageUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new PageResponse(
                response.StatusCode,
                await ProjectionHttpContent.ReadProblemAsync(response, cancellationToken),
                null
            );
        }

        return new PageResponse(
            response.StatusCode,
            ProblemFields.None,
            await ProjectionHttpContent.ReadBoundedAsync(
                response.Content,
                _settings.MaxResponseBodyBytes,
                cancellationToken
            )
        );
    }

    private void Log(
        EducationOrganizationProjectionReadRequest request,
        EducationOrganizationProjectionReadResult result,
        long elapsedMilliseconds
    )
    {
        string tenant = LoggingUtility.SanitizeForLog(request.TenantName);
        switch (result)
        {
            case EducationOrganizationProjectionReadResult.Success success:
                logger.LogInformation(
                    "Read the education organization projection of tenant {Tenant} data store {DataStoreId}: {ItemCount} items in {PageCount} pages after {Restarts} restarts, contract {ContractVersion}, {ElapsedMilliseconds} ms",
                    tenant,
                    request.DataStoreId,
                    success.Items.Count,
                    success.PageCount,
                    success.Restarts,
                    success.ContractVersion,
                    elapsedMilliseconds
                );
                break;

            case EducationOrganizationProjectionReadResult.Failure { Detail: var failure }:
                logger.LogWarning(
                    "Reading the education organization projection of tenant {Tenant} data store {DataStoreId} failed at {Stage}: {Code} ({Category}), HTTP status {HttpStatus}, problem type {ProblemType}, correlation id {CorrelationId}, {PagesRead} pages read, {Restarts} restarts, {ElapsedMilliseconds} ms",
                    tenant,
                    request.DataStoreId,
                    failure.Stage,
                    failure.Code,
                    failure.Category,
                    failure.HttpStatus,
                    failure.ProblemType,
                    failure.CorrelationId,
                    failure.PagesRead,
                    failure.Restarts,
                    elapsedMilliseconds
                );
                break;
        }
    }

    private static EducationOrganizationProjectionFailure PageFailure(
        Code code,
        int? httpStatus = null,
        ProblemFields problem = default
    ) =>
        EducationOrganizationProjectionFailure.Create(
            code,
            EducationOrganizationProjectionStage.Page,
            httpStatus,
            problem.Type,
            problem.CorrelationId
        );
}
