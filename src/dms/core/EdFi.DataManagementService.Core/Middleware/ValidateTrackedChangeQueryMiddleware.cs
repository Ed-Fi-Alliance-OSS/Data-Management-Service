// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal sealed class ValidateTrackedChangeQueryMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateTrackedChangeQueryMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Cursor parameter recognition is operation-scoped: these names are not globally reserved,
        // and a Change Query endpoint must reject them rather than silently discard them. Silently
        // accepting a pageToken here would let a client believe it was walking a cursor when it was
        // re-reading page one. Query validation excludes them from resource-field matching, so they
        // never become QueryElements and are reported here by name.
        string[] cursorParameterErrors =
        [
            .. CursorRequestValidator
                .CursorParameters.Where(requestInfo.FrontendRequest.QueryParameters.ContainsKey)
                .Select(InvalidQueryFieldError),
        ];

        if (cursorParameterErrors.Length == 0 && requestInfo.QueryElements.Length == 0)
        {
            await next();
            return;
        }

        string[] errors =
        [
            .. cursorParameterErrors,
            .. requestInfo.QueryElements.Select(queryElement =>
                InvalidQueryFieldError(queryElement.QueryFieldName)
            ),
        ];

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 400,
            Body: FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                requestInfo.FrontendRequest.TraceId,
                [],
                errors
            ),
            Headers: []
        );
    }

    private static string InvalidQueryFieldError(string queryFieldName) =>
        $"The query field '{queryFieldName}' is not valid for this Change Query endpoint.";
}
