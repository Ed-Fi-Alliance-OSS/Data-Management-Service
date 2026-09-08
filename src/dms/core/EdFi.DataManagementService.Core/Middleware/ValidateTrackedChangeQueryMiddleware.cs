// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
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

        if (requestInfo.QueryElements.Length == 0)
        {
            await next();
            return;
        }

        string[] errors = requestInfo
            .QueryElements.Select(queryElement =>
                $"The query field '{queryElement.QueryFieldName}' is not valid for this Change Query endpoint."
            )
            .ToArray();

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
}
