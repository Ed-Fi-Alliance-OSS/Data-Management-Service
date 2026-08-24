// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.QueryResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

internal class QueryRequestHandler(
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    ICollectionPagingTelemetry _collectionPagingTelemetry
) : IPipelineStep
{
    /// <summary>
    /// The response header carrying the token for the page after this one, as published by the Ed-Fi
    /// cursor-paging client contract.
    /// </summary>
    private const string NextPageTokenHeaderName = "Next-Page-Token";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering QueryRequestHandler - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolve query handler from the per-request scoped service provider
        var queryHandler = requestInfo.ScopedServiceProvider.GetRequiredService<IQueryHandler>();

        long startTimestamp = Stopwatch.GetTimestamp();
        QueryResult queryResult;

        try
        {
            queryResult = await ExecuteWithRetryLogging(
                _resiliencePipeline,
                _logger,
                "query",
                requestInfo.FrontendRequest.TraceId,
                r => IsRetryableResult(r),
                r => r is QuerySuccess,
                async ct => await queryHandler.QueryDocuments(CreateQueryRequest(requestInfo), ct),
                requestInfo
            );
        }
        catch (OperationCanceledException) when (requestInfo.RequestCancellationToken.IsCancellationRequested)
        {
            // A disconnected client is the absence of a completed collection read, not a kind of one.
            // Counting it would report client disconnects as backend execution failures, and its
            // duration would measure how long the client waited rather than how long a read took. The
            // same filter guards the logging middlewares this pipeline already runs through.
            throw;
        }
        catch
        {
            // CustomViewAuthorizationValidationException escapes execution as an exception rather than a
            // result, so without this the one fault that proves a configured view is broken would be the
            // one outcome the metric never saw.
            RecordExecutionException(requestInfo, Stopwatch.GetElapsedTime(startTimestamp));
            throw;
        }

        TimeSpan duration = Stopwatch.GetElapsedTime(startTimestamp);

        _logger.LogDebug(
            "QueryHandler returned {QueryResult}- {TraceId}",
            queryResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        bool nextPageTokenProduced = false;

        requestInfo.FrontendResponse = queryResult switch
        {
            QuerySuccess success => CreateSuccessResponse(requestInfo, success, out nextPageTokenProduced),
            QueryFailureNotImplemented failure => new FrontendResponse(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            QueryFailureSecurityConfiguration failure => CreateSecurityConfigurationFailureResponse(
                _logger,
                requestInfo,
                failure.Errors,
                failure.Diagnostics
            ),
            QueryFailureNamespaceNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    notAuthorized.NamespaceFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            // Returns 500 to match ODS/API behavior: after retries are exhausted for a deadlock,
            // the client receives a generic system error rather than a retryable status code.
            QueryFailureRetryable => new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            QueryFailureKnownError => new FrontendResponse(StatusCode: 400, Body: null, Headers: []),
            UnknownFailure failure => CreateUnknownFailureResponse(
                _logger,
                requestInfo,
                failure.FailureMessage
            ),
            _ => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError("Unknown QueryResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };

        RecordOutcome(requestInfo, queryResult, duration, nextPageTokenProduced);
    }

    /// <summary>
    /// Records the one measurement set this request contributes, classified from what the backend
    /// returned.
    /// </summary>
    /// <remarks>
    /// <paramref name="nextPageTokenProduced" /> is reported by response shaping rather than re-derived
    /// here, and the response header is never read: two independent derivations of the same fact would
    /// eventually disagree, and the header is absent for a reason that is not always terminal.
    /// </remarks>
    private void RecordOutcome(
        RequestInfo requestInfo,
        QueryResult queryResult,
        TimeSpan duration,
        bool nextPageTokenProduced
    )
    {
        (string commandCategory, string outcome, int? returnedPageSize) = queryResult switch
        {
            QuerySuccess success => ClassifySuccess(requestInfo, success, nextPageTokenProduced),
            QueryFailureNotImplemented => FailureClassification(
                CollectionPagingTelemetryLabel.NotImplementedOutcome
            ),
            QueryFailureSecurityConfiguration => FailureClassification(
                CollectionPagingTelemetryLabel.SecurityConfigurationOutcome
            ),
            QueryFailureNamespaceNotAuthorized => FailureClassification(
                CollectionPagingTelemetryLabel.NotAuthorizedOutcome
            ),
            QueryFailureRetryable => FailureClassification(
                CollectionPagingTelemetryLabel.RetryExhaustedOutcome
            ),
            // A known error reports query terms that evaded validation, and the bounded outcome set has
            // no value of its own for it. It is counted as an unclassified backend failure rather than
            // as validation_rejected, which names the middleware rejections operators watch as a share
            // of traffic and would be diluted by backend faults.
            _ => FailureClassification(CollectionPagingTelemetryLabel.UnknownFailureOutcome),
        };

        Record(requestInfo, duration, commandCategory, outcome, returnedPageSize);
    }

    private void RecordExecutionException(RequestInfo requestInfo, TimeSpan duration) =>
        Record(
            requestInfo,
            duration,
            CollectionPagingTelemetryLabel.NoCommandCategory,
            CollectionPagingTelemetryLabel.ExecutionExceptionOutcome,
            returnedPageSize: null
        );

    /// <summary>
    /// Emits one measurement set, and never lets a telemetry fault reach the client.
    /// </summary>
    /// <remarks>
    /// Instrumentation observes; it must not participate. Every call arrives after the response has been
    /// assembled, and the execution-exception call arrives from inside a catch that is about to rethrow,
    /// so an escaping throw would either replace a served response with a system error or replace the
    /// fault it was trying to report. Recording is not free of throwing code: label derivation rejects a
    /// request state the bounded dimension set cannot describe, and an instrument invokes whatever
    /// measurement callbacks the host has subscribed, which is third-party code on this thread.
    /// <para>
    /// The guard covers the derivations as well as the instruments, because the derivations run as
    /// arguments to them. Validation on the recording side stays fail-fast: it can only report a defect
    /// in this handler, and the tests are what catch those.
    /// </para>
    /// </remarks>
    private void Record(
        RequestInfo requestInfo,
        TimeSpan duration,
        string commandCategory,
        string outcome,
        int? returnedPageSize
    )
    {
        try
        {
            _collectionPagingTelemetry.RecordPage(
                CreateContext(requestInfo, commandCategory, outcome),
                duration,
                RequestedPageSize(requestInfo.CollectionPaging),
                returnedPageSize
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Collection paging telemetry was not recorded for outcome {Outcome} - {TraceId}",
                outcome,
                requestInfo.FrontendRequest.TraceId.Value
            );
        }
    }

    private static CollectionPagingTelemetryContext CreateContext(
        RequestInfo requestInfo,
        string commandCategory,
        string outcome
    ) =>
        CollectionPagingTelemetryContext.ForPaging(
            requestInfo.CollectionPaging,
            commandCategory,
            requestInfo.MappingSet?.Key.Dialect,
            outcome
        );

    /// <summary>
    /// The success classification, in precedence order: early-empty, then terminal page, then success.
    /// </summary>
    /// <remarks>
    /// A page that cannot anchor a DocumentId continuation is <c>success</c>, never
    /// <c>terminal_page</c>. That is a traditional page over a max-bearing change-version window: it is
    /// ordered by ContentVersion, is served with rows, and the client keeps paging it with limit and
    /// offset, so reporting it as an ended walk would tell operators a healthy walk had stopped.
    /// </remarks>
    private static (string CommandCategory, string Outcome, int? ReturnedPageSize) ClassifySuccess(
        RequestInfo requestInfo,
        QuerySuccess success,
        bool nextPageTokenProduced
    )
    {
        int returnedPageSize = success.EdfiDocs.Count;

        // Both halves of what early_empty asserts, not the flag alone: the outcome names an empty result
        // that no selection command produced. A success carrying documents was built from rows something
        // selected, so it is classified by what was served however the flag is set. Nothing produces that
        // combination today, and checking it is what keeps the one outcome whose name is a claim about
        // database work from ever being attached to a request that plainly did some.
        if (success is { SelectionSkipped: true, EdfiDocs.Count: 0 })
        {
            return (
                CollectionPagingTelemetryLabel.NoCommandCategory,
                CollectionPagingTelemetryLabel.EarlyEmptyOutcome,
                returnedPageSize
            );
        }

        string commandCategory = requestInfo.CollectionPaging.IncludesTotalCount
            ? CollectionPagingTelemetryLabel.PageWithCountCommandCategory
            : CollectionPagingTelemetryLabel.PageCommandCategory;
        string outcome =
            success.AllowsDocumentIdContinuation && !nextPageTokenProduced
                ? CollectionPagingTelemetryLabel.TerminalPageOutcome
                : CollectionPagingTelemetryLabel.SuccessOutcome;

        return (commandCategory, outcome, returnedPageSize);
    }

    /// <summary>
    /// Every failure carries no command category. Core cannot prove, for most of them, whether a
    /// selection command ran, and attributing a command shape — and therefore a duration — to a request
    /// that may never have issued that command is the failure mode the dimension exists to avoid.
    /// </summary>
    private static (string CommandCategory, string Outcome, int? ReturnedPageSize) FailureClassification(
        string outcome
    ) => (CollectionPagingTelemetryLabel.NoCommandCategory, outcome, null);

    private static int RequestedPageSize(CollectionPaging paging) =>
        paging switch
        {
            CollectionPaging.Cursor cursor => cursor.PageSize.Value,
            CollectionPaging.Traditional traditional => traditional.Parameters.Limit
                ?? traditional.Parameters.MaximumPageSize,
            _ => throw new ArgumentOutOfRangeException(
                nameof(paging),
                paging,
                "Unsupported collection paging mode."
            ),
        };

    private static FrontendResponse CreateSuccessResponse(
        RequestInfo requestInfo,
        QuerySuccess success,
        out bool nextPageTokenProduced
    )
    {
        var contentType = requestInfo.ProfileContext?.ResourceProfile.ReadContentType is not null
            ? ProfileHeaderParser.BuildProfileContentType(
                requestInfo.ResourceSchema.ResourceName.Value,
                requestInfo.ProfileContext.ProfileName,
                ProfileUsageType.Readable
            )
            : "application/json";

        Dictionary<string, string> headers = requestInfo.CollectionPaging.IncludesTotalCount
            ? new() { { "Total-Count", (success.TotalCount ?? 0).ToString() } }
            : [];

        nextPageTokenProduced = false;

        if (TryCreateNextPageToken(requestInfo, success, out var nextPageToken))
        {
            nextPageTokenProduced = true;
            headers.Add(NextPageTokenHeaderName, nextPageToken);
        }

        return new FrontendResponse(
            StatusCode: 200,
            Body: success.EdfiDocs,
            Headers: headers,
            ContentType: contentType
        );
    }

    /// <summary>
    /// Whether this page can hand the client a token for the page after it, and what that token is.
    /// </summary>
    /// <remarks>
    /// The one gate both resource families pass through: regular-resource and descriptor results reach
    /// it as the same <see cref="QuerySuccess"/>, so neither can acquire a header rule of its own. It
    /// asks what page selection chose, never what the response body contains — a page whose selected
    /// rows were all deleted before hydration still advances the walk past them, and a client that
    /// stopped on an empty body would stop early. A page that selected nothing, or one whose ordering
    /// key was not DocumentId, has nothing to anchor a continuation on. At
    /// <see cref="long.MaxValue"/> there is no next range to name, so the codec reports no token and
    /// the header is omitted.
    /// </remarks>
    private static bool TryCreateNextPageToken(
        RequestInfo requestInfo,
        QuerySuccess success,
        [NotNullWhen(true)] out string? nextPageToken
    )
    {
        nextPageToken = null;

        if (success.HighestSelectedDocumentId is not { } highestSelectedDocumentId)
        {
            return false;
        }

        if (!success.AllowsDocumentIdContinuation)
        {
            return false;
        }

        // A cursor request keeps its own upper bound, which is how a walk that entered through a
        // partition stays inside that partition. A traditional request carried no bound, so a walk
        // entered from one is unbounded above.
        long inclusiveMaximum = requestInfo.CollectionPaging is CollectionPaging.Cursor cursor
            ? cursor.Range.InclusiveMaximum
            : long.MaxValue;

        return PageTokenCodec.TryCreateNextPageToken(
                highestSelectedDocumentId,
                inclusiveMaximum,
                out nextPageToken
            ) && nextPageToken is not null;
    }

    private static IQueryRequest CreateQueryRequest(RequestInfo requestInfo)
    {
        var mappingSet = RequireMappingSet(requestInfo, "query");

        return new RelationalQueryRequest(
            ResourceInfo: requestInfo.ResourceInfo,
            AuthorizationContext: RelationalAuthorizationContext.Create(requestInfo.ClientAuthorizations),
            MappingSet: mappingSet,
            QueryElements: requestInfo.QueryElements,
            AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
            Paging: requestInfo.CollectionPaging,
            TraceId: requestInfo.FrontendRequest.TraceId,
            TenantKey: requestInfo.FrontendRequest.Tenant ?? string.Empty,
            ReadableProfileProjectionContext: CreateReadableProfileProjectionContext(requestInfo),
            ChangeVersionRange: requestInfo.ChangeVersionRange,
            ResponseContentCoding: GetServedEtagContentCoding(requestInfo)
        );
    }
}
