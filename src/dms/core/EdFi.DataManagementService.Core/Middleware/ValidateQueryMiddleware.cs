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
internal class ValidateQueryMiddleware(
    ILogger _logger,
    int _maximumPageSize,
    bool _cursorParametersRecognized
) : IPipelineStep
{
    private static readonly string[] _paginationQueryParameters = ["limit", "offset", "totalCount"];

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
        // have to be kept in step. The media type is not stated here at all; it comes from the
        // FrontendResponse default.
        FrontendResponse ParameterValidationFailed(string[] errors) =>
            new(
                StatusCode: 400,
                Body: ForParameterValidation(errors, requestInfo.FrontendRequest.TraceId),
                Headers: []
            );

        // A request that supplied either cursor parameter is validated by the cursor precedence.
        // Everything else keeps the traditional parsing and its existing messages. Both paths answer
        // a pagination fault with the parameter-validation shell, matching ODS/API.
        CursorValidationResult cursorResult = _cursorParametersRecognized
            ? CursorRequestValidator.Validate(requestInfo.FrontendRequest.QueryParameters, _maximumPageSize)
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

        ChangeVersionValidationResult changeVersionResult = ChangeVersionParameterValidator.Validate(
            requestInfo.FrontendRequest.QueryParameters
        );

        if (changeVersionResult.Errors.Count > 0)
        {
            _logger.LogDebug(
                "Change-version parameter validation error - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = ParameterValidationFailed([.. changeVersionResult.Errors]);
            return;
        }

        requestInfo.ChangeVersionRange = changeVersionResult.Range;

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
}
