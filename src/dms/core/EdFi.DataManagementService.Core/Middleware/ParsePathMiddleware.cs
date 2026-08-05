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
/// Parses and validates the path from the frontend is well-formed. Adds PathComponents
/// to the requestInfo if it is.
/// </summary>
internal class ParsePathMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ParsePathMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        switch (ResourcePathParser.Parse(requestInfo.FrontendRequest.Path))
        {
            case ResourcePathParseResult.Unmatched:
                _logger.LogDebug(
                    "ParsePathMiddleware: Not a valid path - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 404,
                    Body: FailureResponse.ForNotFound(
                        "The specified data could not be found.",
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;

            case ResourcePathParseResult.InvalidIdentifier invalidIdentifier:
                RespondWithInvalidIdentifier(requestInfo, invalidIdentifier.SuppliedSegment);
                return;

            case ResourcePathParseResult.Recognized recognized:
                requestInfo.PathComponents = recognized.PathComponents;

                if (recognized.PathComponents.Operation is ResourcePathOperation.Partitions)
                {
                    // The partitions pipeline does not exist yet. Until it does, a recognized
                    // partitions operation is answered exactly as an unrecognized third segment is,
                    // so no incomplete partitions surface is exposed. The classification is still
                    // applied to the request above, so the operation this pipeline declines to serve
                    // is the one recorded in request state.
                    RespondWithInvalidIdentifier(requestInfo, recognized.SuppliedOperationSegment!);
                    return;
                }

                await next();
                return;

            default:
                throw new InvalidOperationException(
                    "ParsePathMiddleware received an unhandled resource path parse result."
                );
        }
    }

    private void RespondWithInvalidIdentifier(RequestInfo requestInfo, string suppliedSegment)
    {
        _logger.LogDebug(
            "ParsePathMiddleware: Not a valid document UUID - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 400,
            Body: FailureResponse.ForDataValidation(
                detail: "Data validation failed. See 'validationErrors' for details.",
                traceId: requestInfo.FrontendRequest.TraceId,
                validationErrors: new Dictionary<string, string[]>
                {
                    { "$.id", new[] { $"The value '{suppliedSegment}' is not valid." } },
                },
                errors: []
            ),
            Headers: []
        );
    }
}
