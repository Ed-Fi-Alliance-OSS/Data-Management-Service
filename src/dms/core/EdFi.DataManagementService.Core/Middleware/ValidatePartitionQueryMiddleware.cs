// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
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
/// <remarks>
/// The GET-many counterpart of this step validates paging first, because a paging fault is the first
/// thing wrong with a page request. This operation has no page, so the order is filters, then the
/// change-version window, then the partition parameters. Filters run first because the reserved paging
/// names are excluded from filter matching, and excluding them is what lets the partition phase report
/// <c>?limit=5</c> as a parameter that does not apply here rather than as an unknown query field.
/// <para>
/// A consequence worth stating: a request carrying both an unknown field and a reserved paging
/// parameter is answered with the unknown-field message alone. Both are client mistakes, and answering
/// the field first keeps this operation's unknown-field behavior identical to GET-many's.
/// </para>
/// </remarks>
internal class ValidatePartitionQueryMiddleware(ILogger _logger, int _defaultPartitionCount) : IPipelineStep
{
    /// <summary>
    /// The parameter names this operation owns, matched case-sensitively. The five reserved paging
    /// names are matched the way <see cref="ValidateQueryMiddleware" /> parses them, and the count is
    /// matched the way <see cref="PartitionRequestValidator" /> looks it up.
    /// </summary>
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

        ResourceQueryFilterResult filterResult = ResourceQueryFilterValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters,
            requestInfo.ResourceSchema.QueryFields.ToArray(),
            ordinalExcludedNames: _ordinalOwnedParameters,
            ignoreCaseExcludedNames: ChangeVersionParameterValidator.ReservedParameterNames
        );

        switch (filterResult)
        {
            case ResourceQueryFilterResult.UnknownQueryField unknownQueryField:
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

        // Both parameter faults below answer with the same shell, so they share one construction. The
        // media type is not stated here at all; it comes from the FrontendResponse default.
        FrontendResponse ParameterValidationFailed(string[] errors) =>
            new(
                StatusCode: 400,
                Body: ForParameterValidation(errors, requestInfo.FrontendRequest.TraceId),
                Headers: []
            );

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

        // The single accepting exit, and the only place any of the three reach request state. A
        // request rejected by any phase above carries none of them, so a handler cannot act on a
        // filter, a window, or a count that this step declined to accept. CollectionPaging is
        // deliberately never assigned on this pipeline: a partitions request has no page.
        requestInfo.QueryElements = ((ResourceQueryFilterResult.Valid)filterResult).QueryElements;
        requestInfo.ChangeVersionRange = changeVersionResult.Range;
        requestInfo.RequestedPartitionCount =
            partitionResult.RequestedPartitionCount ?? _defaultPartitionCount;

        await next();
    }
}
