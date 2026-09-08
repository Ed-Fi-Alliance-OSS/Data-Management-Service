// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that a resolved resource route is being used with the correct write semantics.
/// Collection routes allow POST, while item routes allow PUT and DELETE.
/// </summary>
internal class ValidateRouteSemanticsMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateRouteSemanticsMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        string? error = (requestInfo.Method, requestInfo.PathComponents.HasDocumentUuidSegment) switch
        {
            (RequestMethod.DELETE, false) =>
                "Resource collections cannot be deleted. To delete a specific item, use DELETE and include the 'id' in the route.",
            (RequestMethod.PUT, false) =>
                "Resource collections cannot be replaced. To 'upsert' an item in the collection, use POST. To update a specific item, use PUT and include the 'id' in the route.",
            (RequestMethod.POST, true) =>
                "Resource items can only be updated using PUT. To 'upsert' an item in the resource collection using POST, remove the 'id' from the route.",
            _ => null,
        };

        if (error is null)
        {
            await next();
            return;
        }

        _logger.LogDebug(
            "ValidateRouteSemanticsMiddleware: Invalid route semantics for request method {Method} - {TraceId}",
            requestInfo.Method,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForMethodNotAllowed([error], requestInfo.FrontendRequest.TraceId),
            Headers: [],
            ContentType: "application/json; charset=utf-8"
        );
    }
}
