// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.PartitionResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Serves a partitions request by calculating boundaries and encoding each as a page token.
/// </summary>
/// <param name="_maximumPageSize">
/// The configured maximum page size, from which the minimum partition size is derived. Supplied at
/// composition so the size a partition is measured against is the same page size a walk of that
/// partition will use.
/// </param>
/// <remarks>
/// This is the only place partition token text is created. The backend contracts, planners, and SQL
/// compilers receive and return typed <see cref="External.Model.CursorRange" /> values, which is what
/// keeps a compiler from ever seeing client-supplied text.
/// </remarks>
internal class PartitionRequestHandler(
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    int _maximumPageSize,
    ICollectionPagingTelemetry _collectionPagingTelemetry
) : IPipelineStep
{
    /// <summary>
    /// The response body member carrying one token per partition. Internal so OpenAPI assembly publishes
    /// the member this handler writes rather than a second spelling of it.
    /// </summary>
    internal const string PageTokensMember = "pageTokens";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering PartitionRequestHandler - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolved from the per-request scoped service provider, as every other handler resolves its
        // backend seam.
        var partitionQueryHandler =
            requestInfo.ScopedServiceProvider.GetRequiredService<IPartitionQueryHandler>();

        // Resolved before timing starts. Its absence is a pipeline composition fault rather than a
        // collection read, so it must not be rediscovered from inside the recording catch, where the
        // throw it raises would replace the fault it was trying to report.
        int requestedPartitionCount = RequireRequestedPartitionCount(requestInfo);

        long startTimestamp = Stopwatch.GetTimestamp();
        PartitionResult partitionResult;

        try
        {
            partitionResult = await ExecuteWithRetryLogging(
                _resiliencePipeline,
                _logger,
                "partitions",
                requestInfo.FrontendRequest.TraceId,
                r => IsRetryableResult(r),
                r => r is PartitionSuccess,
                async ct =>
                    await partitionQueryHandler.QueryPartitions(
                        CreatePartitionRequest(requestInfo, requestedPartitionCount),
                        ct
                    ),
                requestInfo,
                // A read is safe to abandon when the client disconnects: nothing is persisted,
                // so stopping the retry loop only stops work nobody is waiting for.
                requestInfo.RequestCancellationToken
            );
        }
        catch (OperationCanceledException) when (requestInfo.RequestCancellationToken.IsCancellationRequested)
        {
            // A disconnected client is the absence of a completed boundary calculation, not a kind of
            // one, and its duration would measure how long the client waited rather than how long the
            // boundary command took.
            throw;
        }
        catch
        {
            // CustomViewAuthorizationValidationException escapes execution as an exception rather than a
            // result, and the boundary command selects through the configured views.
            Record(
                requestInfo,
                Stopwatch.GetElapsedTime(startTimestamp),
                requestedPartitionCount,
                CollectionPagingTelemetryLabel.NoCommandCategory,
                CollectionPagingTelemetryLabel.ExecutionExceptionOutcome,
                returnedPartitionCount: null
            );
            throw;
        }

        TimeSpan duration = Stopwatch.GetElapsedTime(startTimestamp);

        _logger.LogDebug(
            "PartitionQueryHandler returned {PartitionResult}- {TraceId}",
            partitionResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = partitionResult switch
        {
            PartitionSuccess success => CreateSuccessResponse(success, requestInfo.PageOrderingMode),
            PartitionFailureNotImplemented failure => new FrontendResponse(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            PartitionFailureSecurityConfiguration failure => CreateSecurityConfigurationFailureResponse(
                _logger,
                requestInfo,
                failure.Errors,
                failure.Diagnostics
            ),
            PartitionFailureNamespaceNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    notAuthorized.NamespaceFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            // Returns 500 to match the GET-many path: after retries are exhausted for a deadlock, the
            // client receives a generic system error rather than a retryable status code.
            PartitionFailureRetryable => new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UnknownPartitionFailure failure => CreateUnknownFailureResponse(
                _logger,
                requestInfo,
                failure.FailureMessage
            ),
            _ => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError("Unknown PartitionResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };

        ClassifyAndRecord(requestInfo, partitionResult, duration, requestedPartitionCount);
    }

    /// <summary>
    /// Records the one measurement set this request contributes.
    /// </summary>
    /// <remarks>
    /// There is no terminal-page outcome here: a boundary set has no successor, so the question a
    /// GET-many page answers about continuation does not arise. An executed boundary command that found
    /// no starts is a success with a returned count of zero, which is a different fact from a
    /// short-circuit that issued no command at all.
    /// </remarks>
    private void ClassifyAndRecord(
        RequestInfo requestInfo,
        PartitionResult partitionResult,
        TimeSpan duration,
        int requestedPartitionCount
    )
    {
        (string commandCategory, string outcome, int? returnedPartitionCount) = partitionResult switch
        {
            // Both halves of what early_empty asserts, not the flag alone. A success carrying ranges was
            // built by a boundary command whatever the flag says, so it falls through to the arm below
            // and is reported as the boundary work it did.
            PartitionSuccess { SelectionSkipped: true, Ranges.Count: 0 } skipped => (
                CollectionPagingTelemetryLabel.NoCommandCategory,
                CollectionPagingTelemetryLabel.EarlyEmptyOutcome,
                (int?)skipped.Ranges.Count
            ),
            PartitionSuccess success => (
                CollectionPagingTelemetryLabel.BoundaryCommandCategory,
                CollectionPagingTelemetryLabel.SuccessOutcome,
                success.Ranges.Count
            ),
            PartitionFailureNotImplemented => FailureClassification(
                CollectionPagingTelemetryLabel.NotImplementedOutcome
            ),
            PartitionFailureSecurityConfiguration => FailureClassification(
                CollectionPagingTelemetryLabel.SecurityConfigurationOutcome
            ),
            PartitionFailureNamespaceNotAuthorized => FailureClassification(
                CollectionPagingTelemetryLabel.NotAuthorizedOutcome
            ),
            PartitionFailureRetryable => FailureClassification(
                CollectionPagingTelemetryLabel.RetryExhaustedOutcome
            ),
            _ => FailureClassification(CollectionPagingTelemetryLabel.UnknownFailureOutcome),
        };

        Record(
            requestInfo,
            duration,
            requestedPartitionCount,
            commandCategory,
            outcome,
            returnedPartitionCount
        );
    }

    /// <summary>
    /// Emits one measurement set, and never lets a telemetry fault reach the client.
    /// </summary>
    /// <remarks>
    /// Instrumentation observes; it must not participate. Every call arrives after the response has been
    /// assembled, and the execution-exception call arrives from inside a catch that is about to rethrow,
    /// so an escaping throw would either replace a served response with a system error or replace the
    /// fault it was trying to report — the same reason the requested count above is resolved before
    /// timing starts rather than from inside that catch. Recording is not free of throwing code: label
    /// derivation rejects a request state the bounded dimension set cannot describe, and an instrument
    /// invokes whatever measurement callbacks the host has subscribed, which is third-party code on this
    /// thread.
    /// <para>
    /// Validation on the recording side stays fail-fast: it can only report a defect in this handler,
    /// and the tests are what catch those.
    /// </para>
    /// </remarks>
    private void Record(
        RequestInfo requestInfo,
        TimeSpan duration,
        int requestedPartitionCount,
        string commandCategory,
        string outcome,
        int? returnedPartitionCount
    )
    {
        try
        {
            _collectionPagingTelemetry.RecordPartitions(
                CollectionPagingTelemetryContext.ForPagingMode(
                    CollectionPagingTelemetryLabel.PartitionPagingMode,
                    commandCategory,
                    requestInfo.MappingSet?.Key.Dialect,
                    outcome
                ),
                duration,
                requestedPartitionCount,
                returnedPartitionCount
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

    /// <summary>
    /// Every failure carries no command category. Core cannot prove, for most of them, whether the
    /// boundary command ran, and attributing a command shape — and therefore a duration — to a request
    /// that may never have issued that command is the failure mode the dimension exists to avoid.
    /// </summary>
    private static (
        string CommandCategory,
        string Outcome,
        int? ReturnedPartitionCount
    ) FailureClassification(string outcome) =>
        (CollectionPagingTelemetryLabel.NoCommandCategory, outcome, null);

    /// <summary>
    /// Encodes each boundary range as a page token. Always <c>application/json</c> and never a profile
    /// media type: the body carries tokens, not documents, so no readable profile can shape it. No
    /// <c>Total-Count</c> and no <c>Next-Page-Token</c>, because a boundary set is not a page and has
    /// no successor.
    /// </summary>
    /// <remarks>
    /// Every token is stamped with the anchor the boundaries were computed against, so a client whose
    /// replay resolves a different anchor — from its change-version window or from the data store
    /// serving it — is rejected rather than served bounds read from the wrong column. A replay that
    /// resolves the same anchor is served.
    /// </remarks>
    private static FrontendResponse CreateSuccessResponse(
        PartitionSuccess success,
        PageOrderingMode orderingMode
    )
    {
        JsonArray pageTokens = [];

        foreach (var range in success.Ranges)
        {
            pageTokens.Add(PageTokenCodec.Encode(range, orderingMode));
        }

        return new FrontendResponse(
            StatusCode: 200,
            Body: new JsonObject { [PageTokensMember] = pageTokens },
            Headers: [],
            ContentType: "application/json"
        );
    }

    /// <summary>
    /// Builds the backend request. The minimum partition size comes from the configured maximum page
    /// size rather than from request state: <see cref="RequestInfo.PaginationParameters" /> is never
    /// assigned on this pipeline, and the size a partition is measured against must be the same page
    /// size a walk of that partition will use.
    /// </summary>
    private IPartitionRequest CreatePartitionRequest(RequestInfo requestInfo, int requestedPartitionCount)
    {
        var mappingSet = RequireMappingSet(requestInfo, "partitions");

        return new RelationalPartitionRequest(
            ResourceInfo: requestInfo.ResourceInfo,
            AuthorizationContext: RelationalAuthorizationContext.Create(
                requestInfo.ClientAuthorizations,
                requestInfo.ApplicationContext?.CreatorOwnershipTokenId,
                requestInfo.ApplicationContext?.OwnershipTokenIds
            ),
            MappingSet: mappingSet,
            QueryElements: requestInfo.QueryElements,
            AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
            RequestedPartitionCount: requestedPartitionCount,
            MinimumPartitionSize: CursorPagingLimits.MinimumPartitionSize(_maximumPageSize),
            TraceId: requestInfo.FrontendRequest.TraceId,
            ChangeVersionRange: requestInfo.ChangeVersionRange,
            PageOrderingMode: requestInfo.PageOrderingMode,
            TenantKey: requestInfo.FrontendRequest.Tenant ?? string.Empty
        );
    }

    /// <summary>
    /// The validated count reaching this handler. Absent means the validating middleware did not run or
    /// did not accept the request, which is a pipeline composition fault rather than a client one, so it
    /// throws instead of quietly substituting a default this handler would have no basis to choose.
    /// </summary>
    private static int RequireRequestedPartitionCount(RequestInfo requestInfo) =>
        requestInfo.RequestedPartitionCount
        ?? throw new InvalidOperationException(
            "A validated partition count is required before executing partitions requests. Ensure "
                + "ValidatePartitionQueryMiddleware runs before the partitions handler."
        );
}
