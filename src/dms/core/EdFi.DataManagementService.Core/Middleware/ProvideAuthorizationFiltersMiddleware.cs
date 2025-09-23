// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Provides authorization filters
/// </summary>
internal class ProvideAuthorizationFiltersMiddleware(
    IAuthorizationServiceFactory _authorizationServiceFactory,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ProvideAuthorizationFiltersMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            // Check if ClientAuthorizations has been populated by JWT middleware
            if (requestInfo.ClientAuthorizations == No.ClientAuthorizations)
            {
                _logger.LogWarning(
                    "ProvideAuthorizationFiltersMiddleware: No ClientAuthorizations found - JWT authentication may have failed - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: (int)HttpStatusCode.Unauthorized,
                    Body: FailureResponse.ForUnauthorized(
                        requestInfo.FrontendRequest.TraceId,
                        error: "Unauthorized",
                        description: "No authorization information found. Ensure valid JWT token is provided."
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            }

            List<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators = [];
            foreach (string authorizationStrategy in requestInfo.ResourceActionAuthStrategies)
            {
                var authFiltersProvider =
                    _authorizationServiceFactory.GetByName<IAuthorizationFiltersProvider>(
                        authorizationStrategy
                    );
                if (authFiltersProvider == null)
                {
                    requestInfo.FrontendResponse = new FrontendResponse(
                        StatusCode: (int)HttpStatusCode.Forbidden,
                        Body: FailureResponse.ForForbidden(
                            traceId: requestInfo.FrontendRequest.TraceId,
                            errors:
                            [
                                $"Could not find authorization filters implementation for the following strategy: '{authorizationStrategy}'.",
                            ]
                        ),
                        Headers: [],
                        ContentType: "application/problem+json"
                    );
                    return;
                }

                authorizationStrategyEvaluators.Add(
                    authFiltersProvider.GetFilters(requestInfo.ClientAuthorizations)
                );
            }

            requestInfo.AuthorizationStrategyEvaluators = [.. authorizationStrategyEvaluators];
        }
        catch (AuthorizationException ex)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.Forbidden,
                Body: FailureResponse.ForForbidden(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    errors: [ex.Message]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );

            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ProvideAuthorizationFiltersMiddleware: Error while authorizing the request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: new JsonObject
                {
                    ["message"] = $"Error while authorizing the request.{ex.Message}",
                    ["traceId"] = requestInfo.FrontendRequest.TraceId.Value,
                },
                Headers: []
            );

            return;
        }

        await next();
    }
}
