// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates the resource-filter, change-version, and partition query parameters of a partitions
/// request.
/// </summary>
/// <param name="_defaultPartitionCount">
/// The configured partition count applied when the request omits its own. Supplied at composition
/// rather than read here, so the pipeline that serves partitions is the only place the default enters
/// request handling.
/// </param>
/// <param name="_useLegacyDocumentIdOrderingForChangeQueries">
/// The deployment-wide kill switch that restores <c>DocumentId</c> boundary ordering for every
/// change-version window. Supplied the same way and for the same reason as the count.
/// </param>
/// <remarks>
/// The GET-many counterpart of this step validates paging first, because a paging fault is the first
/// thing wrong with a page request. This operation has no page, so the order is the change-version
/// window, then filters, then the partition parameters. Two orderings are load-bearing. The window is
/// validated ahead of filters so that a request faulty in both ways is answered the same way GET-many
/// answers it, with the same problem type: these are sibling operations over one query string, and a
/// client that discriminates on type should not have to know which of the two it called. Filters are
/// validated ahead of the partition parameters because the reserved paging names are excluded from
/// filter matching, and excluding them is what lets the partition phase report <c>?limit=5</c> as a
/// parameter that does not apply here rather than as an unknown query field.
/// <para>
/// A consequence worth stating: a request carrying both an unknown field and a reserved paging
/// parameter is answered with the unknown-field message alone. Both are client mistakes, and answering
/// the field first keeps this operation's unknown-field behavior identical to GET-many's.
/// </para>
/// </remarks>
internal class ValidatePartitionQueryMiddleware(
    ILogger _logger,
    int _defaultPartitionCount,
    ICollectionPagingTelemetry _collectionPagingTelemetry,
    bool _useLegacyDocumentIdOrderingForChangeQueries
) : IPipelineStep
{
    /// <summary>
    /// Resolves the boundary anchor from the request's change-version window, with the same resolver
    /// the GET-many step uses. Sharing it is what makes a boundary set describe the same ordering a
    /// page of the same request would be selected in.
    /// </summary>
    private readonly ChangeQueryPageOrderingPolicy _orderingPolicy = new(
        _useLegacyDocumentIdOrderingForChangeQueries
    );

    /// <summary>
    /// The parameter names this operation owns, matched case-sensitively. The five reserved paging
    /// names are matched the way <see cref="ValidateQueryMiddleware" /> parses them, and the count is
    /// matched the way <see cref="PartitionRequestValidator" /> looks it up.
    /// </summary>
    /// <remarks>
    /// A name listed here is removed from filter matching, so it cannot also be filtered on. That is
    /// what makes a resource query field named <c>number</c> unfilterable on this operation while it
    /// stays filterable on the collection GET, which is the recorded consequence of serving the
    /// published count-parameter name. Nothing outside this list is reserved.
    /// </remarks>
    private static readonly string[] _ordinalOwnedParameters =
    [
        .. PartitionRequestValidator.ReservedParameters,
        PartitionRequestValidator.NumberParameter,
    ];

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidatePartitionQueryMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Both parameter faults answer with the same shell, so they share one construction, and counting
        // the rejection here covers both for the same reason. The media type is not stated here at all,
        // because it comes from the FrontendResponse default.
        FrontendResponse ParameterValidationFailed(string[] errors)
        {
            RecordValidationRejected(requestInfo);

            return new(
                StatusCode: 400,
                Body: ForParameterValidation(errors, requestInfo.FrontendRequest.TraceId),
                Headers: []
            );
        }

        ChangeVersionValidationResult changeVersionResult = ChangeVersionParameterValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters
        );

        if (changeVersionResult.Errors.Count > 0)
        {
            _logger.LogDebug(
                "Partition change-version parameter validation error - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = ParameterValidationFailed([.. changeVersionResult.Errors]);
            return;
        }

        ResourceQueryFilterResult filterResult = ResourceQueryFilterValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters,
            requestInfo.ResourceSchema.QueryFields.ToArray(),
            ordinalExcludedNames: _ordinalOwnedParameters,
            ignoreCaseExcludedNames: ChangeVersionParameterValidator.ReservedParameterNames
        );

        switch (filterResult)
        {
            case ResourceQueryFilterResult.UnknownQueryField unknownQueryField:
                RecordValidationRejected(requestInfo);

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: ForBadRequest(
                        "The request could not be processed. See 'errors' for details.",
                        requestInfo.FrontendRequest.TraceId,
                        [],
                        [
                            $@"The query field '{unknownQueryField.QueryFieldName}' is not valid for this resource.",
                        ]
                    ),
                    []
                );
                return;

            case ResourceQueryFilterResult.InvalidValues invalidValues:
                _logger.LogDebug(
                    "Partition query parameter format error - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );

                RecordValidationRejected(requestInfo);

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: ForDataValidation(
                        "Data validation failed. See 'validationErrors' for details.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        invalidValues.ValidationErrors,
                        []
                    ),
                    Headers: []
                );
                return;

            case ResourceQueryFilterResult.Valid:
                break;

            default:
                throw new InvalidOperationException(
                    $"ValidatePartitionQueryMiddleware received an unhandled resource query filter "
                        + $"result '{filterResult.GetType().Name}'."
                );
        }

        PartitionValidationResult partitionResult = PartitionRequestValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters
        );

        if (partitionResult.Errors.Count > 0)
        {
            _logger.LogDebug(
                "Partition parameter validation error - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = ParameterValidationFailed([.. partitionResult.Errors]);
            return;
        }

        // The single accepting exit, and the only place any of the four reach request state. A
        // request rejected by any phase above carries none of them, so a handler cannot act on a
        // filter, a window, an anchor, or a count that this step declined to accept. CollectionPaging
        // is deliberately never assigned on this pipeline: a partitions request has no page.
        requestInfo.QueryElements = ((ResourceQueryFilterResult.Valid)filterResult).QueryElements;
        requestInfo.ChangeVersionRange = changeVersionResult.Range;
        requestInfo.PageOrderingMode = _orderingPolicy.ResolveForLiveQuery(changeVersionResult.Range);
        requestInfo.RequestedPartitionCount =
            partitionResult.RequestedPartitionCount ?? _defaultPartitionCount;

        await next();
    }

    /// <summary>
    /// Counts a request this step answered. The paging mode is the partition literal on every exit:
    /// this step is composed only into the partitions pipeline, so there is no other mode a request
    /// reaching it could have.
    /// </summary>
    /// <remarks>
    /// A telemetry fault never reaches the client. Counting runs ahead of the rejection this step is
    /// about to answer with, so an escaping throw would replace a 400 that names what the client got
    /// wrong with a system error that names nothing. Recording is not free of throwing code: an
    /// instrument invokes whatever measurement callbacks the host has subscribed, which is third-party
    /// code on this thread.
    /// </remarks>
    private void RecordValidationRejected(RequestInfo requestInfo)
    {
        try
        {
            _collectionPagingTelemetry.RecordValidationRejected(
                CollectionPagingTelemetryContext.ForPagingMode(
                    CollectionPagingTelemetryLabel.PartitionPagingMode,
                    CollectionPagingTelemetryLabel.NoCommandCategory,
                    requestInfo.MappingSet?.Key.Dialect,
                    CollectionPagingTelemetryLabel.ValidationRejectedOutcome
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Collection paging telemetry was not recorded for a validation rejection - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
        }
    }
}
