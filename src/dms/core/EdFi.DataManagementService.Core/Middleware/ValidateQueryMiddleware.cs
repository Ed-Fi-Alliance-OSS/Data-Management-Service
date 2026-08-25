// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.External.Model;
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
/// Validates the paging, change-version, and resource-filter query parameters of a request.
/// </summary>
/// <param name="_cursorParametersRecognized">
/// Whether this pipeline's operation supports cursor paging. True for live GET-many, false for
/// Change Query endpoints, which must reject the cursor parameters rather than acquire them.
/// Recognition is a property of pipeline composition rather than something inferred at run time, so
/// a reader can see at the composition site which operations page by cursor.
/// </param>
/// <param name="_collectionPagingTelemetry">
/// Where a rejection is counted. Required rather than defaulted to the no-op because this step is
/// composed into the Change Query pipeline as well, whose endpoints do not page by cursor and whose
/// faults are therefore not collection-paging events. A required parameter makes that second site a
/// compile error that forces an explicit choice, where a default would quietly pick one.
/// </param>
/// <param name="_useLegacyDocumentIdOrderingForChangeQueries">
/// The deployment-wide kill switch that restores <c>DocumentId</c> page-selection ordering for every
/// change-version window. Supplied at composition rather than read here, so the configured value
/// enters request handling at the pipeline that serves the request.
/// </param>
internal class ValidateQueryMiddleware(
    ILogger _logger,
    int _maximumPageSize,
    bool _cursorParametersRecognized,
    ICollectionPagingTelemetry _collectionPagingTelemetry,
    bool _useLegacyDocumentIdOrderingForChangeQueries
) : IPipelineStep
{
    /// <summary>
    /// Resolves the page anchor from the request's change-version window. Core owns that resolution
    /// because both sides of the request need the anchor and there is only one rule: this step stamps
    /// it on the request, and the backend compiles page selection against the column it names.
    /// </summary>
    private readonly ChangeQueryPageOrderingPolicy _orderingPolicy = new(
        _useLegacyDocumentIdOrderingForChangeQueries
    );

    /// <summary>
    /// The paging names this operation parses and excludes from filter matching. Spelled from the
    /// constants the cursor validator reads, because <see cref="Paging.PartitionRequestValidator" />
    /// reserves the same five names from the same constants: matching filters over a different set of
    /// names than /partitions rejects would let one operation filter on a resource property the other
    /// treats as paging.
    /// </summary>
    private static readonly string[] _paginationQueryParameters =
    [
        CursorRequestValidator.LimitParameter,
        CursorRequestValidator.OffsetParameter,
        CursorRequestValidator.TotalCountParameter,
    ];

    /// <summary>
    /// Finds and sets PaginationParameters on the requestInfo by parsing the client request.
    /// Returns any errors found for those parameters.
    /// </summary>
    private static List<string> SetPaginationParametersOn(RequestInfo requestInfo, int maxPageSize)
    {
        int? offset = null;
        int? limit = null;
        bool totalCount = false;
        List<string> errors = [];

        if (requestInfo.FrontendRequest.QueryParameters.ContainsKey("offset"))
        {
            if (
                !int.TryParse(requestInfo.FrontendRequest.QueryParameters["offset"], out int offsetVal)
                || offsetVal < 0
            )
            {
                errors.Add("Offset must be a numeric value greater than or equal to 0.");
            }
            else
            {
                offset = offsetVal;
            }
        }

        if (requestInfo.FrontendRequest.QueryParameters.ContainsKey("limit"))
        {
            if (
                !int.TryParse(requestInfo.FrontendRequest.QueryParameters["limit"], out int limitVal)
                || limitVal < 0
                || limitVal > maxPageSize
            )
            {
                errors.Add($"Limit must be omitted or set to a numeric value between 0 and {maxPageSize}.");
            }
            else
            {
                limit = limitVal;
            }
        }

        // Unlike offset and limit, a parsed value needs no separate assignment: TryParse writes it
        // straight to totalCount, and a failed parse writes false, which is also the value used when
        // the parameter is omitted. The error recorded below stops the parameters being applied at all.
        if (
            requestInfo.FrontendRequest.QueryParameters.ContainsKey("totalCount")
            && !bool.TryParse(requestInfo.FrontendRequest.QueryParameters["totalCount"], out totalCount)
        )
        {
            errors.Add("TotalCount must be a boolean value.");
        }

        if (errors.Count == 0)
        {
            requestInfo.PaginationParameters = new PaginationParameters(
                limit,
                offset,
                totalCount,
                maxPageSize
            );
        }
        return errors;
    }

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateQueryMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // All three parameter faults below - cursor, traditional pagination, and change-version -
        // answer with the same shell, so they share one construction rather than three copies that
        // have to be kept in step, and counting the rejection here covers all three for the same
        // reason. The media type is not stated here at all, because it comes from the FrontendResponse
        // default.
        FrontendResponse ParameterValidationFailed(string[] errors)
        {
            RecordValidationRejected(requestInfo);

            return new(
                StatusCode: 400,
                Body: ForParameterValidation(errors, requestInfo.FrontendRequest.TraceId),
                Headers: []
            );
        }

        // The change-version window is parsed ahead of every paging check because the page anchor is
        // resolved from it, and cursor validation needs the anchor: a continuation token carries the
        // anchor it was issued for, and a token whose anchor disagrees with this request's window is
        // not replayable. Only the parsing moves. This result's errors are still reported from the
        // position they were reported from before - after cursor and traditional pagination faults -
        // so the messages a client sees, and their precedence, are unchanged.
        ChangeVersionValidationResult changeVersionResult = ChangeVersionParameterValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters
        );

        // Resolved from the parsed window rather than from parameter presence: '?maxChangeVersion='
        // parses to null, so it is not a max-bearing window and does not move the anchor. An
        // unparseable maximum parses to null for the same reason, which makes the anchor of a request
        // rejected below deterministic rather than dependent on the faulty value.
        PageOrderingMode pageOrderingMode = _orderingPolicy.ResolveForLiveQuery(changeVersionResult.Range);

        // A request that supplied either cursor parameter is validated by the cursor precedence.
        // Everything else keeps the traditional parsing and its existing messages. Both paths answer
        // a pagination fault with the parameter-validation shell, matching ODS/API.
        CursorValidationResult cursorResult = _cursorParametersRecognized
            ? CursorRequestValidator.Validate(
                requestInfo.FrontendRequest.QueryParameters,
                _maximumPageSize,
                pageOrderingMode
            )
            : CursorValidationResult.NotCursorRequest.Instance;

        if (cursorResult is CursorValidationResult.Invalid invalidCursorRequest)
        {
            _logger.LogDebug(
                "Cursor parameter validation error - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = ParameterValidationFailed([invalidCursorRequest.Error]);
            return;
        }

        // Determined here, but the typed collection-paging choice reaches request state only at the
        // accepting exit below, its single assignment site: the change-version and query-field steps
        // that follow can still answer the request, and a request they reject must not carry a paging
        // mode a handler could act on. That deferral covers the typed choice. The traditional branch
        // assigns pagination parameters as soon as they parse cleanly, which is ahead of those later
        // steps, so a request one of them rejects does keep parsed pagination parameters.
        CollectionPaging collectionPaging;

        if (cursorResult is CursorValidationResult.Valid validCursorRequest)
        {
            collectionPaging = validCursorRequest.Paging;
        }
        else
        {
            List<string> errors = SetPaginationParametersOn(requestInfo, _maximumPageSize);

            if (errors.Count > 0)
            {
                _logger.LogDebug(
                    "'{Status}'.'{EndpointName}' - {TraceId}",
                    "400",
                    requestInfo.PathComponents.EndpointName,
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = ParameterValidationFailed([.. errors]);
                return;
            }

            collectionPaging = new CollectionPaging.Traditional(requestInfo.PaginationParameters);
        }

        if (changeVersionResult.Errors.Count > 0)
        {
            _logger.LogDebug(
                "Change-version parameter validation error - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = ParameterValidationFailed([.. changeVersionResult.Errors]);
            return;
        }

        // The window and the anchor it resolves to are assigned together: the anchor is a function of
        // the window, so a request carrying one without the other would describe a page selection that
        // cannot exist.
        requestInfo.ChangeVersionRange = changeVersionResult.Range;
        requestInfo.PageOrderingMode = pageOrderingMode;

        // Pagination parameters are matched case-sensitively, consistent with how they are
        // parsed above; change-version parameters are matched case-insensitively, consistent
        // with how the validator looks them up.
        //
        // The cursor parameters are excluded in both modes, for different reasons. Where they are
        // recognized, the cursor validation above has already consumed them. Where they are not,
        // excluding them here is what lets the Change Query step reject them by name instead of the
        // filter validator answering first with the resource-field wording. Excluding a name is not
        // accepting it: an operation that does not recognize these rejects them in the step that
        // follows.
        ResourceQueryFilterResult filterResult = ResourceQueryFilterValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters,
            requestInfo.ResourceSchema.QueryFields.ToArray(),
            ordinalExcludedNames: [.. _paginationQueryParameters, .. CursorRequestValidator.CursorParameters],
            ignoreCaseExcludedNames: ChangeVersionParameterValidator.ReservedParameterNames
        );

        switch (filterResult)
        {
            case ResourceQueryFilterResult.UnknownQueryField unknownQueryField:
                RecordValidationRejected(requestInfo);

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
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
                    "Query parameter format error - {TraceId}",
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

            case ResourceQueryFilterResult.Valid valid:
                requestInfo.CollectionPaging = collectionPaging;
                requestInfo.QueryElements = valid.QueryElements;

                await next();
                return;

            default:
                throw new InvalidOperationException(
                    $"ValidateQueryMiddleware received an unhandled resource query filter result "
                        + $"'{filterResult.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Counts a request this step answered. No duration is recorded: nothing executed.
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
                    RejectedPagingMode(requestInfo),
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

    /// <summary>
    /// The paging mode of a request this step is rejecting, read from the query string rather than from
    /// request state.
    /// </summary>
    /// <remarks>
    /// <see cref="RequestInfo.CollectionPaging" /> is assigned only at the accepting exit, so a rejected
    /// cursor request still carries the traditional default and would be counted as traditional traffic.
    /// The same constant array the cursor validator reads is used here, so the two cannot disagree about
    /// what makes a request a cursor request.
    /// </remarks>
    private static string RejectedPagingMode(RequestInfo requestInfo) =>
        Array.Exists(
            CursorRequestValidator.CursorParameters,
            requestInfo.FrontendRequest.QueryParameters.ContainsKey
        )
            ? CollectionPagingTelemetryLabel.CursorPagingMode
            : CollectionPagingTelemetryLabel.TraditionalPagingMode;
}
